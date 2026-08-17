using System.Globalization;

namespace MightDo.Core.Domain;

/// <summary>
/// A calendar day with no time and no timezone.
/// </summary>
/// <remarks>
/// Due dates are days, not instants. Storing "due 21 Aug" as an instant at
/// midnight and rendering it in another zone shifts it to the 20th, which is the
/// single most common bug in date handling. This type makes that impossible:
/// there is no time component to misinterpret.
/// <para>
/// Completion dates and note timestamps are the opposite case — real moments —
/// and use UTC <see cref="DateTime"/> instead.
/// </para>
/// </remarks>
public readonly record struct CalendarDate(int Year, int Month, int Day)
    : IComparable<CalendarDate>
{
    /// <summary>Interprets <paramref name="value"/> in local time and keeps only the day.</summary>
    public static CalendarDate FromLocal(DateTime value)
    {
        var local = value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
        return new CalendarDate(local.Year, local.Month, local.Day);
    }

    public static CalendarDate Today() => FromLocal(DateTime.Now);

    /// <summary>Parses an ISO-8601 calendar date, <c>2026-08-21</c>.</summary>
    /// <exception cref="FormatException">The value is not an ISO calendar date.</exception>
    public static CalendarDate Parse(string value) =>
        TryParse(value, out var parsed)
            ? parsed
            : throw new FormatException($"Not an ISO calendar date: '{value}'");

    /// <summary>
    /// Strictly <c>yyyy-MM-dd</c>: no time, no zone, and days that do not exist
    /// in the given month are rejected.
    /// </summary>
    public static bool TryParse(string? value, out CalendarDate result)
    {
        result = default;
        if (value is null) return false;

        // ParseExact with an invariant culture rejects '2026-8-21' and anything
        // carrying a time, and validates the day against the month for free.
        if (!DateTime.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return false;
        }

        result = new CalendarDate(parsed.Year, parsed.Month, parsed.Day);
        return true;
    }

    public static CalendarDate? TryParse(string? value) =>
        TryParse(value, out var parsed) ? parsed : null;

    public string ToIso() => $"{Year:D4}-{Month:D2}-{Day:D2}";

    /// <summary>
    /// Midnight local on this day. Only for arithmetic and formatting — never
    /// persist the result.
    /// </summary>
    public DateTime ToLocalDateTime() => new(Year, Month, Day, 0, 0, 0, DateTimeKind.Local);

    /// <summary>Whole days from this date to <paramref name="other"/>; negative if earlier.</summary>
    public int DaysUntil(CalendarDate other) =>
        (int)(other.ToLocalDateTime() - ToLocalDateTime()).TotalDays;

    public CalendarDate AddDays(int days) => FromLocal(ToLocalDateTime().AddDays(days));

    public bool IsPast => CompareTo(Today()) < 0;

    public int CompareTo(CalendarDate other)
    {
        if (Year != other.Year) return Year.CompareTo(other.Year);
        if (Month != other.Month) return Month.CompareTo(other.Month);
        return Day.CompareTo(other.Day);
    }

    public static bool operator <(CalendarDate left, CalendarDate right) => left.CompareTo(right) < 0;
    public static bool operator <=(CalendarDate left, CalendarDate right) => left.CompareTo(right) <= 0;
    public static bool operator >(CalendarDate left, CalendarDate right) => left.CompareTo(right) > 0;
    public static bool operator >=(CalendarDate left, CalendarDate right) => left.CompareTo(right) >= 0;

    public override string ToString() => ToIso();
}
