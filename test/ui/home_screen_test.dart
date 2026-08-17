import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:might_do/app/app_settings.dart';
import 'package:might_do/app/workspace_controller.dart';
import 'package:might_do/domain/status.dart';
import 'package:might_do/storage/task_store.dart';
import 'package:might_do/storage/workspace.dart';
import 'package:might_do/ui/home_screen.dart';
import 'package:might_do/ui/theme.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// Widget tests for the shell.
///
/// Note on `tester.runAsync`: test bodies run under `FakeAsync`, where a real
/// file write never completes — its continuation is queued as a fake microtask
/// that only flushes while something is pumping, and a body sitting on `await`
/// is not pumping. Since this controller writes to disk on every change, any
/// call that touches storage has to be made inside `runAsync`, which runs in
/// the real zone. Awaiting one directly deadlocks until the test times out.
void main() {
  late Directory root;
  late WorkspaceController controller;
  late AppSettings settings;

  setUp(() async {
    SharedPreferences.setMockInitialValues({});
    root = await Directory.systemTemp.createTemp('might_do_ui');
    // Watching is off for the same reason: its timers never settle.
    controller = WorkspaceController(
      TaskStore(Workspace(root)),
      watchForChanges: false,
    );
    await controller.open();
    settings = await AppSettings.load();
  });

  tearDown(() async {
    controller.dispose();
    if (await root.exists()) await root.delete(recursive: true);
  });

  /// might-do is a desktop app; testing it in a phone-sized window means the
  /// board's later columns are never built and the detail pane has no room.
  Future<void> useDesktopWindow(WidgetTester tester) async {
    await tester.binding.setSurfaceSize(const Size(1600, 1000));
    addTearDown(() => tester.binding.setSurfaceSize(null));
  }

  Widget harness() => MaterialApp(
        theme: buildTheme(Brightness.light),
        home: MultiProvider(
          providers: [
            ChangeNotifierProvider.value(value: controller),
            Provider<AppSettings>.value(value: settings),
          ],
          child: HomeScreen(onCloseWorkspace: () async {}),
        ),
      );

  /// Lets pending real I/O finish, then rebuilds.
  ///
  /// Saving a task is a *chain* of real operations — write a temp file, flush
  /// it, rename it over the target. Each link only starts once the previous
  /// one's continuation runs, and those continuations are fake microtasks that
  /// flush while pumping, not while `runAsync` is in the real zone. So a single
  /// pass advances exactly one link; the cycle has to alternate.
  Future<void> settleWithDisk(WidgetTester tester) async {
    for (var i = 0; i < 6; i++) {
      await tester.runAsync(
        () => Future<void>.delayed(const Duration(milliseconds: 10)),
      );
      await tester.pumpAndSettle();
    }
  }

  testWidgets('shows an empty state on a fresh workspace', (tester) async {
    await useDesktopWindow(tester);
    await tester.pumpWidget(harness());
    await tester.pumpAndSettle();

    expect(find.text('No tasks yet'), findsOneWidget);
    expect(find.text('0 tasks'), findsOneWidget);
  });

  testWidgets('creating a task opens it in the detail pane', (tester) async {
    await useDesktopWindow(tester);
    await tester.pumpWidget(harness());
    await tester.pumpAndSettle();

    await tester.tap(find.text('New task'));
    await settleWithDisk(tester);

    expect(controller.tasks, hasLength(1));
    expect(find.text('Summary'), findsOneWidget);
    expect(find.widgetWithText(TextField, 'New task'), findsOneWidget);
    expect(find.text('1 task'), findsOneWidget);
  });

  testWidgets('editing the summary updates the task and the list',
      (tester) async {
    await useDesktopWindow(tester);
    await tester.pumpWidget(harness());
    await tester.pumpAndSettle();
    await tester.tap(find.text('New task'));
    await settleWithDisk(tester);

    await tester.enterText(
      find.widgetWithText(TextField, 'New task'),
      'Renew passport',
    );
    // The summary field saves after a debounce.
    await tester.pump(const Duration(milliseconds: 600));
    await settleWithDisk(tester);

    expect(controller.tasks.single.summary, 'Renew passport');
    expect(find.text('Renew passport'), findsWidgets);
  });

  testWidgets('search narrows the list', (tester) async {
    await tester.runAsync(() async {
      await controller.createTask(summary: 'Renew passport');
      await controller.createTask(summary: 'Buy milk');
    });

    await useDesktopWindow(tester);
    await tester.pumpWidget(harness());
    await tester.pumpAndSettle();
    expect(find.text('2 tasks'), findsOneWidget);

    await tester.enterText(find.byType(TextField).first, 'passport');
    await tester.pumpAndSettle();

    expect(find.text('Showing 1 of 2 tasks'), findsOneWidget);
    expect(find.text('Buy milk'), findsNothing);
  });

  testWidgets('completed tasks are hidden until asked for', (tester) async {
    await tester.runAsync(() async {
      final task = await controller.createTask(summary: 'Shipped');
      final done = controller.config.statuses
          .firstWhere((s) => s.type == StatusType.finalType);
      await controller.moveToStatus(task, done.id);
    });

    await useDesktopWindow(tester);
    await tester.pumpWidget(harness());
    await tester.pumpAndSettle();

    expect(find.text('Nothing left to do'), findsOneWidget);

    await tester.tap(find.byTooltip('Filters'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Include completed'));
    await tester.pumpAndSettle();

    expect(find.text('Shipped'), findsOneWidget);
  });

  testWidgets('the status filter narrows the list to one status',
      (tester) async {
    late Status inProgress;
    await tester.runAsync(() async {
      inProgress = controller.config.statuses
          .firstWhere((s) => s.name == 'In Progress');
      final moving = await controller.createTask(summary: 'Renew passport');
      await controller.moveToStatus(moving, inProgress.id);
      await controller.createTask(summary: 'Buy milk');
    });

    await useDesktopWindow(tester);
    await tester.pumpWidget(harness());
    await tester.pumpAndSettle();
    expect(find.text('2 tasks'), findsOneWidget);

    await tester.tap(find.byTooltip('Filters'));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilterChip, inProgress.name));
    await tester.pumpAndSettle();

    expect(find.text('Showing 1 of 2 tasks'), findsOneWidget);
    expect(find.text('Buy milk'), findsNothing);
  });

  testWidgets('switching to the board shows a column per visible status',
      (tester) async {
    await useDesktopWindow(tester);
    await tester.pumpWidget(harness());
    await tester.pumpAndSettle();

    await tester.tap(find.text('Board'));
    await tester.pumpAndSettle();

    for (final status in controller.config.boardStatuses) {
      expect(find.text(status.name), findsWidgets,
          reason: '${status.name} should have a column');
    }
    // Backlog and Abandoned are hidden from the board by default.
    for (final status
        in controller.config.statuses.where((s) => s.hiddenFromBoard)) {
      expect(find.text(status.name), findsNothing,
          reason: '${status.name} is hidden from the board');
    }
  });

  testWidgets('the reminders banner surfaces an overdue reminder',
      (tester) async {
    await tester.runAsync(() async {
      final task = await controller.createTask(summary: 'Call the vet');
      await controller.addReminder(
        controller.taskById(task.id)!,
        DateTime.now().subtract(const Duration(hours: 2)),
      );
    });

    await useDesktopWindow(tester);
    await tester.pumpWidget(harness());
    await tester.pumpAndSettle();

    expect(find.textContaining('Call the vet'), findsWidgets);
    expect(find.text('Dismiss all'), findsOneWidget);

    await tester.tap(find.text('Dismiss all'));
    await settleWithDisk(tester);

    expect(find.text('Dismiss all'), findsNothing);
  });
}
