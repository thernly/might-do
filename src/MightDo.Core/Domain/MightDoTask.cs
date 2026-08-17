using System.Text.Json.Serialization;

namespace MightDo.Core.Domain;

/// <summary>
/// A single unit of work, tracked from conception to completion.
/// </summary>
/// <remarks>
/// Persisted as one JSON file per task, named by <see cref="Id"/> — see
/// <c>docs/adr/0001-file-per-task-json-storage.md</c>.
/// <para>
/// The domain term is simply "Task". C# already has
/// <see cref="System.Threading.Tasks.Task"/>, and storage and UI are async
/// throughout, so the identifier carries a prefix the domain does not — the same
/// accommodation the Flutter implementation makes for <c>final</c>.
/// </para>
/// <para>
/// Property order is the on-disk key order, which is deliberately stable so a
/// one-field edit produces a one-line diff for the sync client.
/// </para>
/// </remarks>
public sealed record MightDoTask
{
    /// <summary>Maximum tags on one task. Tags are meant to stay lightweight.</summary>
    public const int MaxTags = 10;

    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion => CurrentSchemaVersion;

    public required string Id { get; init; }

    public required string Summary { get; init; }

    /// <summary>What the task involves and why, written before work starts.</summary>
    public string Description { get; init; } = "";

    public required string StatusId { get; init; }

    public string? CategoryId { get; init; }

    public IReadOnlyList<string> TagIds { get; init; } = [];

    public Priority Priority { get; init; } = Priority.Medium;

    /// <summary>A day, never an instant. See <see cref="CalendarDate"/>.</summary>
    public CalendarDate? DueDate { get; init; }

    /// <summary>
    /// The moment the task entered a status of type <see cref="StatusType.Final"/>.
    /// Set by the application, cleared if it leaves one.
    /// </summary>
    public DateTime? CompletedAt { get; init; }

    /// <summary>Expected effort in whole minutes, recorded up front.</summary>
    public int? EstimateMinutes { get; init; }

    /// <summary>Actual effort in whole minutes, entered by hand at completion.</summary>
    public int? TotalTimeMinutes { get; init; }

    /// <summary>
    /// Fractional index controlling manual position on the board. One field
    /// covers every column, since columns hold disjoint sets of tasks. Compare
    /// with <see cref="StringComparer.Ordinal"/>.
    /// </summary>
    public string BoardRank { get; init; } = "i";

    public IReadOnlyList<Step> Steps { get; init; } = [];

    public IReadOnlyList<Note> Notes { get; init; } = [];

    public IReadOnlyList<Attachment> Attachments { get; init; } = [];

    public IReadOnlyList<Reminder> Reminders { get; init; } = [];

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;

    public static MightDoTask Create(
        string summary,
        string statusId,
        string boardRank,
        string description = "",
        string? categoryId = null,
        IReadOnlyList<string>? tagIds = null,
        Priority priority = Priority.Medium,
        CalendarDate? dueDate = null,
        int? estimateMinutes = null)
    {
        var now = DateTime.UtcNow;
        return new MightDoTask
        {
            Id = Ulid.New(),
            Summary = summary,
            Description = description,
            StatusId = statusId,
            CategoryId = categoryId,
            TagIds = tagIds ?? [],
            Priority = priority,
            DueDate = dueDate,
            EstimateMinutes = estimateMinutes,
            BoardRank = boardRank,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    [JsonIgnore]
    public bool IsComplete => CompletedAt is not null;

    [JsonIgnore]
    public int StepsDone => Steps.Count(step => step.Done);

    /// <summary>
    /// Difference between estimate and actual, in minutes. Null unless both are
    /// recorded. Positive means it took longer than expected.
    /// </summary>
    [JsonIgnore]
    public int? EstimateVariance =>
        EstimateMinutes is null || TotalTimeMinutes is null
            ? null
            : TotalTimeMinutes - EstimateMinutes;

    /// <summary><see cref="DueDate"/> is a day, so overdue means the day has fully passed.</summary>
    [JsonIgnore]
    public bool IsOverdue => !IsComplete && DueDate is { } due && due.IsPast;

    /// <summary>Reminders that have come due and haven't been acknowledged.</summary>
    public IReadOnlyList<Reminder> OutstandingReminders(DateTime now)
    {
        var moment = now.ToUniversalTime();
        return [.. Reminders.Where(r => r.IsOutstanding && r.RemindAt <= moment)];
    }

    /// <summary>
    /// Returns a copy stamped as edited now. Every mutation goes through here so
    /// <see cref="UpdatedAt"/> cannot be forgotten.
    /// </summary>
    public MightDoTask Touch() => this with { UpdatedAt = DateTime.UtcNow };
}
