import 'package:flutter_test/flutter_test.dart';
import 'package:might_do/app/task_query.dart';
import 'package:might_do/domain/calendar_date.dart';
import 'package:might_do/domain/rank.dart';
import 'package:might_do/domain/status.dart';
import 'package:might_do/domain/task.dart';
import 'package:might_do/domain/workspace_config.dart';

void main() {
  final config = WorkspaceConfig.seed();
  final initial =
      config.statuses.firstWhere((s) => s.type == StatusType.initial);
  final active = config.statuses.firstWhere((s) => s.type == StatusType.active);
  final done =
      config.statuses.firstWhere((s) => s.type == StatusType.finalType);

  Task task(
    String summary, {
    Status? status,
    Priority priority = Priority.medium,
    CalendarDate? due,
    String description = '',
    List<Note> notes = const [],
    List<Step> steps = const [],
    String? categoryId,
    List<String> tagIds = const [],
    bool complete = false,
  }) {
    final base = Task.create(
      summary: summary,
      statusId: (status ?? initial).id,
      boardRank: rankBetween('', ''),
      priority: priority,
      dueDate: due,
      description: description,
      categoryId: categoryId,
      tagIds: tagIds,
    );
    return base.copyWith(
      notes: notes,
      steps: steps,
      completedAt: complete ? DateTime.now().toUtc() : null,
    );
  }

  group('completed tasks', () {
    test('are hidden by default', () {
      final tasks = [
        task('Open'),
        task('Shipped', status: done, complete: true),
      ];
      final result = const TaskQuery().apply(tasks, config);
      expect(result.map((t) => t.summary), ['Open']);
    });

    test('appear when asked for', () {
      final tasks = [
        task('Open'),
        task('Shipped', status: done, complete: true),
      ];
      final result =
          const TaskQuery(includeCompleted: true).apply(tasks, config);
      expect(result, hasLength(2));
    });

    test('appear when explicitly filtered to a Final status', () {
      final tasks = [
        task('Open'),
        task('Shipped', status: done, complete: true),
      ];
      final result = TaskQuery(statusIds: {done.id}).apply(tasks, config);
      expect(result.map((t) => t.summary), ['Shipped']);
    });
  });

  group('search', () {
    test('matches the summary', () {
      final tasks = [task('Renew passport'), task('Buy milk')];
      expect(
        const TaskQuery(search: 'passport').apply(tasks, config).single.summary,
        'Renew passport',
      );
    });

    test('matches the description, notes and steps', () {
      final tasks = [
        task('One', description: 'involves a dentist'),
        task('Two', notes: [Note.create('called the plumber')]),
        task('Three', steps: [Step.create('book the electrician')]),
        task('Four'),
      ];

      expect(const TaskQuery(search: 'dentist').apply(tasks, config), hasLength(1));
      expect(const TaskQuery(search: 'plumber').apply(tasks, config), hasLength(1));
      expect(
        const TaskQuery(search: 'electrician').apply(tasks, config),
        hasLength(1),
      );
    });

    test('is case-insensitive and requires every term', () {
      final tasks = [
        task('Renew UK passport'),
        task('Renew library card'),
      ];
      expect(
        const TaskQuery(search: 'renew PASSPORT').apply(tasks, config),
        hasLength(1),
      );
      expect(
        const TaskQuery(search: 'renew').apply(tasks, config),
        hasLength(2),
      );
    });

    test('ignores surrounding whitespace', () {
      final tasks = [task('Renew passport')];
      expect(
        const TaskQuery(search: '   passport   ').apply(tasks, config),
        hasLength(1),
      );
    });
  });

  group('filters', () {
    test('by status type', () {
      final tasks = [
        task('Waiting'),
        task('Doing', status: active),
      ];
      final result = TaskQuery(statusTypes: {StatusType.active})
          .apply(tasks, config);
      expect(result.map((t) => t.summary), ['Doing']);
    });

    test('by priority', () {
      final tasks = [
        task('Meh', priority: Priority.low),
        task('Now', priority: Priority.critical),
      ];
      final result =
          const TaskQuery(priorities: {Priority.critical}).apply(tasks, config);
      expect(result.map((t) => t.summary), ['Now']);
    });

    test('by tag, matching any of the selected tags', () {
      final tasks = [
        task('A', tagIds: ['t1']),
        task('B', tagIds: ['t2']),
        task('C', tagIds: ['t3']),
      ];
      final result = TaskQuery(tagIds: {'t1', 't2'}).apply(tasks, config);
      expect(result.map((t) => t.summary), containsAll(['A', 'B']));
      expect(result, hasLength(2));
    });

    test('overdue only excludes future and undated tasks', () {
      final tasks = [
        task('Late', due: CalendarDate.today().addDays(-2)),
        task('Soon', due: CalendarDate.today().addDays(2)),
        task('Undated'),
      ];
      final result = const TaskQuery(overdueOnly: true).apply(tasks, config);
      expect(result.map((t) => t.summary), ['Late']);
    });

    test('a task due today is not yet overdue', () {
      final tasks = [task('Today', due: CalendarDate.today())];
      expect(const TaskQuery(overdueOnly: true).apply(tasks, config), isEmpty);
    });

    test('combine as AND', () {
      final tasks = [
        task('Match', priority: Priority.high, status: active),
        task('Wrong priority', priority: Priority.low, status: active),
        task('Wrong status', priority: Priority.high),
      ];
      final result = TaskQuery(
        priorities: const {Priority.high},
        statusTypes: {StatusType.active},
      ).apply(tasks, config);
      expect(result.map((t) => t.summary), ['Match']);
    });
  });

  group('sorting', () {
    test('smart sort puts overdue first, then priority, then due date', () {
      final tasks = [
        task('Low, undated', priority: Priority.low),
        task('Critical, later',
            priority: Priority.critical, due: CalendarDate.today().addDays(5)),
        task('Critical, sooner',
            priority: Priority.critical, due: CalendarDate.today().addDays(1)),
        task('Overdue, low',
            priority: Priority.low, due: CalendarDate.today().addDays(-1)),
      ];

      final result = const TaskQuery().apply(tasks, config);
      expect(result.first.summary, 'Overdue, low');
      expect(
        result.map((t) => t.summary).toList().sublist(1),
        ['Critical, sooner', 'Critical, later', 'Low, undated'],
      );
    });

    test('undated tasks sort after dated ones', () {
      final tasks = [
        task('Undated'),
        task('Dated', due: CalendarDate.today().addDays(3)),
      ];
      final result =
          const TaskQuery(sort: TaskSort.dueDate).apply(tasks, config);
      expect(result.map((t) => t.summary), ['Dated', 'Undated']);
    });

    test('summary sort is case-insensitive', () {
      final tasks = [task('banana'), task('Apple'), task('cherry')];
      final result =
          const TaskQuery(sort: TaskSort.summary).apply(tasks, config);
      expect(result.map((t) => t.summary), ['Apple', 'banana', 'cherry']);
    });
  });

  group('isFiltered', () {
    test('is false for a fresh query', () {
      expect(const TaskQuery().isFiltered, isFalse);
    });

    test('is true once anything is set', () {
      expect(const TaskQuery(search: 'x').isFiltered, isTrue);
      expect(const TaskQuery(overdueOnly: true).isFiltered, isTrue);
      expect(const TaskQuery(includeCompleted: true).isFiltered, isTrue);
    });

    test('sorting alone is not filtering', () {
      expect(const TaskQuery(sort: TaskSort.summary).isFiltered, isFalse);
    });
  });
}
