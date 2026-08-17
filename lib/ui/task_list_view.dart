import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../app/task_query.dart';
import '../app/workspace_controller.dart';
import '../domain/status.dart';
import '../domain/task.dart';
import 'theme.dart';
import 'widgets/chips.dart';

/// The flat, sortable, filterable view of tasks.
class TaskListView extends StatelessWidget {
  final TaskQuery query;
  final String? selectedTaskId;
  final ValueChanged<Task> onSelect;

  const TaskListView({
    super.key,
    required this.query,
    required this.selectedTaskId,
    required this.onSelect,
  });

  @override
  Widget build(BuildContext context) {
    final controller = context.watch<WorkspaceController>();
    final config = controller.config;
    final tasks = query.apply(controller.tasks, config);

    if (tasks.isEmpty) {
      return _EmptyState(
        filtered: query.isFiltered,
        totalTasks: controller.tasks.length,
      );
    }

    return ListView.separated(
      padding: const EdgeInsets.symmetric(vertical: 4),
      itemCount: tasks.length,
      separatorBuilder: (_, _) => const Divider(height: 1, indent: 12),
      itemBuilder: (context, index) {
        final task = tasks[index];
        return _TaskRow(
          task: task,
          status: config.statusById(task.statusId),
          category: config.categoryById(task.categoryId),
          tags: config.tagsByIds(task.tagIds),
          selected: task.id == selectedTaskId,
          onTap: () => onSelect(task),
        );
      },
    );
  }
}

class _TaskRow extends StatelessWidget {
  final Task task;
  final Status? status;
  final Category? category;
  final List<Tag> tags;
  final bool selected;
  final VoidCallback onTap;

  const _TaskRow({
    required this.task,
    required this.status,
    required this.category,
    required this.tags,
    required this.selected,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;
    final complete = task.isComplete;

    return Material(
      color: selected ? scheme.primaryContainer.withValues(alpha: 0.5) : null,
      child: InkWell(
        onTap: onTap,
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              _StatusDot(status: status),
              const SizedBox(width: 10),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      task.summary,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: theme.textTheme.bodyLarge?.copyWith(
                        decoration: complete ? TextDecoration.lineThrough : null,
                        color: complete ? scheme.onSurfaceVariant : null,
                      ),
                    ),
                    const SizedBox(height: 4),
                    Wrap(
                      spacing: 6,
                      runSpacing: 4,
                      crossAxisAlignment: WrapCrossAlignment.center,
                      children: [
                        if (status != null)
                          MiniChip(
                            label: status!.name,
                            color: scheme.onSurfaceVariant,
                          ),
                        PriorityChip(priority: task.priority),
                        if (category != null) CategoryChip(category: category!),
                        if (task.dueDate != null)
                          DueDateChip(
                            date: task.dueDate!,
                            isComplete: complete,
                          ),
                        for (final tag in tags) TagChip(tag: tag),
                        if (task.steps.isNotEmpty)
                          MiniChip(
                            label: '${task.stepsDone}/${task.steps.length}',
                            color: scheme.onSurfaceVariant,
                            icon: Icons.checklist,
                          ),
                        if (task.notes.isNotEmpty)
                          MiniChip(
                            label: '${task.notes.length}',
                            color: scheme.onSurfaceVariant,
                            icon: Icons.notes,
                          ),
                        if (task.attachments.isNotEmpty)
                          MiniChip(
                            label: '${task.attachments.length}',
                            color: scheme.onSurfaceVariant,
                            icon: Icons.attach_file,
                          ),
                        if (task.reminders.any((r) => r.isOutstanding))
                          MiniChip(
                            label: 'Reminder',
                            color: scheme.onSurfaceVariant,
                            icon: Icons.alarm,
                          ),
                      ],
                    ),
                  ],
                ),
              ),
              const SizedBox(width: 12),
              if (task.estimateMinutes != null || task.totalTimeMinutes != null)
                _TimeSummary(task: task),
            ],
          ),
        ),
      ),
    );
  }
}

class _StatusDot extends StatelessWidget {
  final Status? status;

  const _StatusDot({required this.status});

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final type = status?.type;

    final (Color color, bool filled) = switch (type) {
      StatusType.initial => (scheme.outline, false),
      StatusType.active => (scheme.primary, true),
      StatusType.finalType => (scheme.onSurfaceVariant, true),
      null => (scheme.error, false),
    };

    return Tooltip(
      message: status == null
          ? 'This task points at a status that no longer exists'
          : '${status!.name} · ${status!.type.label}',
      child: Container(
        margin: const EdgeInsets.only(top: 5),
        width: 11,
        height: 11,
        decoration: BoxDecoration(
          shape: BoxShape.circle,
          color: filled ? color : null,
          border: Border.all(color: color, width: 1.5),
        ),
        child: type == StatusType.finalType
            ? Icon(Icons.check, size: 8, color: scheme.surface)
            : null,
      ),
    );
  }
}

class _TimeSummary extends StatelessWidget {
  final Task task;

  const _TimeSummary({required this.task});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final variance = task.estimateVariance;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.end,
      children: [
        Text(
          [
            if (task.estimateMinutes != null)
              'Est ${formatMinutes(task.estimateMinutes!)}',
            if (task.totalTimeMinutes != null)
              'Actual ${formatMinutes(task.totalTimeMinutes!)}',
          ].join(' · '),
          style: theme.textTheme.bodySmall,
        ),
        if (variance != null && variance != 0)
          Text(
            variance > 0
                ? '${formatMinutes(variance)} over'
                : '${formatMinutes(-variance)} under',
            style: theme.textTheme.bodySmall?.copyWith(
              color: variance > 0
                  ? priorityColor(Priority.high, theme.colorScheme)
                  : theme.colorScheme.primary,
            ),
          ),
      ],
    );
  }
}

class _EmptyState extends StatelessWidget {
  final bool filtered;
  final int totalTasks;

  const _EmptyState({required this.filtered, required this.totalTasks});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              filtered ? Icons.filter_alt_off_outlined : Icons.task_alt,
              size: 40,
              color: theme.colorScheme.outline,
            ),
            const SizedBox(height: 12),
            Text(
              filtered
                  ? 'Nothing matches these filters'
                  : totalTasks == 0
                      ? 'No tasks yet'
                      : 'Nothing left to do',
              style: theme.textTheme.titleMedium,
            ),
            const SizedBox(height: 6),
            Text(
              filtered
                  ? 'There are $totalTasks tasks in this workspace.'
                  : totalTasks == 0
                      ? 'Create one with the New task button.'
                      : 'Completed tasks are hidden. Turn on "Include '
                          'completed" to see them.',
              style: theme.textTheme.bodyMedium?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
              textAlign: TextAlign.center,
            ),
          ],
        ),
      ),
    );
  }
}
