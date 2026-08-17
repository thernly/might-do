// Behavioural parity between the two implementations.
//
// `interop_test.dart` proves each can read what the other writes. That is about
// the *format*. This is about *behaviour*: run the same sequence of operations
// through each implementation and the workspaces they leave behind should mean
// the same thing.
//
// The comparison is semantic. Ids are ULIDs and timestamps are real moments, so
// two runs can never be byte-identical; both are normalised away, leaving what
// the user would actually see — names, ordering, completion, board ranks.
//
// This side writes the shared expectation. Regenerate with:
//
//   REGENERATE_PARITY=1 flutter test test/format/parity_test.dart
//
// `MightDo.Core.Tests/ParityTests.cs` runs the same scenario in C# and asserts
// it lands on the same file. If the two implementations ever disagree about
// what an operation means, that test fails.

import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:might_do/app/workspace_controller.dart';
import 'package:might_do/domain/calendar_date.dart';
import 'package:might_do/domain/status.dart';
import 'package:might_do/domain/task.dart';
import 'package:might_do/storage/task_store.dart';
import 'package:might_do/storage/workspace.dart';
import 'package:path/path.dart' as p;

const expectationPath = 'fixtures/parity/scenario.json';

void main() {
  test('the scenario matches the shared expectation', () async {
    final root = await Directory.systemTemp.createTemp('might_do_parity');
    addTearDown(() => root.delete(recursive: true));

    final controller =
        WorkspaceController(TaskStore(Workspace(root)), watchForChanges: false);
    await controller.open();
    addTearDown(controller.dispose);

    await runScenario(controller);
    final actual = normalise(controller);

    const encoder = JsonEncoder.withIndent('  ');
    final rendered = '${encoder.convert(actual)}\n';

    final file = File(expectationPath);
    if (Platform.environment['REGENERATE_PARITY'] == '1') {
      await file.parent.create(recursive: true);
      await file.writeAsString(rendered);
      return;
    }

    expect(file.existsSync(), isTrue,
        reason: 'run with REGENERATE_PARITY=1 to write $expectationPath');

    expect(
      actual,
      equals(jsonDecode(file.readAsStringSync())),
      reason: 'the Flutter implementation drifted from the shared expectation',
    );
  });
}

/// The operations both implementations perform, in order.
///
/// Chosen to exercise the rules most likely to be ported subtly wrong: the
/// completion date following the status *type* in all three directions, board
/// ranks from appending and from dropping between neighbours, tag reuse by
/// name, status deletion reassigning rather than orphaning, and trashing
/// keeping a task out of the working set without destroying it.
Future<void> runScenario(WorkspaceController controller) async {
  Status statusNamed(String name) =>
      controller.config.statuses.firstWhere((s) => s.name == name);

  final work = await controller.addCategory('Work', 0xFF2E7D32);
  final urgent = await controller.addTag('urgent');
  await controller.addTag('URGENT'); // same name, different case: reuses

  final alpha = await controller.createTask(
    summary: 'Alpha',
    description: 'The first one.',
    categoryId: work.id,
    tagIds: [urgent.id],
    estimateMinutes: 60,
  );
  final beta = await controller.createTask(
    summary: 'Beta',
    priority: Priority.high,
    dueDate: const CalendarDate(2026, 9, 1),
  );
  final gamma = await controller.createTask(summary: 'Gamma');

  // Completion follows the status type, in all three directions.
  await controller.moveToStatus(beta, statusNamed('In Progress').id);
  await controller.moveToStatus(
      controller.taskById(beta.id)!, statusNamed('Done').id);
  await controller.moveToStatus(
      controller.taskById(beta.id)!, statusNamed('Blocked').id);

  await controller.addNote(controller.taskById(alpha.id)!, 'Made a start.');
  await controller.addStep(controller.taskById(alpha.id)!, 'Step one');
  await controller.addStep(controller.taskById(alpha.id)!, 'Step two');
  await controller.setStepDone(
    controller.taskById(alpha.id)!,
    controller.taskById(alpha.id)!.steps.first.id,
    true,
  );

  // A manual board move: Gamma above Alpha in the default column.
  await controller.reorderOnBoard(
    task: controller.taskById(gamma.id)!,
    statusId: controller.config.defaultStatusId,
    above: null,
    below: controller.taskById(alpha.id),
  );

  // Adding and removing a status renumbers the rest.
  final review = await controller.addStatus('In Review', StatusType.active);
  await controller.deleteStatus(review.id,
      reassignTo: statusNamed('Blocked').id);

  // Trashed tasks leave the working set without being destroyed.
  final delta = await controller.createTask(summary: 'Delta');
  await controller.trashTask(delta);
}

/// Reduces a workspace to what it means, dropping what cannot match.
///
/// Ids are ULIDs and timestamps are real moments, so both are replaced by the
/// names they stand for or omitted entirely. Completion becomes a flag: whether
/// a task is complete is behaviour, when it completed is a clock reading.
Map<String, dynamic> normalise(WorkspaceController controller) {
  final config = controller.config;

  String? categoryName(String? id) => config.categoryById(id)?.name;
  String statusName(String id) => config.statusById(id)?.name ?? '<unknown>';

  Map<String, dynamic> task(Task t) => {
        'summary': t.summary,
        'description': t.description,
        'status': statusName(t.statusId),
        'category': categoryName(t.categoryId),
        'tags': config.tagsByIds(t.tagIds).map((tag) => tag.name).toList(),
        'priority': t.priority.wire,
        'dueDate': t.dueDate?.toIso(),
        'isComplete': t.isComplete,
        'estimateMinutes': t.estimateMinutes,
        'totalTimeMinutes': t.totalTimeMinutes,
        'boardRank': t.boardRank,
        'steps': t.steps.map((s) => {'text': s.text, 'done': s.done}).toList(),
        'notes': t.notes.map((n) => {'body': n.body}).toList(),
        'reminders': t.reminders
            .map((r) => {'pending': r.isPending, 'outstanding': r.isOutstanding})
            .toList(),
        'attachments': t.attachments
            .map((a) => {'originalName': a.originalName, 'sizeBytes': a.sizeBytes})
            .toList(),
      };

  final tasks = controller.tasks.map(task).toList()
    ..sort((a, b) => (a['summary'] as String).compareTo(b['summary'] as String));

  return {
    'config': {
      'defaultStatus': statusName(config.defaultStatusId),
      'statuses': config.orderedStatuses
          .map((s) => {
                'name': s.name,
                'type': s.type.wire,
                'order': s.order,
                'hiddenFromBoard': s.hiddenFromBoard,
              })
          .toList(),
      'categories':
          config.categories.map((c) => {'name': c.name, 'color': c.color}).toList(),
      'tags': config.tags.map((t) => {'name': t.name}).toList(),
    },
    'tasks': tasks,
    'trashedSummaries': _trashedSummaries(controller),
  };
}

List<String> _trashedSummaries(WorkspaceController controller) {
  final dir = controller.workspace.trashTasksDir;
  if (!dir.existsSync()) return const [];

  final summaries = <String>[];
  for (final entity in dir.listSync()) {
    if (entity is! File) continue;
    if (!isOwnTaskFile(p.basename(entity.path))) continue;

    final json = jsonDecode(entity.readAsStringSync()) as Map;
    summaries.add(json['summary'] as String);
  }

  return summaries..sort();
}
