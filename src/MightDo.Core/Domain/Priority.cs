using System.Text.Json.Serialization;

namespace MightDo.Core.Domain;

/// <summary>
/// How important a task is relative to others. Fixed scale.
/// </summary>
/// <remarks>
/// Declaration order is significant: it is the ordering used when sorting by
/// priority, so <see cref="Critical"/> must stay last.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<Priority>))]
public enum Priority
{
    [JsonStringEnumMemberName("low")]
    Low,

    [JsonStringEnumMemberName("medium")]
    Medium,

    [JsonStringEnumMemberName("high")]
    High,

    [JsonStringEnumMemberName("critical")]
    Critical,
}

public static class PriorityExtensions
{
    /// <summary>Highest first, for default board and list ordering.</summary>
    public static int CompareDescending(this Priority left, Priority right) =>
        right.CompareTo(left);

    /// <summary>Human-readable name shown in the UI.</summary>
    public static string Label(this Priority priority) => priority switch
    {
        Priority.Low => "Low",
        Priority.Medium => "Medium",
        Priority.High => "High",
        Priority.Critical => "Critical",
        _ => throw new ArgumentOutOfRangeException(nameof(priority)),
    };
}
