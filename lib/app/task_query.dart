import '../domain/status.dart';
import '../domain/task.dart';
import '../domain/workspace_config.dart';

/// How the list view is sorted. The board has its own manual order.
enum TaskSort {
  smart('Priority & due date'),
  dueDate('Due date'),
  priority('Priority'),
  summary('Summary'),
  created('Recently created'),
  updated('Recently updated');

  const TaskSort(this.label);

  final String label;
}

/// A filter and sort over the workspace, driving the list view.
///
/// Search covers summaries, descriptions, notes and steps — everything the
/// user typed. Matching anything less makes search feel broken.
class TaskQuery {
  final String search;
  final Set<String> statusIds;
  final Set<StatusType> statusTypes;
  final Set<String> categoryIds;
  final Set<String> tagIds;
  final Set<Priority> priorities;
  final bool overdueOnly;

  /// Tasks in `Final` statuses are hidden by default — the working set is what
  /// you have left to do.
  final bool includeCompleted;

  final TaskSort sort;

  const TaskQuery({
    this.search = '',
    this.statusIds = const {},
    this.statusTypes = const {},
    this.categoryIds = const {},
    this.tagIds = const {},
    this.priorities = const {},
    this.overdueOnly = false,
    this.includeCompleted = false,
    this.sort = TaskSort.smart,
  });

  bool get isFiltered =>
      search.trim().isNotEmpty ||
      statusIds.isNotEmpty ||
      statusTypes.isNotEmpty ||
      categoryIds.isNotEmpty ||
      tagIds.isNotEmpty ||
      priorities.isNotEmpty ||
      overdueOnly ||
      includeCompleted;

  TaskQuery copyWith({
    String? search,
    Set<String>? statusIds,
    Set<StatusType>? statusTypes,
    Set<String>? categoryIds,
    Set<String>? tagIds,
    Set<Priority>? priorities,
    bool? overdueOnly,
    bool? includeCompleted,
    TaskSort? sort,
  }) =>
      TaskQuery(
        search: search ?? this.search,
        statusIds: statusIds ?? this.statusIds,
        statusTypes: statusTypes ?? this.statusTypes,
        categoryIds: categoryIds ?? this.categoryIds,
        tagIds: tagIds ?? this.tagIds,
        priorities: priorities ?? this.priorities,
        overdueOnly: overdueOnly ?? this.overdueOnly,
        includeCompleted: includeCompleted ?? this.includeCompleted,
        sort: sort ?? this.sort,
      );

  List<Task> apply(List<Task> tasks, WorkspaceConfig config) {
    final terms = search
        .toLowerCase()
        .split(RegExp(r'\s+'))
        .where((t) => t.isNotEmpty)
        .toList();

    final matched = tasks.where((task) {
      final status = config.statusById(task.statusId);

      if (!includeCompleted &&
          status?.type == StatusType.finalType &&
          statusIds.isEmpty) {
        return false;
      }
      if (statusIds.isNotEmpty && !statusIds.contains(task.statusId)) {
        return false;
      }
      if (statusTypes.isNotEmpty &&
          (status == null || !statusTypes.contains(status.type))) {
        return false;
      }
      if (categoryIds.isNotEmpty &&
          (task.categoryId == null ||
              !categoryIds.contains(task.categoryId))) {
        return false;
      }
      if (tagIds.isNotEmpty && !task.tagIds.any(tagIds.contains)) {
        return false;
      }
      if (priorities.isNotEmpty && !priorities.contains(task.priority)) {
        return false;
      }
      if (overdueOnly && !task.isOverdue) return false;

      if (terms.isEmpty) return true;
      final haystack = _searchText(task);
      return terms.every(haystack.contains);
    }).toList();

    matched.sort((a, b) => _compare(a, b, config));
    return matched;
  }

  static String _searchText(Task task) => [
        task.summary,
        task.description,
        ...task.notes.map((n) => n.body),
        ...task.steps.map((s) => s.text),
      ].join('\n').toLowerCase();

  int _compare(Task a, Task b, WorkspaceConfig config) {
    switch (sort) {
      case TaskSort.smart:
        // Overdue first, then priority, then soonest due, then oldest.
        final overdue = _flag(b.isOverdue).compareTo(_flag(a.isOverdue));
        if (overdue != 0) return overdue;
        final priority = a.priority.compareDescending(b.priority);
        if (priority != 0) return priority;
        final due = _compareDue(a, b);
        if (due != 0) return due;
        return a.createdAt.compareTo(b.createdAt);

      case TaskSort.dueDate:
        final due = _compareDue(a, b);
        return due != 0 ? due : a.priority.compareDescending(b.priority);

      case TaskSort.priority:
        final priority = a.priority.compareDescending(b.priority);
        return priority != 0 ? priority : _compareDue(a, b);

      case TaskSort.summary:
        return a.summary.toLowerCase().compareTo(b.summary.toLowerCase());

      case TaskSort.created:
        return b.createdAt.compareTo(a.createdAt);

      case TaskSort.updated:
        return b.updatedAt.compareTo(a.updatedAt);
    }
  }

  /// Undated tasks sort last — a task with no due date isn't more urgent than
  /// one due tomorrow.
  static int _compareDue(Task a, Task b) {
    if (a.dueDate == null && b.dueDate == null) return 0;
    if (a.dueDate == null) return 1;
    if (b.dueDate == null) return -1;
    return a.dueDate!.compareTo(b.dueDate!);
  }

  static int _flag(bool value) => value ? 1 : 0;
}
