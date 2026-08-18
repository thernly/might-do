using System.Text.Json.Serialization;

namespace MightDo.Platform;

/// <summary>
/// How a workspace was left: which view, which sort, and what was filtered.
/// </summary>
/// <remarks>
/// Per workspace rather than per application, because the whole point of having
/// several is that they are different bodies of work — a board-first workspace
/// for the day job and a filtered-to-overdue one for the house should each come
/// back as they were left.
/// <para>
/// Everything that names something in the workspace is held as a plain string
/// rather than an enum or an id type. The two halves are written by different
/// hands: this file is machine-local, the ids it mentions live in a synced
/// config.json, and either can move without the other. A sort that no longer
/// exists or a tag deleted on another machine has to read back as "no such
/// filter" — not as a settings file that fails to parse and silently takes the
/// window size and the workspace list down with it.
/// </para>
/// </remarks>
public sealed record WorkspaceViewState
{
    public ViewMode ViewMode { get; init; } = ViewMode.List;

    /// <summary>The <c>TaskSort</c> by name, or null for the default.</summary>
    public string? Sort { get; init; }

    public string Search { get; init; } = "";

    public bool IncludeCompleted { get; init; }

    public bool OverdueOnly { get; init; }

    public IReadOnlyList<string> StatusIds { get; init; } = [];

    /// <summary>Selected <c>StatusType</c> values by name.</summary>
    public IReadOnlyList<string> StatusTypes { get; init; } = [];

    public IReadOnlyList<string> CategoryIds { get; init; } = [];

    public IReadOnlyList<string> TagIds { get; init; } = [];

    /// <summary>Selected <c>Priority</c> values by name.</summary>
    public IReadOnlyList<string> Priorities { get; init; } = [];

    /// <summary>Every selected id, whichever group it came from.</summary>
    /// <remarks>
    /// The filter panel restores by id and the groups cannot collide — statuses,
    /// categories and tags are ULIDs, status types and priorities are names from
    /// closed sets — so the caller has no reason to care which list an id was in.
    /// </remarks>
    [JsonIgnore]
    public IEnumerable<string> SelectedFilterIds =>
        StatusIds.Concat(StatusTypes).Concat(CategoryIds).Concat(TagIds).Concat(Priorities);

    /// <summary>
    /// Whether this holds the same values as <paramref name="other"/>.
    /// </summary>
    /// <remarks>
    /// Not <c>==</c>: the generated record equality compares the list
    /// properties by reference, so a state rebuilt from the same view is never
    /// equal to the stored one and every keystroke would rewrite the file.
    /// </remarks>
    public bool SameAs(WorkspaceViewState other) =>
        ViewMode == other.ViewMode
        && Sort == other.Sort
        && Search == other.Search
        && IncludeCompleted == other.IncludeCompleted
        && OverdueOnly == other.OverdueOnly
        && StatusIds.SequenceEqual(other.StatusIds)
        && StatusTypes.SequenceEqual(other.StatusTypes)
        && CategoryIds.SequenceEqual(other.CategoryIds)
        && TagIds.SequenceEqual(other.TagIds)
        && Priorities.SequenceEqual(other.Priorities);

    [JsonIgnore]
    public bool HasAnyFilter =>
        Search.Length > 0 || IncludeCompleted || OverdueOnly || SelectedFilterIds.Any();
}

/// <summary>
/// One workspace the user has told the app about: where it is, what they call
/// it, and how they left it.
/// </summary>
/// <remarks>
/// The name is machine-local and not part of the workspace format. It could
/// have gone in the workspace's own config.json and travelled between machines,
/// but that would put a key in a versioned, cross-implementation format
/// (<c>docs/format/workspace-v1.md</c>) to hold a label — and the label is only
/// ever needed by whichever machine is drawing the switcher.
/// <para>
/// Identity is the path. Two entries for the same folder are the same workspace
/// however they were added, which is why the list is keyed on it.
/// </para>
/// </remarks>
public sealed record RememberedWorkspace
{
    public required string Path { get; init; }

    public required string Name { get; init; }

    public WorkspaceViewState View { get; init; } = new();

    /// <summary>Whether the folder is where it was left.</summary>
    /// <remarks>
    /// Not stored — asked. A workspace on an unmounted drive or in a
    /// not-yet-synced OneDrive folder is missing rather than gone, and stays in
    /// the list so it can come back.
    /// </remarks>
    [JsonIgnore]
    public bool Exists => Directory.Exists(Path);

    /// <summary>
    /// The name to give a folder nobody has named: the folder itself.
    /// </summary>
    /// <remarks>
    /// Trailing separators are stripped first, because <c>~/tasks/</c> and
    /// <c>~/tasks</c> are the same folder and only one of them has a last
    /// segment. A path with no usable segment at all — a drive root — falls
    /// back to the whole path rather than to an entry with a blank name.
    /// </remarks>
    public static string NameFor(string path)
    {
        var trimmed = path.TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

        var name = System.IO.Path.GetFileName(trimmed);
        return name.Length > 0 ? name : path;
    }
}
