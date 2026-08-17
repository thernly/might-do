import 'package:ulid/ulid.dart';

import 'calendar_date.dart';

/// How important a task is relative to others. Fixed scale.
enum Priority {
  low('low', 'Low'),
  medium('medium', 'Medium'),
  high('high', 'High'),
  critical('critical', 'Critical');

  const Priority(this.wire, this.label);

  final String wire;
  final String label;

  static Priority fromWire(String wire) => values.firstWhere(
        (p) => p.wire == wire,
        orElse: () => throw FormatException('Unknown priority', wire),
      );

  /// Highest first, for default board and list ordering.
  int compareDescending(Priority other) => other.index.compareTo(index);
}

/// One tickable line in a task's ordered breakdown.
///
/// Deliberately not a task: no status, no dates, no board presence. Ticking
/// every step off does nothing automatically — it just shows `4/6` on the card.
class Step {
  final String id;
  final String text;
  final bool done;

  const Step({required this.id, required this.text, this.done = false});

  factory Step.create(String text) =>
      Step(id: Ulid().toString(), text: text);

  Step copyWith({String? text, bool? done}) => Step(
        id: id,
        text: text ?? this.text,
        done: done ?? this.done,
      );

  Map<String, dynamic> toJson() => {'id': id, 'text': text, 'done': done};

  static Step fromJson(Map<String, dynamic> json) => Step(
        id: json['id'] as String,
        text: json['text'] as String,
        done: json['done'] as bool? ?? false,
      );
}

/// A dated entry in a task's running commentary, written while work proceeds.
///
/// Distinct from the description, which is written once up front. Notes are
/// never rewritten to reflect a later understanding.
class Note {
  final String id;

  /// A real instant, stored UTC.
  final DateTime createdAt;
  final String body;

  const Note({
    required this.id,
    required this.createdAt,
    required this.body,
  });

  factory Note.create(String body) => Note(
        id: Ulid().toString(),
        createdAt: DateTime.now().toUtc(),
        body: body,
      );

  Note copyWith({String? body}) => Note(
        id: id,
        createdAt: createdAt,
        body: body ?? this.body,
      );

  Map<String, dynamic> toJson() => {
        'id': id,
        'createdAt': createdAt.toUtc().toIso8601String(),
        'body': body,
      };

  static Note fromJson(Map<String, dynamic> json) => Note(
        id: json['id'] as String,
        createdAt: DateTime.parse(json['createdAt'] as String).toUtc(),
        body: json['body'] as String,
      );
}

/// A file copied into might-do's own storage and bound to a task.
///
/// The copy is authoritative: moving or deleting the user's original has no
/// effect on it. [storedName] is relative to the workspace attachments folder.
class Attachment {
  final String id;

  /// The name the file had when the user attached it, shown in the UI.
  final String originalName;

  /// Name on disk inside the attachments folder. Prefixed with the id so two
  /// files called `contract.pdf` can't collide.
  final String storedName;
  final int sizeBytes;
  final DateTime addedAt;

  const Attachment({
    required this.id,
    required this.originalName,
    required this.storedName,
    required this.sizeBytes,
    required this.addedAt,
  });

  Map<String, dynamic> toJson() => {
        'id': id,
        'originalName': originalName,
        'storedName': storedName,
        'sizeBytes': sizeBytes,
        'addedAt': addedAt.toUtc().toIso8601String(),
      };

  static Attachment fromJson(Map<String, dynamic> json) => Attachment(
        id: json['id'] as String,
        originalName: json['originalName'] as String,
        storedName: json['storedName'] as String,
        sizeBytes: json['sizeBytes'] as int,
        addedAt: DateTime.parse(json['addedAt'] as String).toUtc(),
      );
}

/// A request to be notified about a task at a given moment.
///
/// Carries its own date *and* time, set independently of the task's due date —
/// due dates are days, and a notification needs an instant. A task may have
/// several.
class Reminder {
  final String id;

  /// A real instant, stored UTC, displayed local.
  final DateTime remindAt;

  /// Set once the reminder has been shown, so it fires exactly once.
  final DateTime? firedAt;

  /// Set when the user acknowledges it, which removes it from the overdue
  /// panel.
  final DateTime? dismissedAt;

  const Reminder({
    required this.id,
    required this.remindAt,
    this.firedAt,
    this.dismissedAt,
  });

  factory Reminder.create(DateTime remindAt) => Reminder(
        id: Ulid().toString(),
        remindAt: remindAt.toUtc(),
      );

  bool get isPending => firedAt == null && dismissedAt == null;
  bool get isOutstanding => dismissedAt == null;

  Reminder copyWith({
    DateTime? remindAt,
    DateTime? firedAt,
    DateTime? dismissedAt,
  }) =>
      Reminder(
        id: id,
        remindAt: remindAt ?? this.remindAt,
        firedAt: firedAt ?? this.firedAt,
        dismissedAt: dismissedAt ?? this.dismissedAt,
      );

  Map<String, dynamic> toJson() => {
        'id': id,
        'remindAt': remindAt.toUtc().toIso8601String(),
        'firedAt': firedAt?.toUtc().toIso8601String(),
        'dismissedAt': dismissedAt?.toUtc().toIso8601String(),
      };

  static Reminder fromJson(Map<String, dynamic> json) => Reminder(
        id: json['id'] as String,
        remindAt: DateTime.parse(json['remindAt'] as String).toUtc(),
        firedAt: _parseUtc(json['firedAt']),
        dismissedAt: _parseUtc(json['dismissedAt']),
      );
}

DateTime? _parseUtc(Object? value) =>
    value == null ? null : DateTime.parse(value as String).toUtc();

/// A single unit of work, tracked from conception to completion.
///
/// Persisted as one JSON file per task, named by [id] — see
/// `docs/adr/0001-file-per-task-json-storage.md`.
class Task {
  /// Maximum tags on one task. Tags are meant to stay lightweight.
  static const int maxTags = 10;

  /// Bumped when the on-disk shape changes in a way that needs migrating.
  static const int schemaVersion = 1;

  final String id;
  final String summary;

  /// What the task involves and why, written before work starts.
  final String description;

  final String statusId;
  final String? categoryId;
  final List<String> tagIds;
  final Priority priority;

  /// A day, never an instant. See [CalendarDate].
  final CalendarDate? dueDate;

  /// The moment the task entered a status of type [StatusType.finalType].
  /// Set by the application, cleared if it leaves one.
  final DateTime? completedAt;

  /// Expected effort in whole minutes, recorded up front.
  final int? estimateMinutes;

  /// Actual effort in whole minutes, entered by hand at completion.
  final int? totalTimeMinutes;

  final List<Step> steps;
  final List<Note> notes;
  final List<Attachment> attachments;
  final List<Reminder> reminders;

  /// Fractional index controlling manual position on the board. One field
  /// covers every column, since columns hold disjoint sets of tasks.
  final String boardRank;

  final DateTime createdAt;
  final DateTime updatedAt;

  const Task({
    required this.id,
    required this.summary,
    this.description = '',
    required this.statusId,
    this.categoryId,
    this.tagIds = const [],
    this.priority = Priority.medium,
    this.dueDate,
    this.completedAt,
    this.estimateMinutes,
    this.totalTimeMinutes,
    this.steps = const [],
    this.notes = const [],
    this.attachments = const [],
    this.reminders = const [],
    required this.boardRank,
    required this.createdAt,
    required this.updatedAt,
  });

  factory Task.create({
    required String summary,
    required String statusId,
    required String boardRank,
    String description = '',
    String? categoryId,
    List<String> tagIds = const [],
    Priority priority = Priority.medium,
    CalendarDate? dueDate,
    int? estimateMinutes,
  }) {
    final now = DateTime.now().toUtc();
    return Task(
      id: Ulid().toString(),
      summary: summary,
      description: description,
      statusId: statusId,
      categoryId: categoryId,
      tagIds: tagIds,
      priority: priority,
      dueDate: dueDate,
      estimateMinutes: estimateMinutes,
      boardRank: boardRank,
      createdAt: now,
      updatedAt: now,
    );
  }

  bool get isComplete => completedAt != null;

  int get stepsDone => steps.where((s) => s.done).length;

  /// Reminders that have come due and haven't been acknowledged.
  List<Reminder> outstandingReminders(DateTime now) => reminders
      .where((r) => r.isOutstanding && !r.remindAt.isAfter(now.toUtc()))
      .toList();

  /// Difference between estimate and actual, in minutes. Null unless both are
  /// recorded. Positive means it took longer than expected.
  int? get estimateVariance => (estimateMinutes == null ||
          totalTimeMinutes == null)
      ? null
      : totalTimeMinutes! - estimateMinutes!;

  /// [dueDate] is a day, so "overdue" means the day has fully passed.
  bool get isOverdue =>
      !isComplete && dueDate != null && dueDate!.isPast;

  Task copyWith({
    String? summary,
    String? description,
    String? statusId,
    Object? categoryId = _unset,
    List<String>? tagIds,
    Priority? priority,
    Object? dueDate = _unset,
    Object? completedAt = _unset,
    Object? estimateMinutes = _unset,
    Object? totalTimeMinutes = _unset,
    List<Step>? steps,
    List<Note>? notes,
    List<Attachment>? attachments,
    List<Reminder>? reminders,
    String? boardRank,
    bool touch = true,
  }) {
    return Task(
      id: id,
      summary: summary ?? this.summary,
      description: description ?? this.description,
      statusId: statusId ?? this.statusId,
      categoryId:
          categoryId == _unset ? this.categoryId : categoryId as String?,
      tagIds: tagIds ?? this.tagIds,
      priority: priority ?? this.priority,
      dueDate: dueDate == _unset ? this.dueDate : dueDate as CalendarDate?,
      completedAt:
          completedAt == _unset ? this.completedAt : completedAt as DateTime?,
      estimateMinutes: estimateMinutes == _unset
          ? this.estimateMinutes
          : estimateMinutes as int?,
      totalTimeMinutes: totalTimeMinutes == _unset
          ? this.totalTimeMinutes
          : totalTimeMinutes as int?,
      steps: steps ?? this.steps,
      notes: notes ?? this.notes,
      attachments: attachments ?? this.attachments,
      reminders: reminders ?? this.reminders,
      boardRank: boardRank ?? this.boardRank,
      createdAt: createdAt,
      updatedAt: touch ? DateTime.now().toUtc() : updatedAt,
    );
  }

  /// Key order here is the on-disk key order, which is deliberately stable so
  /// a one-field edit produces a one-line diff for the sync client.
  Map<String, dynamic> toJson() => {
        'schemaVersion': schemaVersion,
        'id': id,
        'summary': summary,
        'description': description,
        'statusId': statusId,
        'categoryId': categoryId,
        'tagIds': tagIds,
        'priority': priority.wire,
        'dueDate': dueDate?.toIso(),
        'completedAt': completedAt?.toUtc().toIso8601String(),
        'estimateMinutes': estimateMinutes,
        'totalTimeMinutes': totalTimeMinutes,
        'boardRank': boardRank,
        'steps': steps.map((s) => s.toJson()).toList(),
        'notes': notes.map((n) => n.toJson()).toList(),
        'attachments': attachments.map((a) => a.toJson()).toList(),
        'reminders': reminders.map((r) => r.toJson()).toList(),
        'createdAt': createdAt.toUtc().toIso8601String(),
        'updatedAt': updatedAt.toUtc().toIso8601String(),
      };

  static Task fromJson(Map<String, dynamic> json) => Task(
        id: json['id'] as String,
        summary: json['summary'] as String,
        description: json['description'] as String? ?? '',
        statusId: json['statusId'] as String,
        categoryId: json['categoryId'] as String?,
        tagIds: (json['tagIds'] as List?)?.cast<String>() ?? const [],
        priority: Priority.fromWire(json['priority'] as String? ?? 'medium'),
        dueDate: CalendarDate.tryParse(json['dueDate'] as String?),
        completedAt: _parseUtc(json['completedAt']),
        estimateMinutes: json['estimateMinutes'] as int?,
        totalTimeMinutes: json['totalTimeMinutes'] as int?,
        steps: _list(json['steps'], Step.fromJson),
        notes: _list(json['notes'], Note.fromJson),
        attachments: _list(json['attachments'], Attachment.fromJson),
        reminders: _list(json['reminders'], Reminder.fromJson),
        boardRank: json['boardRank'] as String? ?? 'i',
        createdAt: _parseUtc(json['createdAt']) ?? DateTime.now().toUtc(),
        updatedAt: _parseUtc(json['updatedAt']) ?? DateTime.now().toUtc(),
      );

  @override
  String toString() => 'Task($id, $summary)';
}

List<T> _list<T>(Object? raw, T Function(Map<String, dynamic>) parse) =>
    (raw as List?)
        ?.map((e) => parse((e as Map).cast<String, dynamic>()))
        .toList() ??
    <T>[];

/// Sentinel letting `copyWith` distinguish "not supplied" from "set to null".
const Object _unset = Object();
