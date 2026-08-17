// Proves the two implementations can share a folder.
//
// `fixture_conformance_test.dart` covers one direction: this app wrote
// `fixtures/workspace-v1/`, and the .NET port reads it and writes it back
// without losing a value. That leaves the other direction assumed rather than
// verified — and "assumed" is not good enough for a format whose whole point is
// that two builds of the application meet in a synced folder.
//
// `fixtures/interop/dotnet-written/` is written by MightDo.Core (see
// `tools/MightDo.FixtureWriter`). This test reads it with *this* implementation
// and asserts nothing is lost on the way in.

import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:might_do/domain/task.dart';
import 'package:might_do/domain/workspace_config.dart';
import 'package:might_do/storage/task_store.dart';
import 'package:might_do/storage/workspace.dart';
import 'package:path/path.dart' as p;

Map<String, dynamic> _readJson(String path) =>
    (jsonDecode(File(path).readAsStringSync()) as Map).cast<String, dynamic>();

Map<String, dynamic> _normalise(Map<String, dynamic> json) =>
    (jsonDecode(jsonEncode(json)) as Map).cast<String, dynamic>();

void main() {
  const canonical = 'fixtures/workspace-v1';
  const written = 'fixtures/interop/dotnet-written';

  /// The task ids present in both corpora. `n1` exists only in the .NET one —
  /// it is a task that implementation created from scratch rather than
  /// re-serialised, so there is no canonical counterpart to compare it against.
  const shared = [
    '01m07z000000000000000000t1',
    '01m07z000000000000000000t2',
    '01m07z000000000000000000t3',
    '01m07z000000000000000000t4',
    '01m07z000000000000000000t5',
  ];

  group('a workspace written by the .NET implementation', () {
    test('exists — regenerate with tools/MightDo.FixtureWriter if not', () {
      expect(Directory(p.join(written, 'tasks')).existsSync(), isTrue,
          reason: 'run: dotnet run --project tools/MightDo.FixtureWriter');
    });

    for (final id in shared) {
      test('$id carries the same values as ours', () {
        final theirs = _readJson(p.join(written, 'tasks', '$id.json'));
        final ours = _readJson(p.join(canonical, 'tasks', '$id.json'));

        // Parse their file with our reader, write it back with our writer, and
        // it must land on our canonical form. Anything they dropped, renamed or
        // reinterpreted shows up here.
        expect(
          _normalise(Task.fromJson(theirs).toJson()),
          equals(_normalise(ours)),
          reason: 'a value was lost reading the .NET implementation\'s $id',
        );
      });
    }

    test('config.json carries the same values as ours', () {
      final theirs = _readJson(p.join(written, 'config.json'));
      final ours = _readJson(p.join(canonical, 'config.json'));

      expect(
        _normalise(WorkspaceConfig.fromJson(theirs).toJson()),
        equals(_normalise(ours)),
      );
    });

    test('a task it created from scratch reads correctly', () {
      final task = Task.fromJson(
        _readJson(p.join(written, 'tasks', '01m07z000000000000000000n1.json')),
      );

      // Not a round-trip of one of our files — this one has no counterpart, so
      // it exercises their writer rather than their reader.
      expect(task.summary, contains('🎉'));
      expect(task.description, contains('<angle brackets>'));
      expect(task.description, contains('café'));
      expect(task.priority, Priority.high);
      expect(task.dueDate?.toIso(), '2026-09-01');
      expect(task.estimateMinutes, 45);
      expect(task.steps.single.done, isTrue);
      expect(task.notes.single.createdAt.isUtc, isTrue);
      expect(task.reminders.single.isPending, isTrue);
      expect(task.tagIds, hasLength(2));

      // Their rank generator must produce something ours accepts and orders.
      expect(task.boardRank.compareTo('h'), greaterThan(0));
      expect(task.boardRank.compareTo('i'), lessThan(0));

      // And it must survive our writer unchanged.
      expect(
        _normalise(Task.fromJson(task.toJson()).toJson()),
        equals(_normalise(task.toJson())),
      );
    });

    test('loads as a whole workspace, with the trash kept separate', () async {
      final temp = await Directory.systemTemp.createTemp('might_do_interop');
      addTearDown(() => temp.delete(recursive: true));
      _copyDirectory(Directory(written), temp);

      final store = TaskStore(Workspace(temp));
      final loaded = await store.load();

      expect(loaded.failures, isEmpty,
          reason: 'a .NET-written file failed to parse');
      expect(loaded.tasks, hasLength(6));
      expect(loaded.conflicts, isEmpty);

      final trashed = await store.loadTrash();
      expect(trashed.single.id, '01m07z000000000000000000t6');
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
