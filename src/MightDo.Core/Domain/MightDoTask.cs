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
/// throughout, so the identifier carries a prefix the domain does not.
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

    /// <summary>The format version of the file this task came from.</summary>
    /// <remarks>
    /// Read from disk rather than assumed, because the value decides whether we
    /// are allowed to write the file back. Unknown keys are skipped on read, so
    /// rewriting a file a newer version wrote would delete whatever it knew and
    /// we do not — the storage layer refuses to materialise or save one instead.
    /// See <c>docs/format/workspace-v1.md</c>.
    /// </remarks>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string Id { get; init; }

    public required string Summary { get; init; }

    /// <summary>What the task involves and why, written before work starts.</summary>
    public string Description { get; init; } = "";

    public required string StatusId { get; init; }

    public string? CategoryId { get; init; }

    /// <summary>
    /// Set through <see cref="WithTags"/> so the <see cref="MaxTags"/> cap lives
    /// in one place. Capping on create only, and re-checking in the UI, leaves
    /// every other write free to exceed it; this makes over-tagging
    /// unrepresentable instead.
    /// </summary>
    [JsonInclude]
    public IReadOnlyList<string> TagIds { get; private init; } = [];

    public Priority Priority { get; init; } = Priority.Medium;

    /// <summary>A day, never an instant. See <see cref="CalendarDate"/>.</summary>
    public CalendarDate? DueDate { get; init; }

    /// <summary>
    /// The moment the task entered a status of type <see cref="StatusType.Final"/>.
    /// Set by the application, never by the user, and cleared if it leaves one.
    /// </summary>
    /// <remarks>
    /// Settable only through <see cref="WithStatus"/>, which is what makes the
    /// rule in ADR-0002 an invariant rather than a convention every caller has
    /// to remember. Deserialization still sets it directly — a file can arrive
    /// from a sync conflict with a completion date that disagrees with its
    /// status, and refusing to represent that would lose the user's data rather
    /// than surface it.
    /// </remarks>
    [JsonInclude]
    public DateTime? CompletedAt
    {
        get;
        private init => field = Instants.AtStoredPrecision(value);
    }

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

    /// <summary>
    /// Kept at the precision the file can hold. See <see cref="Instants"/>: a
    /// task carrying a moment its own file cannot record is a task that differs
    /// from itself the moment it is read back.
    /// </summary>
    public DateTime CreatedAt
    {
        get;
        init => field = Instants.AtStoredPrecision(value);
    } = Instants.Now();

    /// <inheritdoc cref="CreatedAt"/>
    public DateTime UpdatedAt
    {
        get;
        init => field = Instants.AtStoredPrecision(value);
    } = Instants.Now();

    /// <param name="time">
    /// The clock to stamp with. The session passes its own, so a test that owns
    /// time owns these stamps too; left out, it is the machine's.
    /// </param>
    public static MightDoTask Create(
        string summary,
        string statusId,
        string boardRank,
        string description = "",
        string? categoryId = null,
        IReadOnlyList<string>? tagIds = null,
        Priority priority = Priority.Medium,
        CalendarDate? dueDate = null,
        int? estimateMinutes = null,
        TimeProvider? time = null)
    {
        var now = Instants.Now(time ?? TimeProvider.System);
        return new MightDoTask
        {
            Id = Ulid.New(),
            Summary = summary,
            Description = description,
            StatusId = statusId,
            CategoryId = categoryId,
            TagIds = CapTags(tagIds ?? []),
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
    /// Returns a copy stamped as edited now.
    /// </summary>
    /// <remarks>
    /// Applied by <c>WorkspaceSession</c> to every change the user makes to a
    /// task, in one place, rather than by each command — see its remarks on the
    /// stamping policy for what counts as an edit and what does not.
    /// </remarks>
    /// <inheritdoc cref="Create" path="/param[@name='time']"/>
    public MightDoTask Touch(TimeProvider? time = null) =>
        this with { UpdatedAt = Instants.Now(time ?? TimeProvider.System) };

    /// <summary>
    /// Moves the task to <paramref name="statusId"/>, applying the
    /// completion-date rule.
    /// </summary>
    /// <remarks>
    /// The completion date is derived from the status <i>type</i>, not from any
    /// particular status: entering any <see cref="StatusType.Final"/> status
    /// stamps it, leaving one clears it, and moving between two Final statuses
    /// preserves the original moment rather than restamping it. There is no "the
    /// done status" — see ADR-0002.
    /// </remarks>
    /// <param name="boardRank">Null keeps the task's current position.</param>
    /// <inheritdoc cref="Create" path="/param[@name='time']"/>
    public MightDoTask WithStatus(
        string statusId,
        WorkspaceConfig config,
        string? boardRank = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var wasFinal = config.IsFinal(StatusId);
        var isFinal = config.IsFinal(statusId);

        return this with
        {
            StatusId = statusId,
            BoardRank = boardRank ?? BoardRank,
            CompletedAt = isFinal
                ? (wasFinal ? CompletedAt : Instants.Now(time ?? TimeProvider.System))
                : null,
        };
    }

    /// <summary>
    /// Replaces the task's tags, keeping at most <see cref="MaxTags"/>.
    /// </summary>
    /// <remarks>
    /// Truncates rather than throwing, as creating a task does. Tags are a
    /// lightweight convenience; refusing an edit outright over the eleventh one
    /// would be a worse experience than quietly keeping the first ten.
    /// </remarks>
    public MightDoTask WithTags(IEnumerable<string> tagIds) =>
        this with { TagIds = CapTags(tagIds) };

    /// <summary>
    /// Whether this holds the same values as <paramref name="other"/>.
    /// </summary>
    /// <remarks>
    /// Not <c>==</c>: the generated record equality compares the collection
    /// properties by reference, so two tasks read from the same file are never
    /// equal. Their elements are records of scalars, so comparing the sequences
    /// is enough.
    /// </remarks>
    public bool HasSameContentAs(MightDoTask? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return Id == other.Id
               && Summary == other.Summary
               && Description == other.Description
               && StatusId == other.StatusId
               && CategoryId == other.CategoryId
               && Priority == other.Priority
               && DueDate == other.DueDate
               && CompletedAt == other.CompletedAt
               && EstimateMinutes == other.EstimateMinutes
               && TotalTimeMinutes == other.TotalTimeMinutes
               && BoardRank == other.BoardRank
               && CreatedAt == other.CreatedAt
               && UpdatedAt == other.UpdatedAt
               && TagIds.SequenceEqual(other.TagIds)
               && Steps.SequenceEqual(other.Steps)
               && Notes.SequenceEqual(other.Notes)
               && Attachments.SequenceEqual(other.Attachments)
               && Reminders.SequenceEqual(other.Reminders);
    }

    private static IReadOnlyList<string> CapTags(IEnumerable<string> tagIds)
    {
        ArgumentNullException.ThrowIfNull(tagIds);
        return [.. tagIds.Take(MaxTags)];
    }
}
