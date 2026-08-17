using System.Text.Json;
using System.Text.Json.Serialization;
using MightDo.Core.Domain;

namespace MightDo.Core.Serialization;

/// <summary>
/// Reads and writes <see cref="CalendarDate"/> as <c>yyyy-MM-dd</c>.
/// </summary>
/// <remarks>
/// A due date is a day, not an instant. Letting the default converter treat it
/// as a <see cref="DateTime"/> is the bug this type exists to prevent.
/// </remarks>
public sealed class CalendarDateConverter : JsonConverter<CalendarDate>
{
    public override CalendarDate Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return CalendarDate.TryParse(value, out var parsed)
            ? parsed
            : throw new JsonException($"Not an ISO calendar date: '{value}'");
    }

    public override void Write(
        Utf8JsonWriter writer, CalendarDate value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToIso());
}

/// <summary>
/// Reads and writes a real moment as ISO-8601 UTC.
/// </summary>
/// <remarks>
/// Written with a <c>Z</c> suffix and three or six fractional digits, matching
/// what is already on disk. .NET's round-trip ("O") format would emit seven,
/// which the Flutter implementation reads correctly but truncates — so we emit
/// what both sides represent exactly, and a folder edited by either
/// implementation stays diff-clean.
/// <para>
/// On read, any valid ISO-8601 instant is accepted — a zone offset instead of
/// <c>Z</c>, no fractional part, or more digits than we write — and converted
/// to UTC.
/// </para>
/// </remarks>
public sealed class InstantConverter : JsonConverter<DateTime>
{
    public override DateTime Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // DateTimeOffset parsing keeps the offset, so a value written as
        // +02:00 converts rather than being silently reinterpreted as UTC.
        var value = reader.GetDateTimeOffset();
        return value.UtcDateTime;
    }

    public override void Write(
        Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            // An unspecified kind is a bug upstream, but guessing "local" here
            // would shift the value; treat it as already-UTC and move on.
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

        var format = utc.Microsecond == 0
            ? "yyyy-MM-ddTHH:mm:ss.fffZ"
            : "yyyy-MM-ddTHH:mm:ss.ffffffZ";
        writer.WriteStringValue(utc.ToString(format, System.Globalization.CultureInfo.InvariantCulture));
    }
}
