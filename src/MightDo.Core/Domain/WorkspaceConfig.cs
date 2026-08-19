using System.Text.Json.Serialization;

namespace MightDo.Core.Domain;

/// <summary>
/// Everything about a workspace that isn't a task: the statuses, categories and
/// tags the user has defined, plus which status new tasks start in.
/// </summary>
/// <remarks>
/// Persisted as a single <c>config.json</c>. It is the one shared file in the
/// storage model and therefore the one genuine conflict hotspot — accepted
/// deliberately, because status and category edits are rare.
/// </remarks>
public sealed record WorkspaceConfig
{
    public const int CurrentSchemaVersion = 1;

    private readonly IReadOnlyList<Status> _statuses = [];

    /// <summary>The format version of the file this config came from.</summary>
    /// <remarks>
    /// Written first, and kept on read: an older app that rewrote a newer
    /// version's config.json would drop every key it does not know. See
    /// <see cref="MightDoTask.SchemaVersion"/>.
    /// </remarks>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>
    /// The status new tasks are created in. Always a status of type
    /// <see cref="StatusType.Initial"/>. This designation means nothing else.
    /// </summary>
    public required string DefaultStatusId { get; init; }

    /// <summary>
    /// All statuses, always in <see cref="Status.Order"/> order.
    /// </summary>
    /// <remarks>
    /// Sorting on the way in rather than at each use means there is no such
    /// thing as an unsorted <see cref="WorkspaceConfig"/> to forget about, and
    /// what we write back is normalised however the file on disk was ordered.
    /// </remarks>
    public required IReadOnlyList<Status> Statuses
    {
        get => _statuses;
        init => _statuses = [.. value.OrderBy(status => status.Order)];
    }

    public IReadOnlyList<Category> Categories { get; init; } = [];

    public IReadOnlyList<Tag> Tags { get; init; } = [];

    /// <summary>Statuses that get a column on the Kanban view, left to right.</summary>
    /// <remarks>
    /// Ignored for serialization: a computed property is a view of the statuses,
    /// not a field of the format, and writing one into a user's config.json
    /// would put a key there that the spec does not define.
    /// </remarks>
    [JsonIgnore]
    public IEnumerable<Status> BoardStatuses => Statuses.Where(s => !s.HiddenFromBoard);

    public Status? StatusById(string? id) =>
        id is null ? null : Statuses.FirstOrDefault(s => s.Id == id);

    public Category? CategoryById(string? id) =>
        id is null ? null : Categories.FirstOrDefault(c => c.Id == id);

    public Tag? TagById(string? id) =>
        id is null ? null : Tags.FirstOrDefault(t => t.Id == id);

    /// <summary>
    /// Tags for the given ids, skipping any that no longer exist. A task
    /// referencing a deleted tag is not an error.
    /// </summary>
    public IReadOnlyList<Tag> TagsByIds(IEnumerable<string> ids) =>
        [.. ids.Select(TagById).OfType<Tag>()];

    /// <summary>Whether entering <paramref name="statusId"/> should stamp a completion date.</summary>
    public bool IsFinal(string? statusId) => StatusById(statusId)?.Type == StatusType.Final;

    /// <summary>
    /// Whether this holds the same values as <paramref name="other"/>.
    /// </summary>
    /// <remarks>
    /// Not <c>==</c>: the generated record equality compares the list properties
    /// by reference, so two configs read from the same file are never equal.
    /// The elements are records of scalars, so comparing the sequences is
    /// enough.
    /// </remarks>
    public bool HasSameContentAs(WorkspaceConfig? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return SchemaVersion == other.SchemaVersion
               && DefaultStatusId == other.DefaultStatusId
               && Statuses.SequenceEqual(other.Statuses)
               && Categories.SequenceEqual(other.Categories)
               && Tags.SequenceEqual(other.Tags);
    }

    /// <summary>
    /// The starting point for a brand new workspace: one status of each type,
    /// plus a hidden backlog, so the board is immediately usable.
    /// </summary>
    public static WorkspaceConfig Seed()
    {
        var backlog = new Status(Ulid.New(), "Backlog", StatusType.Initial, 0, HiddenFromBoard: true);
        var notStarted = new Status(Ulid.New(), "Not Started", StatusType.Initial, 1);
        var inProgress = new Status(Ulid.New(), "In Progress", StatusType.Active, 2);
        var blocked = new Status(Ulid.New(), "Blocked", StatusType.Active, 3);
        var done = new Status(Ulid.New(), "Done", StatusType.Final, 4);
        var abandoned = new Status(Ulid.New(), "Abandoned", StatusType.Final, 5, HiddenFromBoard: true);

        return new WorkspaceConfig
        {
            DefaultStatusId = notStarted.Id,
            Statuses = [backlog, notStarted, inProgress, blocked, done, abandoned],
        };
    }
}
