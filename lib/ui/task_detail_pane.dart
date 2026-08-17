import 'dart:async';
import 'dart:io';

import 'package:file_selector/file_selector.dart';
import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../app/workspace_controller.dart';
import '../domain/calendar_date.dart';
import '../domain/task.dart';
import 'theme.dart';
import 'widgets/chips.dart';

/// Editor for a single task.
///
/// Edits save as you make them — text fields after a short pause, everything
/// else immediately. There is no Save button because there is nothing to
/// discard: the file on disk is the document.
class TaskDetailPane extends StatefulWidget {
  final Task task;
  final VoidCallback onClose;

  const TaskDetailPane({
    super.key,
    required this.task,
    required this.onClose,
  });

  @override
  State<TaskDetailPane> createState() => _TaskDetailPaneState();
}

class _TaskDetailPaneState extends State<TaskDetailPane> {
  late final TextEditingController _summary;
  late final TextEditingController _description;
  late final TextEditingController _estimate;
  late final TextEditingController _totalTime;
  final TextEditingController _newNote = TextEditingController();
  final TextEditingController _newStep = TextEditingController();

  Timer? _debounce;

  @override
  void initState() {
    super.initState();
    _summary = TextEditingController(text: widget.task.summary);
    _description = TextEditingController(text: widget.task.description);
    _estimate = TextEditingController(
      text: widget.task.estimateMinutes == null
          ? ''
          : formatMinutes(widget.task.estimateMinutes!),
    );
    _totalTime = TextEditingController(
      text: widget.task.totalTimeMinutes == null
          ? ''
          : formatMinutes(widget.task.totalTimeMinutes!),
    );
  }

  @override
  void dispose() {
    _debounce?.cancel();
    _summary.dispose();
    _description.dispose();
    _estimate.dispose();
    _totalTime.dispose();
    _newNote.dispose();
    _newStep.dispose();
    super.dispose();
  }

  WorkspaceController get _controller => context.read<WorkspaceController>();

  void _debouncedSave(Task Function(Task) change) {
    _debounce?.cancel();
    _debounce = Timer(const Duration(milliseconds: 500), () {
      final current = _controller.taskById(widget.task.id);
      if (current == null) return;
      _controller.updateTask(change(current));
    });
  }

  void _saveNow(Task Function(Task) change) {
    _debounce?.cancel();
    final current = _controller.taskById(widget.task.id);
    if (current == null) return;
    _controller.updateTask(change(current));
  }

  @override
  Widget build(BuildContext context) {
    final controller = context.watch<WorkspaceController>();
    final task = controller.taskById(widget.task.id) ?? widget.task;
    final theme = Theme.of(context);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        _Header(task: task, onClose: widget.onClose),
        const Divider(height: 1),
        Expanded(
          child: ListView(
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
            children: [
              TextField(
                controller: _summary,
                style: theme.textTheme.titleMedium,
                maxLines: null,
                decoration: const InputDecoration(labelText: 'Summary'),
                onChanged: (value) =>
                    _debouncedSave((t) => t.copyWith(summary: value)),
              ),
              const SizedBox(height: 16),
              _Section(
                title: 'Description',
                subtitle: 'What this task is and why. Written before you start.',
                child: TextField(
                  controller: _description,
                  minLines: 3,
                  maxLines: 10,
                  decoration: const InputDecoration(
                    hintText: 'Add a description',
                  ),
                  onChanged: (value) =>
                      _debouncedSave((t) => t.copyWith(description: value)),
                ),
              ),
              const SizedBox(height: 20),
              _FieldGrid(
                children: [
                  _StatusField(task: task),
                  _PriorityField(task: task),
                  _CategoryField(task: task),
                  _DueDateField(task: task),
                  _DurationField(
                    label: 'Estimate',
                    controller: _estimate,
                    onCommit: (minutes) => _saveNow(
                      (t) => t.copyWith(estimateMinutes: minutes),
                    ),
                  ),
                  _DurationField(
                    label: 'Total time',
                    controller: _totalTime,
                    onCommit: (minutes) => _saveNow(
                      (t) => t.copyWith(totalTimeMinutes: minutes),
                    ),
                  ),
                ],
              ),
              if (task.estimateVariance != null) ...[
                const SizedBox(height: 8),
                _VarianceLine(variance: task.estimateVariance!),
              ],
              const SizedBox(height: 20),
              _TagsField(task: task),
              const SizedBox(height: 20),
              _StepsSection(task: task, controller: _newStep),
              const SizedBox(height: 20),
              _NotesSection(task: task, controller: _newNote),
              const SizedBox(height: 20),
              _RemindersSection(task: task),
              const SizedBox(height: 20),
              _AttachmentsSection(task: task),
              const SizedBox(height: 28),
              _DangerZone(task: task, onDeleted: widget.onClose),
              const SizedBox(height: 16),
              _Timestamps(task: task),
            ],
          ),
        ),
      ],
    );
  }
}

class _Header extends StatelessWidget {
  final Task task;
  final VoidCallback onClose;

  const _Header({required this.task, required this.onClose});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 10, 8, 10),
      child: Row(
        children: [
          Expanded(
            child: Text(
              task.isComplete
                  ? 'Completed ${formatInstant(task.completedAt!)}'
                  : 'Task',
              style: theme.textTheme.labelLarge?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
            ),
          ),
          IconButton(
            tooltip: 'Close',
            icon: const Icon(Icons.close),
            onPressed: onClose,
          ),
        ],
      ),
    );
  }
}

class _Section extends StatelessWidget {
  final String title;
  final String? subtitle;
  final Widget child;
  final Widget? trailing;

  const _Section({
    required this.title,
    this.subtitle,
    required this.child,
    this.trailing,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Expanded(child: Text(title, style: theme.textTheme.titleSmall)),
            ?trailing,
          ],
        ),
        if (subtitle != null) ...[
          const SizedBox(height: 2),
          Text(
            subtitle!,
            style: theme.textTheme.bodySmall?.copyWith(
              color: theme.colorScheme.onSurfaceVariant,
            ),
          ),
        ],
        const SizedBox(height: 8),
        child,
      ],
    );
  }
}

class _FieldGrid extends StatelessWidget {
  final List<Widget> children;

  const _FieldGrid({required this.children});

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(
      builder: (context, constraints) {
        const spacing = 12.0;
        final width = (constraints.maxWidth - spacing) / 2;
        return Wrap(
          spacing: spacing,
          runSpacing: spacing,
          children: [
            for (final child in children) SizedBox(width: width, child: child),
          ],
        );
      },
    );
  }
}

class _StatusField extends StatelessWidget {
  final Task task;

  const _StatusField({required this.task});

  @override
  Widget build(BuildContext context) {
    final controller = context.read<WorkspaceController>();
    final config = context.watch<WorkspaceController>().config;

    return DropdownButtonFormField<String>(
      initialValue:
          config.statusById(task.statusId) == null ? null : task.statusId,
      decoration: const InputDecoration(labelText: 'Status'),
      isExpanded: true,
      items: [
        for (final status in config.orderedStatuses)
          DropdownMenuItem(
            value: status.id,
            child: Row(
              children: [
                Expanded(child: Text(status.name, overflow: TextOverflow.ellipsis)),
                const SizedBox(width: 6),
                Text(
                  status.type.label,
                  style: Theme.of(context).textTheme.bodySmall?.copyWith(
                        color: Theme.of(context).colorScheme.onSurfaceVariant,
                      ),
                ),
              ],
            ),
          ),
      ],
      onChanged: (value) {
        if (value != null) controller.moveToStatus(task, value);
      },
    );
  }
}

class _PriorityField extends StatelessWidget {
  final Task task;

  const _PriorityField({required this.task});

  @override
  Widget build(BuildContext context) {
    final controller = context.read<WorkspaceController>();
    return DropdownButtonFormField<Priority>(
      initialValue: task.priority,
      decoration: const InputDecoration(labelText: 'Priority'),
      isExpanded: true,
      items: [
        for (final priority in Priority.values)
          DropdownMenuItem(
            value: priority,
            child: Row(
              children: [
                Icon(
                  Icons.flag,
                  size: 14,
                  color: priorityColor(priority, Theme.of(context).colorScheme),
                ),
                const SizedBox(width: 8),
                Text(priority.label),
              ],
            ),
          ),
      ],
      onChanged: (value) {
        if (value != null) {
          controller.updateTask(task.copyWith(priority: value));
        }
      },
    );
  }
}

class _CategoryField extends StatelessWidget {
  final Task task;

  const _CategoryField({required this.task});

  @override
  Widget build(BuildContext context) {
    final controller = context.read<WorkspaceController>();
    final config = context.watch<WorkspaceController>().config;

    return DropdownButtonFormField<String?>(
      initialValue: config.categoryById(task.categoryId)?.id,
      decoration: const InputDecoration(labelText: 'Category'),
      isExpanded: true,
      items: [
        const DropdownMenuItem<String?>(value: null, child: Text('None')),
        for (final category in config.categories)
          DropdownMenuItem<String?>(
            value: category.id,
            child: Row(
              children: [
                Container(
                  width: 10,
                  height: 10,
                  decoration: BoxDecoration(
                    color: Color(category.color),
                    shape: BoxShape.circle,
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child:
                      Text(category.name, overflow: TextOverflow.ellipsis),
                ),
              ],
            ),
          ),
      ],
      onChanged: (value) =>
          controller.updateTask(task.copyWith(categoryId: value)),
    );
  }
}

class _DueDateField extends StatelessWidget {
  final Task task;

  const _DueDateField({required this.task});

  @override
  Widget build(BuildContext context) {
    final controller = context.read<WorkspaceController>();
    final due = task.dueDate;

    return InputDecorator(
      decoration: InputDecoration(
        labelText: 'Due date',
        suffixIcon: due == null
            ? null
            : IconButton(
                icon: const Icon(Icons.clear, size: 16),
                tooltip: 'Clear',
                onPressed: () =>
                    controller.updateTask(task.copyWith(dueDate: null)),
              ),
      ),
      child: InkWell(
        onTap: () async {
          final now = DateTime.now();
          final picked = await showDatePicker(
            context: context,
            initialDate: due?.toLocalDateTime() ?? now,
            firstDate: DateTime(now.year - 5),
            lastDate: DateTime(now.year + 10),
          );
          if (picked != null) {
            controller.updateTask(
              task.copyWith(dueDate: CalendarDate.fromLocal(picked)),
            );
          }
        },
        child: Text(
          due == null ? 'Not set' : describeDueDate(due),
          style: TextStyle(
            color: due == null
                ? Theme.of(context).colorScheme.onSurfaceVariant
                : null,
          ),
        ),
      ),
    );
  }
}

class _DurationField extends StatelessWidget {
  final String label;
  final TextEditingController controller;
  final ValueChanged<int?> onCommit;

  const _DurationField({
    required this.label,
    required this.controller,
    required this.onCommit,
  });

  @override
  Widget build(BuildContext context) {
    return TextField(
      controller: controller,
      decoration: InputDecoration(
        labelText: label,
        hintText: '2h 30m',
      ),
      onSubmitted: (value) => onCommit(parseMinutes(value)),
      onTapOutside: (_) {
        FocusManager.instance.primaryFocus?.unfocus();
        onCommit(parseMinutes(controller.text));
      },
    );
  }
}

class _VarianceLine extends StatelessWidget {
  final int variance;

  const _VarianceLine({required this.variance});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    if (variance == 0) {
      return Text('Exactly as estimated.', style: theme.textTheme.bodySmall);
    }
    return Text(
      variance > 0
          ? '${formatMinutes(variance)} longer than estimated.'
          : '${formatMinutes(-variance)} quicker than estimated.',
      style: theme.textTheme.bodySmall?.copyWith(
        color: variance > 0
            ? priorityColor(Priority.high, theme.colorScheme)
            : theme.colorScheme.primary,
      ),
    );
  }
}

class _TagsField extends StatelessWidget {
  final Task task;

  const _TagsField({required this.task});

  @override
  Widget build(BuildContext context) {
    final controller = context.read<WorkspaceController>();
    final config = context.watch<WorkspaceController>().config;
    final tags = config.tagsByIds(task.tagIds);
    final atLimit = task.tagIds.length >= Task.maxTags;

    return _Section(
      title: 'Tags',
      subtitle: '${task.tagIds.length} of ${Task.maxTags} used',
      child: Wrap(
        spacing: 6,
        runSpacing: 6,
        crossAxisAlignment: WrapCrossAlignment.center,
        children: [
          for (final tag in tags)
            TagChip(
              tag: tag,
              onDeleted: () => controller.updateTask(
                task.copyWith(
                  tagIds: task.tagIds.where((id) => id != tag.id).toList(),
                ),
              ),
            ),
          ActionChip(
            avatar: const Icon(Icons.add, size: 14),
            label: const Text('Add tag'),
            visualDensity: VisualDensity.compact,
            onPressed: atLimit
                ? null
                : () => _showTagPicker(context, task, controller),
          ),
        ],
      ),
    );
  }

  Future<void> _showTagPicker(
    BuildContext context,
    Task task,
    WorkspaceController controller,
  ) async {
    final textController = TextEditingController();

    await showDialog<void>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: const Text('Add a tag'),
        content: SizedBox(
          width: 360,
          child: StatefulBuilder(
            builder: (context, setState) {
              final available = controller.config.tags
                  .where((t) => !task.tagIds.contains(t.id))
                  .where((t) => t.name
                      .toLowerCase()
                      .contains(textController.text.toLowerCase()))
                  .toList();

              return Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  TextField(
                    controller: textController,
                    autofocus: true,
                    decoration: const InputDecoration(
                      labelText: 'Tag name',
                      hintText: 'Type to search or create',
                    ),
                    onChanged: (_) => setState(() {}),
                    onSubmitted: (value) async {
                      if (value.trim().isEmpty) return;
                      final tag = await controller.addTag(value.trim());
                      final current = controller.taskById(task.id);
                      if (current != null &&
                          !current.tagIds.contains(tag.id) &&
                          current.tagIds.length < Task.maxTags) {
                        await controller.updateTask(current.copyWith(
                          tagIds: [...current.tagIds, tag.id],
                        ));
                      }
                      if (dialogContext.mounted) {
                        Navigator.of(dialogContext).pop();
                      }
                    },
                  ),
                  const SizedBox(height: 12),
                  if (available.isNotEmpty)
                    ConstrainedBox(
                      constraints: const BoxConstraints(maxHeight: 200),
                      child: SingleChildScrollView(
                        child: Wrap(
                          spacing: 6,
                          runSpacing: 6,
                          children: [
                            for (final tag in available)
                              ActionChip(
                                label: Text('#${tag.name}'),
                                visualDensity: VisualDensity.compact,
                                onPressed: () async {
                                  final current = controller.taskById(task.id);
                                  if (current != null &&
                                      current.tagIds.length < Task.maxTags) {
                                    await controller.updateTask(
                                      current.copyWith(
                                        tagIds: [...current.tagIds, tag.id],
                                      ),
                                    );
                                  }
                                  if (dialogContext.mounted) {
                                    Navigator.of(dialogContext).pop();
                                  }
                                },
                              ),
                          ],
                        ),
                      ),
                    ),
                ],
              );
            },
          ),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(),
            child: const Text('Cancel'),
          ),
        ],
      ),
    );

    textController.dispose();
  }
}

class _StepsSection extends StatelessWidget {
  final Task task;
  final TextEditingController controller;

  const _StepsSection({required this.task, required this.controller});

  @override
  Widget build(BuildContext context) {
    final workspace = context.read<WorkspaceController>();

    return _Section(
      title: 'Steps',
      subtitle: task.steps.isEmpty
          ? 'Break the task down. Steps have no status or dates of their own.'
          : '${task.stepsDone} of ${task.steps.length} done',
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          for (final step in task.steps)
            Row(
              children: [
                Checkbox(
                  value: step.done,
                  visualDensity: VisualDensity.compact,
                  onChanged: (value) =>
                      workspace.setStepDone(task, step.id, value ?? false),
                ),
                Expanded(
                  child: Text(
                    step.text,
                    style: TextStyle(
                      decoration:
                          step.done ? TextDecoration.lineThrough : null,
                      color: step.done
                          ? Theme.of(context).colorScheme.onSurfaceVariant
                          : null,
                    ),
                  ),
                ),
                IconButton(
                  icon: const Icon(Icons.close, size: 16),
                  tooltip: 'Remove step',
                  onPressed: () => workspace.deleteStep(task, step.id),
                ),
              ],
            ),
          TextField(
            controller: controller,
            decoration: const InputDecoration(
              hintText: 'Add a step and press Enter',
              prefixIcon: Icon(Icons.add, size: 18),
            ),
            onSubmitted: (value) {
              if (value.trim().isEmpty) return;
              workspace.addStep(task, value.trim());
              controller.clear();
            },
          ),
        ],
      ),
    );
  }
}

class _NotesSection extends StatelessWidget {
  final Task task;
  final TextEditingController controller;

  const _NotesSection({required this.task, required this.controller});

  @override
  Widget build(BuildContext context) {
    final workspace = context.read<WorkspaceController>();
    final theme = Theme.of(context);
    final notes = [...task.notes]
      ..sort((a, b) => b.createdAt.compareTo(a.createdAt));

    return _Section(
      title: 'Notes',
      subtitle: 'A running log while you work. Newest first.',
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          TextField(
            controller: controller,
            minLines: 2,
            maxLines: 6,
            decoration: const InputDecoration(
              hintText: 'Add a note',
            ),
          ),
          const SizedBox(height: 6),
          Align(
            alignment: Alignment.centerRight,
            child: FilledButton.tonal(
              onPressed: () {
                final text = controller.text.trim();
                if (text.isEmpty) return;
                workspace.addNote(task, text);
                controller.clear();
              },
              child: const Text('Add note'),
            ),
          ),
          const SizedBox(height: 8),
          for (final note in notes)
            Padding(
              padding: const EdgeInsets.only(bottom: 10),
              child: Container(
                padding: const EdgeInsets.all(10),
                decoration: BoxDecoration(
                  color: theme.colorScheme.surfaceContainerLow,
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        Expanded(
                          child: Text(
                            formatInstant(note.createdAt),
                            style: theme.textTheme.labelSmall?.copyWith(
                              color: theme.colorScheme.onSurfaceVariant,
                            ),
                          ),
                        ),
                        InkWell(
                          onTap: () => workspace.deleteNote(task, note.id),
                          child: Icon(
                            Icons.close,
                            size: 14,
                            color: theme.colorScheme.onSurfaceVariant,
                          ),
                        ),
                      ],
                    ),
                    const SizedBox(height: 4),
                    SelectableText(note.body),
                  ],
                ),
              ),
            ),
        ],
      ),
    );
  }
}

class _RemindersSection extends StatelessWidget {
  final Task task;

  const _RemindersSection({required this.task});

  @override
  Widget build(BuildContext context) {
    final workspace = context.read<WorkspaceController>();
    final theme = Theme.of(context);
    final reminders = [...task.reminders]
      ..sort((a, b) => a.remindAt.compareTo(b.remindAt));

    return _Section(
      title: 'Reminders',
      subtitle: 'Notifies you while might-do is open.',
      trailing: IconButton(
        icon: const Icon(Icons.add_alarm, size: 18),
        tooltip: 'Add reminder',
        onPressed: () => _pick(context, workspace),
      ),
      child: reminders.isEmpty
          ? Text(
              'None set.',
              style: theme.textTheme.bodySmall?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
            )
          : Column(
              children: [
                for (final reminder in reminders)
                  ListTile(
                    dense: true,
                    contentPadding: EdgeInsets.zero,
                    leading: Icon(
                      reminder.dismissedAt != null
                          ? Icons.notifications_off_outlined
                          : Icons.alarm,
                      size: 18,
                    ),
                    title: Text(formatInstant(reminder.remindAt)),
                    subtitle: reminder.dismissedAt != null
                        ? const Text('Dismissed')
                        : reminder.firedAt != null
                            ? const Text('Notified')
                            : null,
                    trailing: IconButton(
                      icon: const Icon(Icons.close, size: 16),
                      onPressed: () =>
                          workspace.deleteReminder(task, reminder.id),
                    ),
                  ),
              ],
            ),
    );
  }

  Future<void> _pick(
    BuildContext context,
    WorkspaceController workspace,
  ) async {
    final now = DateTime.now();
    final date = await showDatePicker(
      context: context,
      initialDate: task.dueDate?.toLocalDateTime() ?? now,
      firstDate: DateTime(now.year - 1),
      lastDate: DateTime(now.year + 10),
    );
    if (date == null || !context.mounted) return;

    final time = await showTimePicker(
      context: context,
      initialTime: const TimeOfDay(hour: 9, minute: 0),
    );
    if (time == null) return;

    await workspace.addReminder(
      task,
      DateTime(date.year, date.month, date.day, time.hour, time.minute),
    );
  }
}

class _AttachmentsSection extends StatelessWidget {
  final Task task;

  const _AttachmentsSection({required this.task});

  @override
  Widget build(BuildContext context) {
    final workspace = context.read<WorkspaceController>();
    final theme = Theme.of(context);

    return _Section(
      title: 'Attachments',
      subtitle: 'Files are copied here, so moving the original is safe.',
      trailing: IconButton(
        icon: const Icon(Icons.attach_file, size: 18),
        tooltip: 'Attach a file',
        onPressed: () => _attach(context, workspace),
      ),
      child: task.attachments.isEmpty
          ? Text(
              'None.',
              style: theme.textTheme.bodySmall?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
            )
          : Column(
              children: [
                for (final attachment in task.attachments)
                  ListTile(
                    dense: true,
                    contentPadding: EdgeInsets.zero,
                    leading: const Icon(Icons.insert_drive_file_outlined,
                        size: 18),
                    title: Text(
                      attachment.originalName,
                      overflow: TextOverflow.ellipsis,
                    ),
                    subtitle: Text(_formatBytes(attachment.sizeBytes)),
                    onTap: () => _open(
                      workspace.workspace
                          .attachmentFile(attachment.storedName)
                          .path,
                    ),
                    trailing: IconButton(
                      icon: const Icon(Icons.close, size: 16),
                      tooltip: 'Remove',
                      onPressed: () =>
                          workspace.deleteAttachment(task, attachment.id),
                    ),
                  ),
              ],
            ),
    );
  }

  Future<void> _attach(
    BuildContext context,
    WorkspaceController workspace,
  ) async {
    final file = await openFile();
    if (file == null) return;

    final source = File(file.path);
    final size = await source.length();
    const warnAbove = 25 * 1024 * 1024;

    if (size > warnAbove && context.mounted) {
      final proceed = await showDialog<bool>(
        context: context,
        builder: (context) => AlertDialog(
          title: const Text('That is a large file'),
          content: Text(
            '${file.name} is ${_formatBytes(size)}. Attachments are copied '
            'into your workspace folder, so your sync client will upload it '
            'to every machine. Attach it anyway?',
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(context).pop(false),
              child: const Text('Cancel'),
            ),
            FilledButton(
              onPressed: () => Navigator.of(context).pop(true),
              child: const Text('Attach'),
            ),
          ],
        ),
      );
      if (proceed != true) return;
    }

    await workspace.attachFile(task, source);
  }

  /// Hands the file to the OS rather than trying to preview it in-app.
  static Future<void> _open(String path) async {
    if (Platform.isMacOS) {
      await Process.run('open', [path]);
    } else if (Platform.isWindows) {
      await Process.run('cmd', ['/c', 'start', '', path]);
    } else {
      await Process.run('xdg-open', [path]);
    }
  }
}

String _formatBytes(int bytes) {
  if (bytes < 1024) return '$bytes B';
  if (bytes < 1024 * 1024) return '${(bytes / 1024).toStringAsFixed(0)} KB';
  return '${(bytes / (1024 * 1024)).toStringAsFixed(1)} MB';
}

class _DangerZone extends StatelessWidget {
  final Task task;
  final VoidCallback onDeleted;

  const _DangerZone({required this.task, required this.onDeleted});

  @override
  Widget build(BuildContext context) {
    final workspace = context.read<WorkspaceController>();
    final scheme = Theme.of(context).colorScheme;

    return OutlinedButton.icon(
      style: OutlinedButton.styleFrom(foregroundColor: scheme.error),
      icon: const Icon(Icons.delete_outline, size: 18),
      label: const Text('Delete task'),
      onPressed: () async {
        final confirmed = await showDialog<bool>(
          context: context,
          builder: (context) => AlertDialog(
            title: const Text('Delete this task?'),
            content: Text(
              '"${task.summary}" moves to the .trash folder inside your '
              'workspace. Nothing is destroyed — you can drag it back with '
              'your file manager.\n\n'
              'If you simply are not going to do it, moving it to an '
              'Abandoned status keeps the record instead.',
            ),
            actions: [
              TextButton(
                onPressed: () => Navigator.of(context).pop(false),
                child: const Text('Cancel'),
              ),
              FilledButton(
                style: FilledButton.styleFrom(backgroundColor: scheme.error),
                onPressed: () => Navigator.of(context).pop(true),
                child: const Text('Delete'),
              ),
            ],
          ),
        );

        if (confirmed == true) {
          await workspace.trashTask(task);
          onDeleted();
        }
      },
    );
  }
}

class _Timestamps extends StatelessWidget {
  final Task task;

  const _Timestamps({required this.task});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Text(
      'Created ${formatInstant(task.createdAt)}\n'
      'Updated ${formatInstant(task.updatedAt)}',
      style: theme.textTheme.labelSmall?.copyWith(
        color: theme.colorScheme.onSurfaceVariant,
      ),
    );
  }
}
