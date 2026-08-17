import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../app/reminder_service.dart';
import '../app/workspace_controller.dart';
import 'widgets/chips.dart';

/// Shows reminders that have come due and not been acknowledged.
///
/// This is the half of reminders that doesn't depend on the app having been
/// running: anything that fell due while might-do was closed appears here the
/// moment you open it.
class RemindersBanner extends StatelessWidget {
  const RemindersBanner({super.key});

  @override
  Widget build(BuildContext context) {
    final controller = context.watch<WorkspaceController>();
    final due = outstandingReminders(controller.tasks);

    if (due.isEmpty) return const SizedBox.shrink();

    final scheme = Theme.of(context).colorScheme;
    final first = due.first;

    return Material(
      color: scheme.secondaryContainer,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
        child: Row(
          children: [
            Icon(Icons.alarm, size: 18, color: scheme.onSecondaryContainer),
            const SizedBox(width: 10),
            Expanded(
              child: Text(
                due.length == 1
                    ? '${first.task.summary} — reminder set for '
                        '${formatInstant(first.reminder.remindAt)}'
                    : '${due.length} reminders are waiting, the most recent '
                        'for "${first.task.summary}"',
                overflow: TextOverflow.ellipsis,
                style: TextStyle(color: scheme.onSecondaryContainer),
              ),
            ),
            TextButton(
              onPressed: () => showDialog<void>(
                context: context,
                builder: (_) => ChangeNotifierProvider.value(
                  value: controller,
                  child: const _RemindersDialog(),
                ),
              ),
              child: const Text('Review'),
            ),
            TextButton(
              onPressed: () {
                for (final entry in due) {
                  controller.dismissReminder(entry.task, entry.reminder.id);
                }
              },
              child: const Text('Dismiss all'),
            ),
          ],
        ),
      ),
    );
  }
}

class _RemindersDialog extends StatelessWidget {
  const _RemindersDialog();

  @override
  Widget build(BuildContext context) {
    final controller = context.watch<WorkspaceController>();
    final due = outstandingReminders(controller.tasks);

    return AlertDialog(
      title: const Text('Reminders'),
      content: SizedBox(
        width: 460,
        child: due.isEmpty
            ? const Text('Nothing waiting.')
            : ListView(
                shrinkWrap: true,
                children: [
                  for (final entry in due)
                    ListTile(
                      contentPadding: EdgeInsets.zero,
                      leading: const Icon(Icons.alarm),
                      title: Text(entry.task.summary),
                      subtitle: Text(formatInstant(entry.reminder.remindAt)),
                      trailing: TextButton(
                        onPressed: () => controller.dismissReminder(
                          entry.task,
                          entry.reminder.id,
                        ),
                        child: const Text('Dismiss'),
                      ),
                    ),
                ],
              ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Close'),
        ),
      ],
    );
  }
}
