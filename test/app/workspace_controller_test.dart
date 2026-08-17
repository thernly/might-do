import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:might_do/app/workspace_controller.dart';
import 'package:might_do/domain/status.dart';
import 'package:might_do/domain/task.dart';
import 'package:might_do/storage/task_store.dart';
import 'package:might_do/storage/workspace.dart';

void main() {
  late Directory root;
  late WorkspaceController controller;

  setUp(() async {
    root = await Directory.systemTemp.createTemp('might_do_ctrl');
    controller = WorkspaceController(TaskStore(Workspace(root)), watchForChanges: false);
    await controller.open();
  });

  tearDown(() async {
    controller.dispose();
    if (await root.exists()) await root.delete(recursive: true);
  });

  Status statusOfType(StatusType type) =>
      controller.config.statuses.firstWhere((s) => s.type == type);

  group('creating tasks', () {
    test('starts in the default Initial status with no completion date', () async {
      final task = await controller.createTask(summary: 'Write the thing');

      expect(task.statusId, controller.config.defaultStatusId);
      expect(
        controller.config.statusById(task.statusId)!.type,
        StatusType.initial,
      );
      expect(task.completedAt, isNull);
      expect(task.isComplete, isFalse);
    });

    test('caps tags at the documented maximum', () async {
      final task = await controller.createTask(
        summary: 'Over-tagged',
        tagIds: List.generate(15, (i) => 'tag-$i'),
      );
      expect(task.tagIds, hasLength(Task.maxTags));
    });

    test('appends to the bottom of its column', () async {
      final first = await controller.createTask(summary: 'First');
      final second = await controller.createTask(summary: 'Second');
      expect(first.boardRank.compareTo(second.boardRank), lessThan(0));
    });
  });

  group('completion date follows the status type', () {
    test('is stamped on entering any Final status', () async {
      final task = await controller.createTask(summary: 'Finish');
      final done = statusOfType(StatusType.finalType);

      await controller.moveToStatus(task, done.id);

      expect(controller.taskById(task.id)!.completedAt, isNotNull);
      expect(controller.taskById(task.id)!.isComplete, isTrue);
    });

    test('is cleared on leaving a Final status', () async {
      final task = await controller.createTask(summary: 'Reopened');
      await controller.moveToStatus(task, statusOfType(StatusType.finalType).id);
      await controller.moveToStatus(
        controller.taskById(task.id)!,
        statusOfType(StatusType.active).id,
      );

      expect(controller.taskById(task.id)!.completedAt, isNull);
    });

    test('is preserved when moving between two Final statuses', () async {
      final finals = controller.config.statuses
          .where((s) => s.type == StatusType.finalType)
          .toList();
      expect(finals.length, greaterThanOrEqualTo(2),
          reason: 'seed should provide Done and Abandoned');

      final task = await controller.createTask(summary: 'Done then abandoned');
      await controller.moveToStatus(task, finals[0].id);
      final stampedAt = controller.taskById(task.id)!.completedAt;

      await controller.moveToStatus(controller.taskById(task.id)!, finals[1].id);

      expect(controller.taskById(task.id)!.completedAt, stampedAt,
          reason: 'the moment it concluded did not change');
    });

    test('is not set by Active statuses', () async {
      final task = await controller.createTask(summary: 'Working');
      await controller.moveToStatus(task, statusOfType(StatusType.active).id);
      expect(controller.taskById(task.id)!.completedAt, isNull);
    });
  });

  group('board reordering', () {
    test('drops a task between two others', () async {
      final a = await controller.createTask(summary: 'A');
      final b = await controller.createTask(summary: 'B');
      final c = await controller.createTask(summary: 'C');

      await controller.reorderOnBoard(
        task: c,
        statusId: a.statusId,
        above: a,
        below: b,
      );

      final ordered = controller.tasks.toList()
        ..sort((x, y) => x.boardRank.compareTo(y.boardRank));
      expect(ordered.map((t) => t.summary), ['A', 'C', 'B']);
    });

    test('moving to another column changes status and stamps completion',
        () async {
      final task = await controller.createTask(summary: 'Drag me');
      final done = statusOfType(StatusType.finalType);

      await controller.reorderOnBoard(task: task, statusId: done.id);

      final moved = controller.taskById(task.id)!;
      expect(moved.statusId, done.id);
      expect(moved.completedAt, isNotNull);
    });
  });

  group('deleting a status', () {
    test('is blocked for the default status', () async {
      final blocker =
          controller.statusDeletionBlocker(controller.config.defaultStatusId);
      expect(blocker, isNotNull);
      expect(blocker, contains('new tasks start in'));
    });

    test('is blocked when it is the last of its type', () async {
      final actives = controller.config.statuses
          .where((s) => s.type == StatusType.active)
          .toList();
      for (var i = 0; i < actives.length - 1; i++) {
        await controller.deleteStatus(
          actives[i].id,
          reassignTo: actives.last.id,
        );
      }

      final last = controller.config.statuses
          .firstWhere((s) => s.type == StatusType.active);
      expect(controller.statusDeletionBlocker(last.id), contains('only Active'));
      expect(
        () => controller.deleteStatus(last.id, reassignTo: last.id),
        throwsStateError,
      );
    });

    test('moves affected tasks to the replacement rather than deleting them',
        () async {
      final blocked = await controller.addStatus('Blocked', StatusType.active);
      final task = await controller.createTask(summary: 'Stuck');
      await controller.moveToStatus(task, blocked.id);

      final replacement = statusOfType(StatusType.active);
      await controller.deleteStatus(blocked.id, reassignTo: replacement.id);

      expect(controller.config.statusById(blocked.id), isNull);
      expect(controller.tasks, hasLength(1));
      expect(controller.taskById(task.id)!.statusId, replacement.id);
    });

    test('applies the completion rule when reassigning', () async {
      final extra = await controller.addStatus('Shipped', StatusType.active);
      final task = await controller.createTask(summary: 'Ship it');
      await controller.moveToStatus(task, extra.id);
      expect(controller.taskById(task.id)!.completedAt, isNull);

      await controller.deleteStatus(
        extra.id,
        reassignTo: statusOfType(StatusType.finalType).id,
      );

      expect(controller.taskById(task.id)!.completedAt, isNotNull,
          reason: 'reassignment into a Final status is still a completion');
    });

    test('renumbers the remaining statuses so board order stays contiguous',
        () async {
      final extra = await controller.addStatus('Temporary', StatusType.active);
      await controller.deleteStatus(
        extra.id,
        reassignTo: statusOfType(StatusType.active).id,
      );

      final orders =
          controller.config.orderedStatuses.map((s) => s.order).toList();
      expect(orders, List.generate(orders.length, (i) => i));
    });
  });

  group('default status', () {
    test('must be an Initial status', () async {
      expect(
        () => controller.setDefaultStatus(statusOfType(StatusType.active).id),
        throwsArgumentError,
      );
    });

    test('can move to another Initial status', () async {
      final backlog = controller.config.statuses.firstWhere(
        (s) => s.type == StatusType.initial && s.id != controller.config.defaultStatusId,
      );
      await controller.setDefaultStatus(backlog.id);
      expect(controller.config.defaultStatusId, backlog.id);
    });
  });

  group('categories and tags', () {
    test('deleting a category clears it from tasks by default', () async {
      final category = await controller.addCategory('Home', 0xFF00FF00);
      final task = await controller.createTask(
        summary: 'Fix the door',
        categoryId: category.id,
      );

      await controller.deleteCategory(category.id);

      expect(controller.config.categories, isEmpty);
      expect(controller.taskById(task.id)!.categoryId, isNull);
      expect(controller.tasks, hasLength(1), reason: 'the task survives');
    });

    test('deleting a category can reassign instead', () async {
      final from = await controller.addCategory('Old', 0xFF00FF00);
      final to = await controller.addCategory('New', 0xFF0000FF);
      final task = await controller.createTask(
        summary: 'Move me',
        categoryId: from.id,
      );

      await controller.deleteCategory(from.id, reassignTo: to.id);

      expect(controller.taskById(task.id)!.categoryId, to.id);
    });

    test('adding an existing tag by name reuses it', () async {
      final first = await controller.addTag('urgent');
      final second = await controller.addTag('URGENT');
      expect(second.id, first.id);
      expect(controller.config.tags, hasLength(1));
    });

    test('deleting a tag detaches it from every task', () async {
      final tag = await controller.addTag('waiting');
      final task = await controller.createTask(
        summary: 'Tagged',
        tagIds: [tag.id],
      );

      await controller.deleteTag(tag.id);

      expect(controller.config.tags, isEmpty);
      expect(controller.taskById(task.id)!.tagIds, isEmpty);
    });
  });

  group('steps, notes and reminders', () {
    test('ticking every step does not complete the task', () async {
      final task = await controller.createTask(summary: 'Multi-step');
      await controller.addStep(controller.taskById(task.id)!, 'One');
      await controller.addStep(controller.taskById(task.id)!, 'Two');

      for (final step in controller.taskById(task.id)!.steps) {
        await controller.setStepDone(
          controller.taskById(task.id)!,
          step.id,
          true,
        );
      }

      final updated = controller.taskById(task.id)!;
      expect(updated.stepsDone, 2);
      expect(updated.isComplete, isFalse,
          reason: 'steps are not a completion mechanism');
    });

    test('notes accumulate with timestamps', () async {
      final task = await controller.createTask(summary: 'Logged');
      await controller.addNote(controller.taskById(task.id)!, 'First');
      await controller.addNote(controller.taskById(task.id)!, 'Second');

      final notes = controller.taskById(task.id)!.notes;
      expect(notes.map((n) => n.body), ['First', 'Second']);
      expect(notes.every((n) => n.createdAt.isUtc), isTrue);
    });

    test('a dismissed reminder stops being outstanding', () async {
      final task = await controller.createTask(summary: 'Remind me');
      await controller.addReminder(
        controller.taskById(task.id)!,
        DateTime.now().subtract(const Duration(hours: 1)),
      );

      final due = controller.taskById(task.id)!.outstandingReminders(
            DateTime.now(),
          );
      expect(due, hasLength(1));

      await controller.dismissReminder(
        controller.taskById(task.id)!,
        due.single.id,
      );

      expect(
        controller.taskById(task.id)!.outstandingReminders(DateTime.now()),
        isEmpty,
      );
    });

    test('a future reminder is not outstanding yet', () async {
      final task = await controller.createTask(summary: 'Later');
      await controller.addReminder(
        controller.taskById(task.id)!,
        DateTime.now().add(const Duration(days: 1)),
      );

      expect(
        controller.taskById(task.id)!.outstandingReminders(DateTime.now()),
        isEmpty,
      );
    });
  });

  test('trashing removes the task from the working set', () async {
    final task = await controller.createTask(summary: 'Mistake');
    await controller.trashTask(task);

    expect(controller.tasks, isEmpty);
    expect(controller.taskById(task.id), isNull);
  });

  test('changes survive a reload from disk', () async {
    final task = await controller.createTask(summary: 'Persisted');
    await controller.moveToStatus(task, statusOfType(StatusType.active).id);
    await controller.addNote(controller.taskById(task.id)!, 'A note');

    final reopened = WorkspaceController(TaskStore(Workspace(root)), watchForChanges: false);
    await reopened.open();
    addTearDown(reopened.dispose);

    final loaded = reopened.taskById(task.id)!;
    expect(loaded.summary, 'Persisted');
    expect(loaded.notes.single.body, 'A note');
    expect(
      reopened.config.statusById(loaded.statusId)!.type,
      StatusType.active,
    );
  });
}
