import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../app/task_query.dart';
import '../app/workspace_controller.dart';
import '../domain/status.dart';
import '../domain/task.dart';
import '../domain/workspace_config.dart';
import 'widgets/chips.dart';

/// Tasks as cards in columns, one column per status.
///
/// Dragging a card to another column changes its status — that's the point of
/// the view. Dragging within a column sets a manual order, persisted as a
/// fractional rank so each drop rewrites exactly one file.
class BoardView extends StatelessWidget {
  final TaskQuery query;
  final String? selectedTaskId;
  final ValueChanged<Task> onSelect;

  const BoardView({
    super.key,
    required this.query,
    required this.selectedTaskId,
    required this.onSelect,
  });

  @override
  Widget build(BuildContext context) {
    final controller = context.watch<WorkspaceController>();
    final config = controller.config;
    final columns = config.boardStatuses;

    if (columns.isEmpty) {
      return const Center(
        child: Text('Every status is hidden from the board. Check Settings.'),
      );
    }

    // Completed tasks are always shown here: `Final` statuses have their own
    // columns, and hiding their contents would leave those columns
    // permanently, confusingly empty.
    final visible = query.copyWith(includeCompleted: true).apply(
          controller.tasks,
          config,
        );

    return ListView(
      scrollDirection: Axis.horizontal,
      padding: const EdgeInsets.all(12),
      children: [
        for (final status in columns)
          _BoardColumn(
            status: status,
            config: config,
            tasks: _column(visible, status.id),
            selectedTaskId: selectedTaskId,
            onSelect: onSelect,
          ),
      ],
    );
  }

  static List<Task> _column(List<Task> tasks, String statusId) {
    final column = tasks.where((t) => t.statusId == statusId).toList()
      ..sort((a, b) => a.boardRank.compareTo(b.boardRank));
    return column;
  }
}

class _BoardColumn extends StatelessWidget {
  final Status status;
  final WorkspaceConfig config;
  final List<Task> tasks;
  final String? selectedTaskId;
  final ValueChanged<Task> onSelect;

  const _BoardColumn({
    required this.status,
    required this.config,
    required this.tasks,
    required this.selectedTaskId,
    required this.onSelect,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;

    return Container(
      width: 300,
      margin: const EdgeInsets.only(right: 12),
      decoration: BoxDecoration(
        color: scheme.surfaceContainerLow,
        borderRadius: BorderRadius.circular(10),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(12, 10, 12, 8),
            child: Row(
              children: [
                Expanded(
                  child: Text(
                    status.name,
                    style: theme.textTheme.titleSmall,
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
                const SizedBox(width: 6),
                Text(
                  '${tasks.length}',
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: scheme.onSurfaceVariant,
                  ),
                ),
              ],
            ),
          ),
          Expanded(
            child: ListView(
              padding: const EdgeInsets.fromLTRB(8, 0, 8, 12),
              children: [
                _DropSlot(status: status, tasks: tasks, index: 0),
                for (var i = 0; i < tasks.length; i++) ...[
                  _TaskCard(
                    task: tasks[i],
                    config: config,
                    selected: tasks[i].id == selectedTaskId,
                    onTap: () => onSelect(tasks[i]),
                  ),
                  _DropSlot(status: status, tasks: tasks, index: i + 1),
                ],
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// The gap between two cards. Accepting the drop here is what makes the
/// insertion point unambiguous — the card lands exactly where the line shows.
class _DropSlot extends StatelessWidget {
  final Status status;
  final List<Task> tasks;
  final int index;

  const _DropSlot({
    required this.status,
    required this.tasks,
    required this.index,
  });

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    return DragTarget<Task>(
      onWillAcceptWithDetails: (details) {
        // Dropping a card back where it already sits is a no-op, so don't
        // invite it.
        final dragged = details.data;
        if (dragged.statusId != status.id) return true;
        final current = tasks.indexWhere((t) => t.id == dragged.id);
        return current != index && current != index - 1;
      },
      onAcceptWithDetails: (details) {
        final dragged = details.data;
        final remaining =
            tasks.where((t) => t.id != dragged.id).toList(growable: false);
        final position = tasks.indexWhere((t) => t.id == dragged.id) != -1 &&
                tasks.indexWhere((t) => t.id == dragged.id) < index
            ? index - 1
            : index;

        context.read<WorkspaceController>().reorderOnBoard(
              task: dragged,
              statusId: status.id,
              above: position > 0 ? remaining[position - 1] : null,
              below: position < remaining.length ? remaining[position] : null,
            );
      },
      builder: (context, candidate, _) {
        final active = candidate.isNotEmpty;
        return AnimatedContainer(
          duration: const Duration(milliseconds: 120),
          height: active ? 34 : 8,
          margin: const EdgeInsets.symmetric(vertical: 2),
          decoration: BoxDecoration(
            color: active ? scheme.primary.withValues(alpha: 0.12) : null,
            border: active
                ? Border.all(color: scheme.primary, style: BorderStyle.solid)
                : null,
            borderRadius: BorderRadius.circular(6),
          ),
        );
      },
    );
  }
}

class _TaskCard extends StatelessWidget {
  final Task task;
  final WorkspaceConfig config;
  final bool selected;
  final VoidCallback onTap;

  const _TaskCard({
    required this.task,
    required this.config,
    required this.selected,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    final card = _CardBody(task: task, config: config, selected: selected);

    return Draggable<Task>(
      data: task,
      dragAnchorStrategy: pointerDragAnchorStrategy,
      feedback: Transform.translate(
        offset: const Offset(-140, -24),
        child: Material(
          color: Colors.transparent,
          child: Opacity(
            opacity: 0.9,
            child: SizedBox(
              width: 280,
              child: _CardBody(task: task, config: config, selected: false),
            ),
          ),
        ),
      ),
      childWhenDragging: Opacity(opacity: 0.3, child: card),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(8),
        child: card,
      ),
    );
  }
}

class _CardBody extends StatelessWidget {
  final Task task;
  final WorkspaceConfig config;
  final bool selected;

  const _CardBody({
    required this.task,
    required this.config,
    required this.selected,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;
    final category = config.categoryById(task.categoryId);
    final tags = config.tagsByIds(task.tagIds);

    return Container(
      padding: const EdgeInsets.all(10),
      decoration: BoxDecoration(
        color: scheme.surface,
        borderRadius: BorderRadius.circular(8),
        border: Border.all(
          color: selected ? scheme.primary : scheme.outlineVariant,
          width: selected ? 2 : 1,
        ),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            task.summary,
            maxLines: 3,
            overflow: TextOverflow.ellipsis,
            style: theme.textTheme.bodyMedium?.copyWith(
              decoration: task.isComplete ? TextDecoration.lineThrough : null,
              color: task.isComplete ? scheme.onSurfaceVariant : null,
            ),
          ),
          const SizedBox(height: 8),
          Wrap(
            spacing: 5,
            runSpacing: 4,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              PriorityChip(priority: task.priority),
              if (category != null) CategoryChip(category: category),
              if (task.dueDate != null)
                DueDateChip(date: task.dueDate!, isComplete: task.isComplete),
              for (final tag in tags.take(3)) TagChip(tag: tag),
              if (tags.length > 3)
                MiniChip(
                  label: '+${tags.length - 3}',
                  color: scheme.onSurfaceVariant,
                ),
              if (task.steps.isNotEmpty)
                MiniChip(
                  label: '${task.stepsDone}/${task.steps.length}',
                  color: scheme.onSurfaceVariant,
                  icon: Icons.checklist,
                ),
              if (task.attachments.isNotEmpty)
                MiniChip(
                  label: '${task.attachments.length}',
                  color: scheme.onSurfaceVariant,
                  icon: Icons.attach_file,
                ),
            ],
          ),
        ],
      ),
    );
  }
}
