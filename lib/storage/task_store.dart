import 'dart:io';

import 'package:path/path.dart' as p;

import '../domain/task.dart';
import '../domain/workspace_config.dart';
import 'workspace.dart';

/// Result of loading a workspace from disk, including anything that failed.
class LoadedWorkspace {
  final WorkspaceConfig config;
  final List<Task> tasks;

  /// Task files that couldn't be parsed. Reported rather than swallowed — a
  /// task that silently vanishes is worse than one that shows up as broken.
  final List<TaskLoadFailure> failures;

  final List<ConflictFile> conflicts;

  const LoadedWorkspace({
    required this.config,
    required this.tasks,
    required this.failures,
    required this.conflicts,
  });
}

class TaskLoadFailure {
  final String fileName;
  final Object error;

  const TaskLoadFailure(this.fileName, this.error);
}

/// Reads and writes the workspace. One JSON file per task; see
/// `docs/adr/0001-file-per-task-json-storage.md`.
class TaskStore {
  final Workspace workspace;

  TaskStore(this.workspace);

  /// Creates the folder layout and seeds `config.json` if this is a fresh
  /// workspace. Safe to call on an existing one.
  Future<WorkspaceConfig> initialise() async {
    await workspace.ensureLayout();
    final existing = await readJson(workspace.configFile);
    if (existing != null) return WorkspaceConfig.fromJson(existing);

    final seed = WorkspaceConfig.seed();
    await saveConfig(seed);
    return seed;
  }

  Future<void> saveConfig(WorkspaceConfig config) =>
      writeJsonAtomic(workspace.configFile, config.toJson());

  Future<LoadedWorkspace> load() async {
    final config = await initialise();

    final tasks = <Task>[];
    final failures = <TaskLoadFailure>[];

    await for (final entity in workspace.tasksDir.list()) {
      if (entity is! File) continue;
      final name = p.basename(entity.path);
      if (!isOwnTaskFile(name)) continue;

      try {
        final json = await readJson(entity);
        if (json == null) continue;
        tasks.add(Task.fromJson(json));
      } catch (error) {
        failures.add(TaskLoadFailure(name, error));
      }
    }

    return LoadedWorkspace(
      config: config,
      tasks: tasks,
      failures: failures,
      conflicts: await findConflictFiles(workspace),
    );
  }

  Future<Task?> loadTask(String taskId) async {
    final json = await readJson(workspace.taskFile(taskId));
    return json == null ? null : Task.fromJson(json);
  }

  Future<void> saveTask(Task task) =>
      writeJsonAtomic(workspace.taskFile(task.id), task.toJson());

  /// Moves a task's file into `.trash/`, along with its attachments.
  ///
  /// Deliberately not a `deleted` flag: keeping trashed tasks out of every
  /// query by construction means no filter can ever forget to exclude them.
  /// Nothing purges the trash automatically — silently destroying data on a
  /// timer is worse than a folder that grows.
  Future<void> trashTask(Task task) async {
    await workspace.ensureLayout();

    for (final attachment in task.attachments) {
      final file = workspace.attachmentFile(attachment.storedName);
      if (await file.exists()) {
        await _moveInto(file, workspace.trashAttachmentsDir);
      }
    }

    final file = workspace.taskFile(task.id);
    if (await file.exists()) {
      await _moveInto(file, workspace.trashTasksDir);
    }
  }

  /// Brings a trashed task back.
  Future<Task?> restoreTask(String taskId) async {
    final trashed = File(p.join(workspace.trashTasksDir.path, '$taskId.json'));
    if (!await trashed.exists()) return null;
    await _moveInto(trashed, workspace.tasksDir);
    return loadTask(taskId);
  }

  Future<List<Task>> loadTrash() async {
    if (!await workspace.trashTasksDir.exists()) return const [];
    final tasks = <Task>[];
    await for (final entity in workspace.trashTasksDir.list()) {
      if (entity is! File) continue;
      if (!isOwnTaskFile(p.basename(entity.path))) continue;
      try {
        final json = await readJson(entity);
        if (json != null) tasks.add(Task.fromJson(json));
      } catch (_) {
        // A broken file in the trash isn't worth reporting.
      }
    }
    return tasks;
  }

  Future<void> _moveInto(File file, Directory target) async {
    if (!await target.exists()) await target.create(recursive: true);
    var destination = p.join(target.path, p.basename(file.path));

    // Never clobber something already in the trash.
    if (await File(destination).exists()) {
      final stem = p.basenameWithoutExtension(file.path);
      final extension = p.extension(file.path);
      final stamp = DateTime.now().toUtc().millisecondsSinceEpoch;
      destination = p.join(target.path, '$stem-$stamp$extension');
    }

    try {
      await file.rename(destination);
    } on FileSystemException {
      // Renames can fail across volumes; fall back to copy-then-delete.
      await file.copy(destination);
      await file.delete();
    }
  }
}
