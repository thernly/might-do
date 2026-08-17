using System.Collections.Frozen;
using MightDo.Core.Domain;

namespace MightDo.Core.Query;

/// <summary>
/// A filter and sort over the workspace, driving the list view.
/// </summary>
/// <remarks>
/// A pure function of <c>(tasks, config)</c>: no I/O, no session, no caching.
/// The workspace holds state; this answers questions about it, and keeping the
/// two apart is what lets the list view be tested without a folder on disk.
/// <para>
/// Search covers summaries, descriptions, notes and steps — everything the user
/// typed. Matching anything less makes search feel broken.
/// </para>
/// <para>
/// Trashed tasks are not represented here, and deliberately so: they live in
/// <c>.trash/</c> and are never loaded, so no filter can forget to exclude them.
/// Adding a "show trashed" option here would undo that guarantee.
/// </para>
/// <para>
/// <b>Caveat:</b> this is a record, but the generated equality compares the set
/// properties by reference, so two queries with equal contents are not equal.
/// Don't use <c>==</c> to decide whether a re-query is needed without fixing
/// that first.
/// </para>
/// </remarks>
public sealed record TaskQuery
{
    public static TaskQuery Default { get; } = new();

    // FrozenSet<T>.Empty rather than static readonly fields of our own: a field
    // declared below Default would still be null when Default's initialiser
    // runs, and every set on it would silently be null.

    public string Search { get; init; } = "";

    public IReadOnlySet<string> StatusIds { get; init; } = FrozenSet<string>.Empty;

    public IReadOnlySet<StatusType> StatusTypes { get; init; } = FrozenSet<StatusType>.Empty;

    public IReadOnlySet<string> CategoryIds { get; init; } = FrozenSet<string>.Empty;

    public IReadOnlySet<string> TagIds { get; init; } = FrozenSet<string>.Empty;

    public IReadOnlySet<Priority> Priorities { get; init; } = FrozenSet<Priority>.Empty;

    public bool OverdueOnly { get; init; }

    /// <summary>
    /// Tasks in <c>Final</c> statuses are hidden by default — the working set is
    /// what you have left to do.
    /// </summary>
    public bool IncludeCompleted { get; init; }

    public TaskSort Sort { get; init; } = TaskSort.Smart;

    /// <summary>
    /// Whether this is anything other than the default view.
    /// </summary>
    /// <remarks>
    /// Deliberately <i>not</i> "has the user narrowed the result": it is what
    /// the empty state and the "clear filters" affordance key off, and both want
    /// to react to <see cref="IncludeCompleted"/> even though that widens the
    /// result. With Completed ticked and nothing matching, "no tasks match your
    /// filters" is the true message, not "no tasks yet".
    /// <para>
    /// Sorting is not filtering, so <see cref="Sort"/> is excluded.
    /// </para>
    /// <para>
    /// Written out property by property rather than as a comparison against
    /// <see cref="Default"/>, which would read more like the definition but
    /// would be wrong: the set properties compare by reference, so a query
    /// holding a freshly allocated empty set would not equal the default.
    /// </para>
    /// <para>
    /// Distinct from the filter panel's count badge, which asks a different
    /// question — how many controls <i>inside the panel</i> are active — and so
    /// ignores the search box, which is a visible field outside it. That count
    /// is a fact about a UI arrangement and lives in the view model.
    /// </para>
    /// </remarks>
    public bool IsFiltered =>
        !string.IsNullOrWhiteSpace(Search)
        || StatusIds.Count > 0
        || StatusTypes.Count > 0
        || CategoryIds.Count > 0
        || TagIds.Count > 0
        || Priorities.Count > 0
        || OverdueOnly
        || IncludeCompleted;

    public IReadOnlyList<MightDoTask> Apply(
        IEnumerable<MightDoTask> tasks, WorkspaceConfig config)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(config);

        var terms = Search
            .ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        var matched = tasks.Where(task => Matches(task, config, terms)).ToList();
        matched.Sort((a, b) => Compare(a, b));
        return matched;
    }

    private bool Matches(MightDoTask task, WorkspaceConfig config, string[] terms)
    {
        // May be null: the status could have been deleted, or the config could
        // be out of step with the tasks after a sync conflict.
        var status = config.StatusById(task.StatusId);

        // Hide-completed keys off the status's *type*, never off CompletedAt.
        // The two can disagree, and the type is what the application reasons
        // about (ADR-0002).
        //
        // Concluded work is hidden unless the user has said otherwise, and there
        // are three equally explicit ways of saying so: the Completed toggle,
        // ticking this task's Status, or ticking its Status Type. The last of
        // those is a deliberate divergence from the Flutter implementation,
        // which consults selected Statuses only — so there, ticking the Status
        // `Done` shows your done tasks while ticking the Status Type `Final`
        // shows an empty list, though `Done` is Final. Same intent, two
        // controls, opposite outcomes. Not a porting mistake; see
        // FinalTypeSelectionRevealsCompletedWork.
        if (!IncludeCompleted
            && status?.Type == StatusType.Final
            && !StatusIds.Contains(task.StatusId)
            && !StatusTypes.Contains(StatusType.Final))
        {
            return false;
        }

        if (StatusIds.Count > 0 && !StatusIds.Contains(task.StatusId)) return false;

        // The only filter that excludes a task whose status can't be resolved.
        if (StatusTypes.Count > 0 && (status is null || !StatusTypes.Contains(status.Type)))
        {
            return false;
        }

        // No "uncategorised" selector: an unset category never matches.
        if (CategoryIds.Count > 0
            && (task.CategoryId is null || !CategoryIds.Contains(task.CategoryId)))
        {
            return false;
        }

        // Any-of, not all-of.
        if (TagIds.Count > 0 && !task.TagIds.Any(TagIds.Contains)) return false;

        if (Priorities.Count > 0 && !Priorities.Contains(task.Priority)) return false;

        if (OverdueOnly && !task.IsOverdue) return false;

        if (terms.Length == 0) return true;

        var haystack = SearchText(task);
        return terms.All(term => haystack.Contains(term, StringComparison.Ordinal));
    }

    /// <summary>
    /// Everything the user typed, joined so a term cannot span two fields.
    /// </summary>
    private static string SearchText(MightDoTask task) =>
        string.Join(
            '\n',
            new[] { task.Summary, task.Description }
                .Concat(task.Notes.Select(note => note.Body))
                .Concat(task.Steps.Select(step => step.Text)))
        .ToLowerInvariant();

    private int Compare(MightDoTask a, MightDoTask b)
    {
        var result = Sort switch
        {
            TaskSort.Smart => CompareSmart(a, b),
            TaskSort.DueDate => Chain(CompareDue(a, b), () => ComparePriority(a, b)),
            TaskSort.Priority => Chain(ComparePriority(a, b), () => CompareDue(a, b)),
            TaskSort.Summary => string.CompareOrdinal(
                a.Summary.ToLowerInvariant(), b.Summary.ToLowerInvariant()),
            TaskSort.Created => b.CreatedAt.CompareTo(a.CreatedAt),
            TaskSort.Updated => b.UpdatedAt.CompareTo(a.UpdatedAt),
            _ => throw new ArgumentOutOfRangeException(nameof(Sort)),
        };

        // No sort in the Flutter implementation has a total tie-break, and its
        // sort is not stable, so tied tasks come out in an undefined order. Id
        // is a ULID, so this settles ties by creation time and makes the result
        // reproducible — defining behaviour that was previously unspecified.
        return result != 0 ? result : string.CompareOrdinal(a.Id, b.Id);
    }

    private static int CompareSmart(MightDoTask a, MightDoTask b)
    {
        var overdue = Flag(b.IsOverdue).CompareTo(Flag(a.IsOverdue));
        if (overdue != 0) return overdue;

        var priority = ComparePriority(a, b);
        if (priority != 0) return priority;

        var due = CompareDue(a, b);
        if (due != 0) return due;

        return a.CreatedAt.CompareTo(b.CreatedAt); // oldest first
    }

    private static int ComparePriority(MightDoTask a, MightDoTask b) =>
        a.Priority.CompareDescending(b.Priority);

    /// <summary>
    /// Undated tasks sort last — a task with no due date isn't more urgent than
    /// one due tomorrow.
    /// </summary>
    private static int CompareDue(MightDoTask a, MightDoTask b) => (a.DueDate, b.DueDate) switch
    {
        (null, null) => 0,
        (null, _) => 1,
        (_, null) => -1,
        var (left, right) => left!.Value.CompareTo(right!.Value),
    };

    private static int Chain(int first, Func<int> next) => first != 0 ? first : next();

    private static int Flag(bool value) => value ? 1 : 0;
}
