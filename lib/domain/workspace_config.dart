import 'package:ulid/ulid.dart';

import 'status.dart';

/// Everything about a workspace that isn't a task: the statuses, categories and
/// tags the user has defined, plus which status new tasks start in.
///
/// Persisted as a single `config.json`. It is the one shared file in the
/// storage model and therefore the one genuine conflict hotspot — accepted
/// deliberately, because status and category edits are rare.
class WorkspaceConfig {
  static const int schemaVersion = 1;

  final List<Status> statuses;
  final List<Category> categories;
  final List<Tag> tags;

  /// The status new tasks are created in. Always a status of type
  /// [StatusType.initial]. This designation means nothing else.
  final String defaultStatusId;

  const WorkspaceConfig({
    required this.statuses,
    required this.categories,
    required this.tags,
    required this.defaultStatusId,
  });

  /// The starting point for a brand new workspace: one status of each type,
  /// plus a hidden backlog, so the board is immediately usable.
  factory WorkspaceConfig.seed() {
    final backlog = Status(
      id: Ulid().toString(),
      name: 'Backlog',
      type: StatusType.initial,
      order: 0,
      hiddenFromBoard: true,
    );
    final notStarted = Status(
      id: Ulid().toString(),
      name: 'Not Started',
      type: StatusType.initial,
      order: 1,
    );
    final inProgress = Status(
      id: Ulid().toString(),
      name: 'In Progress',
      type: StatusType.active,
      order: 2,
    );
    final blocked = Status(
      id: Ulid().toString(),
      name: 'Blocked',
      type: StatusType.active,
      order: 3,
    );
    final done = Status(
      id: Ulid().toString(),
      name: 'Done',
      type: StatusType.finalType,
      order: 4,
    );
    final abandoned = Status(
      id: Ulid().toString(),
      name: 'Abandoned',
      type: StatusType.finalType,
      order: 5,
      hiddenFromBoard: true,
    );

    return WorkspaceConfig(
      statuses: [backlog, notStarted, inProgress, blocked, done, abandoned],
      categories: const [],
      tags: const [],
      defaultStatusId: notStarted.id,
    );
  }

  List<Status> get orderedStatuses {
    final sorted = [...statuses]..sort((a, b) => a.order.compareTo(b.order));
    return sorted;
  }

  /// Statuses that get a column on the Kanban view, left to right.
  List<Status> get boardStatuses =>
      orderedStatuses.where((s) => !s.hiddenFromBoard).toList();

  Status? statusById(String id) {
    for (final status in statuses) {
      if (status.id == id) return status;
    }
    return null;
  }

  Category? categoryById(String? id) {
    if (id == null) return null;
    for (final category in categories) {
      if (category.id == id) return category;
    }
    return null;
  }

  Tag? tagById(String id) {
    for (final tag in tags) {
      if (tag.id == id) return tag;
    }
    return null;
  }

  List<Tag> tagsByIds(List<String> ids) =>
      ids.map(tagById).whereType<Tag>().toList();

  /// Whether entering [statusId] should stamp a completion date.
  bool isFinal(String statusId) =>
      statusById(statusId)?.type == StatusType.finalType;

  WorkspaceConfig copyWith({
    List<Status>? statuses,
    List<Category>? categories,
    List<Tag>? tags,
    String? defaultStatusId,
  }) =>
      WorkspaceConfig(
        statuses: statuses ?? this.statuses,
        categories: categories ?? this.categories,
        tags: tags ?? this.tags,
        defaultStatusId: defaultStatusId ?? this.defaultStatusId,
      );

  Map<String, dynamic> toJson() => {
        'schemaVersion': schemaVersion,
        'defaultStatusId': defaultStatusId,
        'statuses': orderedStatuses.map((s) => s.toJson()).toList(),
        'categories': categories.map((c) => c.toJson()).toList(),
        'tags': tags.map((t) => t.toJson()).toList(),
      };

  static WorkspaceConfig fromJson(Map<String, dynamic> json) => WorkspaceConfig(
        statuses: (json['statuses'] as List)
            .map((e) => Status.fromJson((e as Map).cast<String, dynamic>()))
            .toList(),
        categories: (json['categories'] as List? ?? [])
            .map((e) => Category.fromJson((e as Map).cast<String, dynamic>()))
            .toList(),
        tags: (json['tags'] as List? ?? [])
            .map((e) => Tag.fromJson((e as Map).cast<String, dynamic>()))
            .toList(),
        defaultStatusId: json['defaultStatusId'] as String,
      );
}
