import 'dart:async';
import 'dart:io';

import 'package:collection/collection.dart';
// `Category` is a domain term here; flutter/foundation exports an annotation
// of the same name that would otherwise shadow it.
import 'package:flutter/foundation.dart' hide Category;
import 'package:path/path.dart' as p;
import 'package:ulid/ulid.dart';
import 'package:watcher/watcher.dart';

import '../domain/calendar_date.dart';
import '../domain/rank.dart';
import '../domain/status.dart';
import '../domain/task.dart';
import '../domain/workspace_config.dart';
import '../storage/task_store.dart';
import '../storage/workspace.dart';

/// Holds the whole workspace in memory and is the only thing that writes to it.
///
/// Everything lives in memory because the storage model is one small JSON file
/// per task — see `docs/adr/0001-file-per-task-json-storage.md`. That's
/// comfortable into the low thousands of tasks and is what lets filtering and
/// search stay instant without an index.
class WorkspaceController extends ChangeNotifier {
  final TaskStore store;
  Workspace get workspace => store.workspace;

  WorkspaceConfig _config = WorkspaceConfig.seed();
  final Map<String, Task> _tasks = {};
  List<ConflictFile> _conflicts = const [];
  List<TaskLoadFailure> _failures = const [];
  bool _loading = true;

  StreamSubscription<WatchEvent>? _watch;
  Timer? _reloadDebounce;

  /// Paths this app wrote recently, so its own writes don't bounce back through
  /// the watcher as external changes.
  final Map<String, DateTime> _selfWrites = {};
  static const Duration _selfWriteWindow = Duration(seconds: 3);

  /// Whether to watch the tasks folder for external changes.
  ///
  /// Off in tests: the watcher schedules real periodic timers, which
  /// `pumpAndSettle` would wait on forever.
  final bool watchForChanges;

  WorkspaceController(this.store, {this.watchForChanges = true});

  bool get isLoading => _loading;
  WorkspaceConfig get config => _config;
  List<ConflictFile> get conflicts => _conflicts;
  List<TaskLoadFailure> get failures => _failures;

  List<Task> get tasks => _tasks.values.toList(growable: false);

  Task? taskById(String id) => _tasks[id];

  // ---------------------------------------------------------------- lifecycle

  Future<void> open() async {
    _loading = true;
    notifyListeners();

    final loaded = await store.load();
    _config = loaded.config;
    _tasks
      ..clear()
      ..addEntries(loaded.tasks.map((t) => MapEntry(t.id, t)));
    _conflicts = loaded.conflicts;
    _failures = loaded.failures;
    _loading = false;
    notifyListeners();

    if (watchForChanges) await _startWatching();
  }

  @override
  void dispose() {
    _reloadDebounce?.cancel();
    _watch?.cancel();
    super.dispose();
  }

  /// Watches the tasks folder so edits made on another machine appear without a
  /// restart. Without this, leaving the app open on one machine and editing on
  /// another silently clobbers one side.
  Future<void> _startWatching() async {
    await _watch?.cancel();
    if (!await workspace.tasksDir.exists()) return;

    final watcher = DirectoryWatcher(workspace.tasksDir.path);
    _watch = watcher.events.listen(_onFileEvent, onError: (_) {
      // Watching is a convenience; losing it shouldn't take the app down.
    });
  }

  void _onFileEvent(WatchEvent event) {
    final name = p.basename(event.path);
    if (name.endsWith('.tmp')) return;

    final wroteAt = _selfWrites[event.path];
    if (wroteAt != null &&
        DateTime.now().difference(wroteAt) < _selfWriteWindow) {
      return;
    }

    // Sync clients write in bursts; collapse them into one reload.
    _reloadDebounce?.cancel();
    _reloadDebounce = Timer(const Duration(milliseconds: 400), _reloadFromDisk);
  }

  Future<void> _reloadFromDisk() async {
    final loaded = await store.load();
    _config = loaded.config;
    _tasks
      ..clear()
      ..addEntries(loaded.tasks.map((t) => MapEntry(t.id, t)));
    _conflicts = loaded.conflicts;
    _failures = loaded.failures;
    _prune();
    notifyListeners();
  }

  Future<void> refresh() => _reloadFromDisk();

  void _prune() {
    final cutoff = DateTime.now().subtract(_selfWriteWindow);
    _selfWrites.removeWhere((_, at) => at.isBefore(cutoff));
  }

  Future<void> _persist(Task task) async {
    _tasks[task.id] = task;
    _selfWrites[workspace.taskFile(task.id).path] = DateTime.now();
    notifyListeners();
    await store.saveTask(task);
  }

  // -------------------------------------------------------------- task writes

  Future<Task> createTask({
    required String summary,
    String? statusId,
    String description = '',
    String? categoryId,
    List<String> tagIds = const [],
    Priority priority = Priority.medium,
    CalendarDate? dueDate,
    int? estimateMinutes,
  }) async {
    final targetStatus = statusId ?? _config.defaultStatusId;
    final task = Task.create(
      summary: summary,
      statusId: targetStatus,
      boardRank: _rankForBottomOf(targetStatus),
      description: description,
      categoryId: categoryId,
      tagIds: tagIds.take(Task.maxTags).toList(),
      priority: priority,
      dueDate: dueDate,
      estimateMinutes: estimateMinutes,
    );
    await _persist(task);
    return task;
  }

  Future<void> updateTask(Task task) => _persist(task);

  /// Moves a task to [statusId], applying the completion-date rule.
  ///
  /// The completion date is derived from the status *type*, not from any
  /// particular status: entering any `Final` status stamps it, leaving one
  /// clears it. See `docs/adr/0002-...`.
  Future<void> moveToStatus(Task task, String statusId, {String? boardRank}) {
    final wasFinal = _config.isFinal(task.statusId);
    final isFinal = _config.isFinal(statusId);

    return _persist(task.copyWith(
      statusId: statusId,
      boardRank: boardRank,
      completedAt: isFinal
          ? (wasFinal ? task.completedAt : DateTime.now().toUtc())
          : null,
    ));
  }

  /// Places [task] in [statusId] between the two given board neighbours.
  /// Pass null for either neighbour to drop at the top or bottom of a column.
  Future<void> reorderOnBoard({
    required Task task,
    required String statusId,
    Task? above,
    Task? below,
  }) {
    final rank = rankBetween(above?.boardRank ?? '', below?.boardRank ?? '');
    return moveToStatus(task, statusId, boardRank: rank);
  }

  Future<void> addNote(Task task, String body) => _persist(
        task.copyWith(notes: [...task.notes, Note.create(body)]),
      );

  Future<void> deleteNote(Task task, String noteId) => _persist(
        task.copyWith(
          notes: task.notes.where((n) => n.id != noteId).toList(),
        ),
      );

  Future<void> addStep(Task task, String text) => _persist(
        task.copyWith(steps: [...task.steps, Step.create(text)]),
      );

  Future<void> setStepDone(Task task, String stepId, bool done) => _persist(
        task.copyWith(
          steps: task.steps
              .map((s) => s.id == stepId ? s.copyWith(done: done) : s)
              .toList(),
        ),
      );

  Future<void> deleteStep(Task task, String stepId) => _persist(
        task.copyWith(
          steps: task.steps.where((s) => s.id != stepId).toList(),
        ),
      );

  Future<void> addReminder(Task task, DateTime remindAt) => _persist(
        task.copyWith(
          reminders: [...task.reminders, Reminder.create(remindAt)],
        ),
      );

  Future<void> deleteReminder(Task task, String reminderId) => _persist(
        task.copyWith(
          reminders: task.reminders.where((r) => r.id != reminderId).toList(),
        ),
      );

  Future<void> markReminderFired(Task task, String reminderId) => _persist(
        task.copyWith(
          reminders: task.reminders
              .map((r) => r.id == reminderId
                  ? r.copyWith(firedAt: DateTime.now().toUtc())
                  : r)
              .toList(),
        ),
      );

  Future<void> dismissReminder(Task task, String reminderId) => _persist(
        task.copyWith(
          reminders: task.reminders
              .map((r) => r.id == reminderId
                  ? r.copyWith(dismissedAt: DateTime.now().toUtc())
                  : r)
              .toList(),
        ),
      );

  /// Copies a file into the workspace and binds it to the task. The copy is
  /// authoritative — the user's original can move or vanish afterwards.
  Future<void> attachFile(Task task, File source) async {
    await workspace.ensureLayout();
    final id = Ulid().toString();
    final storedName = '$id-${p.basename(source.path)}';
    final destination = workspace.attachmentFile(storedName);
    await source.copy(destination.path);

    await _persist(task.copyWith(attachments: [
      ...task.attachments,
      Attachment(
        id: id,
        originalName: p.basename(source.path),
        storedName: storedName,
        sizeBytes: await destination.length(),
        addedAt: DateTime.now().toUtc(),
      ),
    ]));
  }

  Future<void> deleteAttachment(Task task, String attachmentId) async {
    final attachment =
        task.attachments.where((a) => a.id == attachmentId).firstOrNull;
    if (attachment != null) {
      final file = workspace.attachmentFile(attachment.storedName);
      if (await file.exists()) await file.delete();
    }
    await _persist(task.copyWith(
      attachments: task.attachments.where((a) => a.id != attachmentId).toList(),
    ));
  }

  Future<void> trashTask(Task task) async {
    _tasks.remove(task.id);
    _selfWrites[workspace.taskFile(task.id).path] = DateTime.now();
    notifyListeners();
    await store.trashTask(task);
  }

  // ------------------------------------------------------------ config writes

  Future<void> _persistConfig(WorkspaceConfig config) async {
    _config = config;
    _selfWrites[workspace.configFile.path] = DateTime.now();
    notifyListeners();
    await store.saveConfig(config);
  }

  Future<Status> addStatus(String name, StatusType type) async {
    final status = Status(
      id: Ulid().toString(),
      name: name,
      type: type,
      order: _config.statuses.length,
    );
    await _persistConfig(
      _config.copyWith(statuses: [..._config.statuses, status]),
    );
    return status;
  }

  Future<void> updateStatus(Status status) => _persistConfig(
        _config.copyWith(
          statuses: _config.statuses
              .map((s) => s.id == status.id ? status : s)
              .toList(),
        ),
      );

  /// Reorders statuses to match [ordered], which is also the board's column
  /// order.
  Future<void> reorderStatuses(List<Status> ordered) => _persistConfig(
        _config.copyWith(
          statuses: [
            for (var i = 0; i < ordered.length; i++)
              ordered[i].copyWith(order: i),
          ],
        ),
      );

  int tasksUsingStatus(String statusId) =>
      _tasks.values.where((t) => t.statusId == statusId).length;

  /// Why a status can't be deleted, or null if it can.
  ///
  /// Deleting a status in use is blocked rather than cascading — tasks are
  /// never orphaned or destroyed as a side effect of a settings change.
  String? statusDeletionBlocker(String statusId) {
    final status = _config.statusById(statusId);
    if (status == null) return 'That status no longer exists.';
    if (statusId == _config.defaultStatusId) {
      return 'This is the status new tasks start in. Make another '
          'Initial status the default first.';
    }
    final remaining =
        _config.statuses.where((s) => s.type == status.type && s.id != statusId);
    if (remaining.isEmpty) {
      return 'This is the only ${status.type.label} status, and every '
          'workspace needs at least one of each type.';
    }
    return null;
  }

  /// Deletes [statusId], moving any tasks using it to [reassignTo].
  Future<void> deleteStatus(String statusId, {required String reassignTo}) async {
    final blocker = statusDeletionBlocker(statusId);
    if (blocker != null) throw StateError(blocker);
    if (_config.statusById(reassignTo) == null) {
      throw ArgumentError.value(reassignTo, 'reassignTo', 'Unknown status');
    }

    for (final task in _tasks.values.where((t) => t.statusId == statusId).toList()) {
      await moveToStatus(task, reassignTo);
    }

    final remaining =
        _config.statuses.where((s) => s.id != statusId).toList();
    await _persistConfig(_config.copyWith(
      statuses: [
        for (var i = 0; i < remaining.length; i++) remaining[i].copyWith(order: i),
      ],
    ));
  }

  Future<void> setDefaultStatus(String statusId) {
    final status = _config.statusById(statusId);
    if (status == null || status.type != StatusType.initial) {
      throw ArgumentError('New tasks must start in an Initial status');
    }
    return _persistConfig(_config.copyWith(defaultStatusId: statusId));
  }

  Future<Category> addCategory(String name, int color) async {
    final category =
        Category(id: Ulid().toString(), name: name, color: color);
    await _persistConfig(
      _config.copyWith(categories: [..._config.categories, category]),
    );
    return category;
  }

  Future<void> updateCategory(Category category) => _persistConfig(
        _config.copyWith(
          categories: _config.categories
              .map((c) => c.id == category.id ? category : c)
              .toList(),
        ),
      );

  int tasksUsingCategory(String categoryId) =>
      _tasks.values.where((t) => t.categoryId == categoryId).length;

  /// Deletes a category. Tasks using it move to [reassignTo], or lose their
  /// category entirely when that is null.
  Future<void> deleteCategory(String categoryId, {String? reassignTo}) async {
    for (final task
        in _tasks.values.where((t) => t.categoryId == categoryId).toList()) {
      await _persist(task.copyWith(categoryId: reassignTo));
    }
    await _persistConfig(_config.copyWith(
      categories:
          _config.categories.where((c) => c.id != categoryId).toList(),
    ));
  }

  Future<Tag> addTag(String name) async {
    final existing = _config.tags
        .where((t) => t.name.toLowerCase() == name.toLowerCase())
        .firstOrNull;
    if (existing != null) return existing;

    final tag = Tag(id: Ulid().toString(), name: name);
    await _persistConfig(_config.copyWith(tags: [..._config.tags, tag]));
    return tag;
  }

  Future<void> updateTag(Tag tag) => _persistConfig(
        _config.copyWith(
          tags: _config.tags.map((t) => t.id == tag.id ? tag : t).toList(),
        ),
      );

  /// Deletes a tag, detaching it from every task. Unlike statuses and
  /// categories this needs no prompt — tags are deliberately lightweight.
  Future<void> deleteTag(String tagId) async {
    for (final task
        in _tasks.values.where((t) => t.tagIds.contains(tagId)).toList()) {
      await _persist(task.copyWith(
        tagIds: task.tagIds.where((id) => id != tagId).toList(),
      ));
    }
    await _persistConfig(_config.copyWith(
      tags: _config.tags.where((t) => t.id != tagId).toList(),
    ));
  }

  // ------------------------------------------------------------------ helpers

  String _rankForBottomOf(String statusId) {
    final column = _tasks.values.where((t) => t.statusId == statusId).toList()
      ..sort((a, b) => a.boardRank.compareTo(b.boardRank));
    return rankBetween(column.isEmpty ? '' : column.last.boardRank, '');
  }
}
