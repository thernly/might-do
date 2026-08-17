/// A calendar day with no time and no timezone.
///
/// Due dates are days, not instants. Storing "due 21 Aug" as a `DateTime` at
/// midnight and rendering it in another zone shifts it to the 20th, which is
/// the single most common bug in date handling. This type makes that
/// impossible: there is no time component to misinterpret.
///
/// Completion dates and note timestamps are the opposite case — real moments —
/// and use `DateTime` in UTC instead.
class CalendarDate implements Comparable<CalendarDate> {
  final int year;
  final int month;
  final int day;

  const CalendarDate(this.year, this.month, this.day);

  /// Interprets [dt] in local time and keeps only the calendar day.
  factory CalendarDate.fromLocal(DateTime dt) {
    final local = dt.isUtc ? dt.toLocal() : dt;
    return CalendarDate(local.year, local.month, local.day);
  }

  static CalendarDate today() => CalendarDate.fromLocal(DateTime.now());

  /// Parses an ISO-8601 calendar date, `2026-08-21`.
  static CalendarDate parse(String value) {
    final parsed = tryParse(value);
    if (parsed == null) {
      throw FormatException('Not an ISO calendar date', value);
    }
    return parsed;
  }

  static CalendarDate? tryParse(String? value) {
    if (value == null) return null;
    final match = RegExp(r'^(\d{4})-(\d{2})-(\d{2})$').firstMatch(value);
    if (match == null) return null;
    final year = int.parse(match.group(1)!);
    final month = int.parse(match.group(2)!);
    final day = int.parse(match.group(3)!);
    if (month < 1 || month > 12 || day < 1 || day > 31) return null;
    // Reject days that don't exist in the given month, e.g. 2026-02-30.
    final probe = DateTime(year, month, day);
    if (probe.year != year || probe.month != month || probe.day != day) {
      return null;
    }
    return CalendarDate(year, month, day);
  }

  String toIso() => '${year.toString().padLeft(4, '0')}-'
      '${month.toString().padLeft(2, '0')}-'
      '${day.toString().padLeft(2, '0')}';

  /// Midnight local on this day. Only for arithmetic and formatting — never
  /// persist the result.
  DateTime toLocalDateTime() => DateTime(year, month, day);

  /// Whole days from this date to [other]; negative if [other] is earlier.
  int daysUntil(CalendarDate other) =>
      other.toLocalDateTime().difference(toLocalDateTime()).inDays;

  CalendarDate addDays(int days) =>
      CalendarDate.fromLocal(toLocalDateTime().add(Duration(days: days)));

  bool get isPast => compareTo(today()) < 0;

  @override
  int compareTo(CalendarDate other) {
    if (year != other.year) return year.compareTo(other.year);
    if (month != other.month) return month.compareTo(other.month);
    return day.compareTo(other.day);
  }

  bool operator <(CalendarDate other) => compareTo(other) < 0;
  bool operator <=(CalendarDate other) => compareTo(other) <= 0;
  bool operator >(CalendarDate other) => compareTo(other) > 0;
  bool operator >=(CalendarDate other) => compareTo(other) >= 0;

  @override
  bool operator ==(Object other) =>
      other is CalendarDate &&
      other.year == year &&
      other.month == month &&
      other.day == day;

  @override
  int get hashCode => Object.hash(year, month, day);

  @override
  String toString() => toIso();
}
