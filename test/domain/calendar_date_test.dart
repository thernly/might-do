import 'package:flutter_test/flutter_test.dart';
import 'package:might_do/domain/calendar_date.dart';

void main() {
  group('parsing', () {
    test('round-trips an ISO date', () {
      expect(CalendarDate.parse('2026-08-21').toIso(), '2026-08-21');
    });

    test('pads single-digit months and days', () {
      expect(const CalendarDate(2026, 1, 5).toIso(), '2026-01-05');
    });

    test('rejects days that do not exist', () {
      expect(CalendarDate.tryParse('2026-02-30'), isNull);
      expect(CalendarDate.tryParse('2026-13-01'), isNull);
      expect(CalendarDate.tryParse('2026-00-10'), isNull);
    });

    test('rejects anything that is not a bare date', () {
      expect(CalendarDate.tryParse('2026-08-21T00:00:00Z'), isNull);
      expect(CalendarDate.tryParse('21/08/2026'), isNull);
      expect(CalendarDate.tryParse(''), isNull);
      expect(CalendarDate.tryParse(null), isNull);
      expect(() => CalendarDate.parse('nonsense'), throwsFormatException);
    });

    test('accepts a leap day in a leap year', () {
      expect(CalendarDate.tryParse('2028-02-29')?.toIso(), '2028-02-29');
      expect(CalendarDate.tryParse('2027-02-29'), isNull);
    });
  });

  group('timezone safety', () {
    test('a due date never shifts, whatever the local offset', () {
      // The bug this type exists to prevent: storing "due the 21st" as an
      // instant and rendering it west of UTC, which shows the 20th.
      const due = CalendarDate(2026, 8, 21);
      expect(due.toIso(), '2026-08-21');
      expect(due.toLocalDateTime().day, 21);
    });

    test('reads the calendar day from a UTC instant in local terms', () {
      final instant = DateTime.utc(2026, 8, 21, 12);
      final date = CalendarDate.fromLocal(instant);
      final local = instant.toLocal();
      expect(date, CalendarDate(local.year, local.month, local.day));
    });
  });

  group('comparison and arithmetic', () {
    test('orders chronologically', () {
      const earlier = CalendarDate(2026, 8, 21);
      const later = CalendarDate(2026, 9, 1);
      expect(earlier < later, isTrue);
      expect(later > earlier, isTrue);
      expect(earlier <= const CalendarDate(2026, 8, 21), isTrue);
      expect(earlier >= const CalendarDate(2026, 8, 21), isTrue);
    });

    test('sorts a list correctly across year boundaries', () {
      final dates = [
        const CalendarDate(2027, 1, 1),
        const CalendarDate(2026, 12, 31),
        const CalendarDate(2026, 2, 3),
      ]..sort();
      expect(dates.map((d) => d.toIso()), [
        '2026-02-03',
        '2026-12-31',
        '2027-01-01',
      ]);
    });

    test('counts days between dates', () {
      expect(
        const CalendarDate(2026, 8, 21).daysUntil(const CalendarDate(2026, 8, 24)),
        3,
      );
      expect(
        const CalendarDate(2026, 8, 24).daysUntil(const CalendarDate(2026, 8, 21)),
        -3,
      );
    });

    test('adds days across a month boundary', () {
      expect(const CalendarDate(2026, 8, 30).addDays(3).toIso(), '2026-09-02');
    });

    test('equal dates are equal and hash alike', () {
      const a = CalendarDate(2026, 8, 21);
      final b = CalendarDate.parse('2026-08-21');
      expect(a, b);
      expect(a.hashCode, b.hashCode);
      // Value equality means dates deduplicate in sets and work as map keys.
      expect(<CalendarDate>{a, b}, hasLength(1));
      expect(<CalendarDate>{a, b, const CalendarDate(2026, 8, 22)},
          hasLength(2));
    });
  });

  test('yesterday is past and tomorrow is not', () {
    expect(CalendarDate.today().addDays(-1).isPast, isTrue);
    expect(CalendarDate.today().isPast, isFalse);
    expect(CalendarDate.today().addDays(1).isPast, isFalse);
  });
}
