import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:might_do/domain/calendar_date.dart';
import 'package:might_do/domain/rank.dart';
import 'package:might_do/domain/status.dart';
import 'package:might_do/domain/task.dart';
import 'package:might_do/storage/task_store.dart';
import 'package:might_do/storage/workspace.dart';
import 'package:path/path.dart' as p;

void main() {
  late Directory root;
  late Workspace workspace;
  late TaskStore store;

  setUp(() async {
    root = await Directory.systemTemp.createTemp('might_do_test');
    workspace = Workspace(root);
    store = TaskStore(workspace);
  });

  tearDown(() async {
    if (await root.exists()) await root.delete(recursive: true);
  });

  Task sampleTask(String summary, {String statusId = 'status-1'}) =>
      Task.create(
        summary: summary,
        statusId: statusId,
        boardRank: rankBetween('', ''),
      );

  group('initialise', () {
    test('creates the folder layout and seeds a config', () async {
      final config = await store.initialise();

      expect(await workspace.tasksDir.exists(), isTrue);
      expect(await workspace.attachmentsDir.exists(), isTrue);
      expect(await workspace.trashTasksDir.exists(), isTrue);
      expect(await workspace.configFile.exists(), isTrue);

      expect(config.statuses, isNotEmpty);
      expect(
        config.statusById(config.defaultStatusId)?.type,
        StatusType.initial,
        reason: 'new tasks must start in an Initial status',
      );
      expect(
        config.statuses.map((s) => s.type).toSet(),
        containsAll(StatusType.values),
        reason: 'seed must cover all three types',
      );
    });

    test('does not overwrite an existing config', () async {
      final first = await store.initialise();
      final renamed = first.copyWith(
        statuses: [first.statuses.first.copyWith(name: 'Renamed')],
        defaultStatusId: first.statuses.first.id,
      );
      await store.saveConfig(renamed);

      final reloaded = await TaskStore(Workspace(root)).initialise();
      expect(reloaded.statuses.single.name, 'Renamed');
    });
  });

  group('saving and loading tasks', () {
    test('round-trips every field', () async {
      await store.initialise();

      final original = sampleTask('Renew passport').copyWith(
        description: 'Photos first, then the form.',
        dueDate: const CalendarDate(2026, 9, 30),
        priority: Priority.high,
        estimateMinutes: 90,
        totalTimeMinutes: 145,
        categoryId: 'cat-1',
        tagIds: ['tag-a', 'tag-b'],
        steps: [Step.create('Get photos'), Step.create('Fill in form')],
        notes: [Note.create('Booked an appointment.')],
        reminders: [Reminder.create(DateTime.utc(2026, 9, 25, 9))],
      );

      await store.saveTask(original);
      final loaded = await store.loadTask(original.id);

      expect(loaded, isNotNull);
      expect(loaded!.id, original.id);
      expect(loaded.summary, 'Renew passport');
      expect(loaded.description, 'Photos first, then the form.');
      expect(loaded.dueDate, const CalendarDate(2026, 9, 30));
      expect(loaded.priority, Priority.high);
      expect(loaded.estimateMinutes, 90);
      expect(loaded.totalTimeMinutes, 145);
      expect(loaded.estimateVariance, 55);
      expect(loaded.categoryId, 'cat-1');
      expect(loaded.tagIds, ['tag-a', 'tag-b']);
      expect(loaded.steps.map((s) => s.text), ['Get photos', 'Fill in form']);
      expect(loaded.notes.single.body, 'Booked an appointment.');
      expect(loaded.reminders.single.remindAt, DateTime.utc(2026, 9, 25, 9));
      expect(loaded.boardRank, original.boardRank);
    });

    test('writes a due date as a bare day, not an instant', () async {
      await store.initialise();
      final task = sampleTask('Pay rent')
          .copyWith(dueDate: const CalendarDate(2026, 8, 21));
      await store.saveTask(task);

      final raw = jsonDecode(await workspace.taskFile(task.id).readAsString())
          as Map<String, dynamic>;
      expect(raw['dueDate'], '2026-08-21');
      expect(raw['dueDate'], isNot(contains('T')));
    });

    test('names files by task id and loads them all back', () async {
      await store.initialise();
      for (final summary in ['One', 'Two', 'Three']) {
        await store.saveTask(sampleTask(summary));
      }

      final loaded = await store.load();
      expect(loaded.tasks.map((t) => t.summary).toSet(), {
        'One',
        'Two',
        'Three',
      });
      expect(loaded.failures, isEmpty);
      expect(loaded.conflicts, isEmpty);
    });

    test('reports unparseable files instead of dropping them', () async {
      await store.initialise();
      final good = sampleTask('Fine');
      await store.saveTask(good);

      // A real ULID filename holding rubbish.
      final broken = sampleTask('Broken');
      await workspace.taskFile(broken.id).writeAsString('{not json');

      final loaded = await store.load();
      expect(loaded.tasks.map((t) => t.summary), ['Fine']);
      expect(loaded.failures, hasLength(1));
      expect(loaded.failures.single.fileName, '${broken.id}.json');
    });

    test('leaves no temporary files behind', () async {
      await store.initialise();
      await store.saveTask(sampleTask('Tidy'));

      final names = await workspace.tasksDir
          .list()
          .map((e) => p.basename(e.path))
          .toList();
      expect(names.where((n) => n.endsWith('.tmp')), isEmpty);
    });
  });

  group('conflict detection', () {
    test('flags sync-client copies without treating them as tasks', () async {
      await store.initialise();
      final task = sampleTask('Original');
      await store.saveTask(task);

      // The three shapes the major sync clients actually produce.
      final copies = [
        '${task.id}-LAPTOP.json',
        '${task.id} (conflicted copy 2026-08-16).json',
        '${task.id} 2.json',
      ];
      for (final name in copies) {
        await File(p.join(workspace.tasksDir.path, name))
            .writeAsString(jsonEncode(task.toJson()));
      }

      final loaded = await store.load();
      expect(loaded.tasks, hasLength(1),
          reason: 'conflict copies must not load as extra tasks');
      expect(loaded.conflicts, hasLength(3));
      expect(
        loaded.conflicts.map((c) => c.taskId).toSet(),
        {task.id},
        reason: 'the originating task should be recoverable from the name',
      );
    });

    test('ignores our own in-flight temp files', () async {
      await store.initialise();
      final task = sampleTask('Busy');
      await File(p.join(workspace.tasksDir.path, '${task.id}.json.tmp'))
          .writeAsString('{}');

      final loaded = await store.load();
      expect(loaded.conflicts, isEmpty);
    });
  });

  group('trash', () {
    test('moves a task out of the working set but keeps the file', () async {
      await store.initialise();
      final task = sampleTask('Mistake');
      await store.saveTask(task);

      await store.trashTask(task);

      expect(await workspace.taskFile(task.id).exists(), isFalse);
      expect((await store.load()).tasks, isEmpty);
      expect((await store.loadTrash()).single.id, task.id);
    });

    test('restores a trashed task', () async {
      await store.initialise();
      final task = sampleTask('Second thoughts');
      await store.saveTask(task);
      await store.trashTask(task);

      final restored = await store.restoreTask(task.id);

      expect(restored?.summary, 'Second thoughts');
      expect((await store.load()).tasks, hasLength(1));
      expect(await store.loadTrash(), isEmpty);
    });

    test('moves attachments along with the task', () async {
      await store.initialise();
      final task = sampleTask('Has a file').copyWith(attachments: [
        Attachment(
          id: 'att-1',
          originalName: 'contract.pdf',
          storedName: 'att-1-contract.pdf',
          sizeBytes: 4,
          addedAt: DateTime.now().toUtc(),
        ),
      ]);
      await workspace.attachmentFile('att-1-contract.pdf').writeAsString('pdf!');
      await store.saveTask(task);

      await store.trashTask(task);

      expect(
        await workspace.attachmentFile('att-1-contract.pdf').exists(),
        isFalse,
      );
      expect(
        await File(p.join(
          workspace.trashAttachmentsDir.path,
          'att-1-contract.pdf',
        )).exists(),
        isTrue,
      );
    });

    test('does not clobber an identically named file already in the trash',
        () async {
      await store.initialise();
      final task = sampleTask('Recycled');
      await store.saveTask(task);
      await store.trashTask(task);

      // Same id trashed twice — restore, then trash again.
      await store.restoreTask(task.id);
      await store.saveTask(task.copyWith(summary: 'Recycled again'));
      await store.trashTask(task);

      final trashed = await store.loadTrash();
      expect(trashed, hasLength(1));
      final files = await workspace.trashTasksDir.list().toList();
      expect(files, hasLength(1));
    });
  });
}
