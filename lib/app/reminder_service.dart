import 'dart:async';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:flutter_local_notifications/flutter_local_notifications.dart';

import '../domain/task.dart';
import 'workspace_controller.dart';

/// Fires OS notifications for reminders that come due while the app is open.
///
/// Scope is deliberately limited to "while running": delivery when the process
/// is closed needs a tray presence and launch-at-login, which is deferred.
/// Anything that fires while you were away is caught by the in-app overdue
/// panel instead, so nothing is silently missed — see [outstandingReminders].
class ReminderService {
  final WorkspaceController controller;

  final FlutterLocalNotificationsPlugin _plugin =
      FlutterLocalNotificationsPlugin();

  Timer? _ticker;
  bool _available = false;

  /// How often to check. Reminders are set to the minute, so this is plenty
  /// and costs nothing — it's a scan of an in-memory list.
  static const Duration _interval = Duration(seconds: 20);

  ReminderService(this.controller);

  Future<void> start() async {
    _available = await _initialise();
    _ticker?.cancel();
    _ticker = Timer.periodic(_interval, (_) => _tick());
    await _tick();
  }

  void dispose() {
    _ticker?.cancel();
    _ticker = null;
  }

  Future<bool> _initialise() async {
    try {
      const settings = InitializationSettings(
        macOS: DarwinInitializationSettings(
          requestAlertPermission: true,
          requestBadgePermission: false,
          requestSoundPermission: true,
        ),
        linux: LinuxInitializationSettings(defaultActionName: 'Open'),
        windows: WindowsInitializationSettings(
          appName: 'might-do',
          appUserModelId: 'org.chonar.mightDo',
          // Stable identity for Windows toast notifications. Changing it makes
          // Windows treat the app as a different sender.
          guid: '5f6b1f38-1d3a-4a5f-9a52-2f0a2c1b7e41',
        ),
      );
      final result = await _plugin.initialize(settings: settings);
      return result ?? true;
    } catch (error) {
      // Notifications are a convenience. If the platform refuses, the overdue
      // panel still surfaces everything.
      debugPrint('Reminder notifications unavailable: $error');
      return false;
    }
  }

  Future<void> _tick() async {
    final now = DateTime.now().toUtc();

    for (final task in controller.tasks) {
      for (final reminder in task.reminders) {
        if (!reminder.isPending) continue;
        if (reminder.remindAt.isAfter(now)) continue;

        // Mark fired before showing, so a failure to notify can't produce a
        // loop that re-notifies every 20 seconds.
        await controller.markReminderFired(task, reminder.id);
        if (_available) await _show(task);
      }
    }
  }

  Future<void> _show(Task task) async {
    try {
      await _plugin.show(
        id: task.id.hashCode & 0x7fffffff,
        title: task.summary,
        body: _body(task),
        notificationDetails: const NotificationDetails(
          macOS: DarwinNotificationDetails(),
          linux: LinuxNotificationDetails(),
          windows: WindowsNotificationDetails(),
        ),
      );
    } catch (error) {
      debugPrint('Could not show notification: $error');
    }
  }

  static String _body(Task task) {
    if (task.dueDate != null) {
      return 'Due ${task.dueDate!.toIso()}';
    }
    return task.description.isEmpty
        ? 'Reminder'
        : task.description.split('\n').first;
  }
}

/// Every reminder that has come due and not been dismissed, newest first.
///
/// This is what makes reminders trustworthy without a background process: open
/// the app after two days away and everything that fired while it was closed
/// is waiting here.
List<({Task task, Reminder reminder})> outstandingReminders(
  List<Task> tasks, {
  DateTime? now,
}) {
  final moment = (now ?? DateTime.now()).toUtc();
  final due = <({Task task, Reminder reminder})>[];

  for (final task in tasks) {
    for (final reminder in task.outstandingReminders(moment)) {
      due.add((task: task, reminder: reminder));
    }
  }

  due.sort((a, b) => b.reminder.remindAt.compareTo(a.reminder.remindAt));
  return due;
}

/// Whether this platform can show notifications at all.
bool get notificationsSupported =>
    Platform.isMacOS || Platform.isWindows || Platform.isLinux;
