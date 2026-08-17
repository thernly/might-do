// Holds the on-disk format still.
//
// `fixtures/` is the portable definition of a might-do workspace — the thing a
// reimplementation is written against (see `docs/format/workspace-v1.md`). This
// test proves the corpus still describes what this app actually does, so it
// can't quietly drift out of date and start lying to the port.
//
// The bar is *semantic* equivalence, not byte-identity: reading a fixture and
// writing it back must preserve every value, but formatting is not pinned.
// Anything that reads and writes the same values is a valid implementation.

import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:might_do/domain/calendar_date.dart';
import 'package:might_do/domain/rank.dart';
import 'package:might_do/domain/task.dart';
import 'package:might_do/domain/workspace_config.dart';
import 'package:might_do/storage/task_store.dart';
import 'package:might_do/storage/workspace.dart';
import 'package:path/path.dart' as p;

Map<String, dynamic> _readJson(String path) =>
    (jsonDecode(File(path).readAsStringSync()) as Map).cast<String, dynamic>();

/// Round-trips through the encoder so the comparison is value-based: two trees
/// holding the same values match however their source files were formatted.
Map<String, dynamic> _normalise(Map<String, dynamic> json) =>
    (jsonDecode(jsonEncode(json)) as Map).cast<String, dynamic>();

void main() {
  const fixtures = 'fixtures';
  final canonical = p.join(fixtures, 'workspace-v1');

  group('canonical workspace', () {
    test('every task file survives a read/write round-trip unchanged', () {
      final dir = Directory(p.join(canonical, 'tasks'));
      final files = dir
          .listSync()
          .whereType<File>()
          .where((f) => isOwnTaskFile(p.basename(f.path)))
          .toList();

      expect(files, hasLength(5), reason: 'fixture corpus lost a task file');

      for (final file in files) {
        final original = _readJson(file.path);
        final rewritten = Task.fromJson(original).toJson();
        expect(
          _normalise(rewritten),
          equals(_normalise(original)),
          reason: '${p.basename(file.path)} did not round-trip',
        );
      }
    });

    test('config survives a read/write round-trip unchanged', () {
      final original = _readJson(p.join(canonical, 'config.json'));
      expect(
        _normalise(WorkspaceConfig.fromJson(original).toJson()),
        equals(_normalise(original)),
      );
    });

    test('the corpus covers the cases a port is likely to get wrong', () {
      final maximal = Task.fromJson(
        _readJson(p.join(canonical, 'tasks', '01m07z000000000000000000t1.json')),
      );
      expect(maximal.tagIds, hasLength(Task.maxTags));
      expect(maximal.steps.where((s) => s.done), isNotEmpty);
      expect(maximal.steps.where((s) => !s.done), isNotEmpty);
      expect(maximal.attachments, isNotEmpty);
      expect(maximal.reminders.where((r) => r.isPending), isNotEmpty);
      expect(
        maximal.reminders.where((r) => !r.isPending && r.isOutstanding),
        isNotEmpty,
        reason: 'need a fired-but-not-dismissed reminder',
      );
      expect(maximal.reminders.where((r) => !r.isOutstanding), isNotEmpty);
      expect(maximal.estimateVariance, 75);

      final minimal = Task.fromJson(
        _readJson(p.join(canonical, 'tasks', '01m07z000000000000000000t2.json')),
      );
      expect(minimal.categoryId, isNull);
      expect(minimal.dueDate, isNull);
      expect(minimal.tagIds, isEmpty);
      expect(minimal.notes, isEmpty);

      // A task in a Final status carries a completion date.
      final config = WorkspaceConfig.fromJson(
        _readJson(p.join(canonical, 'config.json')),
      );
      final completed = Task.fromJson(
        _readJson(p.join(canonical, 'tasks', '01m07z000000000000000000t4.json')),
      );
      expect(config.isFinal(completed.statusId), isTrue);
      expect(completed.completedAt, isNotNull);

      // Category colours are ARGB and overflow a signed 32-bit int.
      final work = config.categories.first;
      expect(work.color, greaterThan(0x7FFFFFFF));
    });

    test('loading the workspace finds the tasks and reports the conflicts',
        () async {
      final temp = await Directory.systemTemp.createTemp('might_do_fixture');
      addTearDown(() => temp.delete(recursive: true));
      _copyDirectory(Directory(canonical), temp);

      final loaded = await TaskStore(Workspace(temp)).load();

      expect(loaded.failures, isEmpty,
          reason: 'a fixture task file failed to parse');
      expect(loaded.tasks, hasLength(5));

      final expected =
          _readJson(p.join(fixtures, 'vectors', 'conflicts.json'))['files']
              as List;
      expect(loaded.conflicts, hasLength(expected.length));
      for (final entry in expected.cast<Map<String, dynamic>>()) {
        final found = loaded.conflicts
            .where((c) => c.fileName == entry['fileName'])
            .toList();
        expect(found, hasLength(1),
            reason: '${entry['fileName']} was not reported as a conflict');
        expect(found.single.taskId, entry['taskId']);
      }

      final trashed = await TaskStore(Workspace(temp)).loadTrash();
      expect(trashed, hasLength(1));
      expect(trashed.single.id, '01m07z000000000000000000t6');

      // The attachment's bytes are present, not just its metadata.
      final attachment = loaded.tasks
          .expand((t) => t.attachments)
          .single;
      expect(
        File(p.join(temp.path, 'attachments', attachment.storedName))
            .existsSync(),
        isTrue,
      );
    });
  });

  group('tolerance', () {
    for (final name in ['sparse', 'offset-timestamps', 'future-keys']) {
      test('$name normalises to the canonical form', () {
        final input = _readJson(p.join(fixtures, 'tolerance', 'input', '$name.json'));
        final expected =
            _readJson(p.join(fixtures, 'tolerance', 'expected', '$name.json'));
        expect(
          _normalise(Task.fromJson(input).toJson()),
          equals(_normalise(expected)),
        );
      });
    }

    test('config-unsorted normalises to the canonical form', () {
      final input =
          _readJson(p.join(fixtures, 'tolerance', 'input', 'config-unsorted.json'));
      final expected = _readJson(
          p.join(fixtures, 'tolerance', 'expected', 'config-unsorted.json'));
      expect(
        _normalise(WorkspaceConfig.fromJson(input).toJson()),
        equals(_normalise(expected)),
      );
    });
  });

  group('vectors', () {
    test('ranks', () {
      final vector = _readJson(p.join(fixtures, 'vectors', 'ranks.json'));

      for (final c in (vector['between'] as List).cast<Map<String, dynamic>>()) {
        final before = c['before'] as String;
        final after = c['after'] as String;
        final result = rankBetween(before, after);
        expect(result, c['result'], reason: 'rankBetween($before, $after)');

        // The property the vector exists to protect.
        if (before.isNotEmpty) expect(before.compareTo(result), lessThan(0));
        if (after.isNotEmpty) expect(result.compareTo(after), lessThan(0));
        expect(result.endsWith('0'), isFalse);
      }

      final initial = vector['initialRanks'] as Map<String, dynamic>;
      expect(initialRanks(initial['count'] as int), initial['result']);

      for (final c in (vector['rejected'] as List).cast<Map<String, dynamic>>()) {
        expect(
          () => rankBetween(c['before'] as String, c['after'] as String),
          throwsArgumentError,
          reason: c['why'] as String,
        );
      }
    });

    test('calendar dates', () {
      final vector = _readJson(p.join(fixtures, 'vectors', 'calendar-dates.json'));
      for (final c in (vector['cases'] as List).cast<Map<String, dynamic>>()) {
        expect(
          CalendarDate.tryParse(c['input'] as String)?.toIso(),
          c['parsed'],
          reason: 'tryParse(${c['input']})',
        );
      }
    });

    test('filenames', () {
      final vector = _readJson(p.join(fixtures, 'vectors', 'filenames.json'));
      for (final c in (vector['cases'] as List).cast<Map<String, dynamic>>()) {
        final name = c['fileName'] as String;
        expect(isOwnTaskFile(name), c['isOwnTaskFile'],
            reason: 'isOwnTaskFile($name)');
        expect(!isOwnTaskFile(name) && !name.endsWith('.tmp'),
            c['isConflictArtefact'],
            reason: 'conflict classification of $name');
      }
    });
  });
}

void _copyDirectory(Directory from, Directory to) {
  for (final entity in from.listSync(recursive: true)) {
    final relative = p.relative(entity.path, from: from.path);
    final target = p.join(to.path, relative);
    if (entity is Directory) {
      Directory(target).createSync(recursive: true);
    } else if (entity is File) {
      Directory(p.dirname(target)).createSync(recursive: true);
      entity.copySync(target);
    }
  }
}
