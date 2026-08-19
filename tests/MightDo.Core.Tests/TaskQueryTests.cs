using MightDo.Core.Domain;
using MightDo.Core.Query;

namespace MightDo.Core.Tests;

/// <summary>
/// Ports the original implementation's query suite case for case, then adds the
/// cases it left uncovered. Filtering and sorting are specified nowhere but in
/// the code and these tests, so they are the specification.
/// </summary>
public class TaskQueryTests
{
    private readonly WorkspaceConfig _config = WorkspaceConfig.Seed();
    private readonly Status _initial;
    private readonly Status _active;
    private readonly Status _done;

    public TaskQueryTests()
    {
        _initial = _config.Statuses.First(s => s.Type == StatusType.Initial);
        _active = _config.Statuses.First(s => s.Type == StatusType.Active);
        _done = _config.Statuses.First(s => s.Type == StatusType.Final);
    }

    private MightDoTask Task(
        string summary,
        Status? status = null,
        Priority priority = Priority.Medium,
        CalendarDate? due = null,
        string description = "",
        IReadOnlyList<Note>? notes = null,
        IReadOnlyList<Step>? steps = null,
        string? categoryId = null,
        IReadOnlyList<string>? tagIds = null)
    {
        var created = MightDoTask.Create(
            summary: summary,
            statusId: _initial.Id,
            boardRank: Rank.First,
            description: description,
            categoryId: categoryId,
            tagIds: tagIds,
            priority: priority,
            dueDate: due) with
        {
            Notes = notes ?? [],
            Steps = steps ?? [],
        };

        // Reaching a status goes through WithStatus, so the completion date
        // follows the rule rather than being set by hand — it is no longer
        // possible to set it by hand.
        return status is null || status.Id == _initial.Id
            ? created
            : created.WithStatus(status.Id, _config);
    }

    private static IEnumerable<string> Summaries(IEnumerable<MightDoTask> tasks) =>
        tasks.Select(t => t.Summary);

    // ---- completed tasks ---------------------------------------------------

    [Fact]
    public void CompletedTasksAreHiddenByDefault()
    {
        List<MightDoTask> tasks = [Task("Open"), Task("Shipped", _done)];

        var result = TaskQuery.Default.Apply(tasks, _config);

        Assert.Equal(["Open"], Summaries(result));
    }

    [Fact]
    public void CompletedTasksAppearWhenAskedFor()
    {
        List<MightDoTask> tasks = [Task("Open"), Task("Shipped", _done)];

        var result = new TaskQuery { IncludeCompleted = true }.Apply(tasks, _config);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void CompletedTasksAppearWhenExplicitlyFilteredToAFinalStatus()
    {
        List<MightDoTask> tasks = [Task("Open"), Task("Shipped", _done)];

        var result = new TaskQuery { StatusIds = Set(_done.Id) }.Apply(tasks, _config);

        Assert.Equal(["Shipped"], Summaries(result));
    }

    [Fact]
    public void HidingCompletedKeysOffStatusTypeNotTheCompletionDate()
    {
        // The two can disagree, and the type is what the application reasons
        // about. A task carrying a completion date but sitting in an Active
        // status is still work in progress and stays visible; one in a Final
        // status without a date is finished and is hidden.
        //
        // Neither state is reachable through the API any more — WithStatus owns
        // the rule — so they are built the only way they can actually occur: a
        // file arriving from a sync conflict or a hand edit.
        List<MightDoTask> tasks =
        [
            FromDisk("Stamped but active", _active.Id, completedAt: "2026-08-16T14:22:09.000Z"),
            FromDisk("Final but unstamped", _done.Id, completedAt: null),
        ];

        var result = TaskQuery.Default.Apply(tasks, _config);

        Assert.Equal(["Stamped but active"], Summaries(result));
    }

    private static MightDoTask FromDisk(string summary, string statusId, string? completedAt)
    {
        var completed = completedAt is null ? "null" : $"\"{completedAt}\"";
        return MightDo.Core.Serialization.WorkspaceJson.Deserialize<MightDoTask>($$"""
            {
              "id": "{{Ulid.New()}}",
              "summary": "{{summary}}",
              "statusId": "{{statusId}}",
              "completedAt": {{completed}},
              "createdAt": "2026-08-15T07:30:00.000Z",
              "updatedAt": "2026-08-15T07:30:00.000Z"
            }
            """)!;
    }

    [Fact]
    public void FinalTypeSelectionRevealsCompletedWork()
    {
        // Concluded work is hidden unless the user says otherwise, and ticking
        // the Status Type `Final` says so as plainly as ticking the Status
        // `Done`. A deliberate divergence from the original implementation,
        // which consults selected Statuses only and so shows an empty list here.
        List<MightDoTask> tasks = [Task("Open"), Task("Shipped", _done)];

        var result = new TaskQuery { StatusTypes = Set(StatusType.Final) }
            .Apply(tasks, _config);

        Assert.Equal(["Shipped"], Summaries(result));
    }

    [Fact]
    public void SelectingAnotherStatusTypeStillHidesCompletedWork()
    {
        // The fix must not turn into "any Stage selection reveals everything".
        List<MightDoTask> tasks = [Task("Doing", _active), Task("Shipped", _done)];

        var result = new TaskQuery { StatusTypes = Set(StatusType.Active) }
            .Apply(tasks, _config);

        Assert.Equal(["Doing"], Summaries(result));
    }

    [Fact]
    public void SelectingAnInitialStatusStillHidesCompletedWork()
    {
        // Guards the equivalence the rewrite relies on. The old rule keyed off
        // "no Status ticked at all"; the new one keys off "this task's Status
        // ticked". They agree because a Final task can only survive the Status
        // filter when its own Status was ticked — but that is worth pinning
        // rather than asserting.
        List<MightDoTask> tasks = [Task("Waiting"), Task("Shipped", _done)];

        var result = new TaskQuery { StatusIds = Set(_initial.Id) }.Apply(tasks, _config);

        Assert.Equal(["Waiting"], Summaries(result));
    }

    [Fact]
    public void SelectingBothAnInitialAndAFinalStatusShowsBoth()
    {
        List<MightDoTask> tasks =
        [
            Task("Waiting"),
            Task("Doing", _active),
            Task("Shipped", _done),
        ];

        var result = new TaskQuery { StatusIds = Set(_initial.Id, _done.Id) }
            .Apply(tasks, _config);

        Assert.Equal(2, result.Count);
        Assert.Contains("Waiting", Summaries(result));
        Assert.Contains("Shipped", Summaries(result));
    }

    // ---- search ------------------------------------------------------------

    [Fact]
    public void SearchMatchesTheSummary()
    {
        List<MightDoTask> tasks = [Task("Renew passport"), Task("Buy milk")];

        var result = new TaskQuery { Search = "passport" }.Apply(tasks, _config);

        Assert.Equal("Renew passport", Assert.Single(result).Summary);
    }

    [Fact]
    public void SearchMatchesDescriptionNotesAndSteps()
    {
        List<MightDoTask> tasks =
        [
            Task("One", description: "involves a dentist"),
            Task("Two", notes: [Note.Create("called the plumber")]),
            Task("Three", steps: [Step.Create("book the electrician")]),
            Task("Four"),
        ];

        Assert.Single(new TaskQuery { Search = "dentist" }.Apply(tasks, _config));
        Assert.Single(new TaskQuery { Search = "plumber" }.Apply(tasks, _config));
        Assert.Single(new TaskQuery { Search = "electrician" }.Apply(tasks, _config));
    }

    [Fact]
    public void SearchIsCaseInsensitiveAndRequiresEveryTerm()
    {
        List<MightDoTask> tasks = [Task("Renew UK passport"), Task("Renew library card")];

        Assert.Single(new TaskQuery { Search = "renew PASSPORT" }.Apply(tasks, _config));
        Assert.Equal(2, new TaskQuery { Search = "renew" }.Apply(tasks, _config).Count);
    }

    [Fact]
    public void SearchIgnoresSurroundingWhitespace()
    {
        List<MightDoTask> tasks = [Task("Renew passport")];

        Assert.Single(new TaskQuery { Search = "   passport   " }.Apply(tasks, _config));
    }

    [Fact]
    public void SearchTermsAreOrderIndependentAndMatchSubstrings()
    {
        List<MightDoTask> tasks = [Task("Renew UK passport")];

        Assert.Single(new TaskQuery { Search = "passport renew" }.Apply(tasks, _config));
        Assert.Single(new TaskQuery { Search = "pass" }.Apply(tasks, _config));
    }

    [Fact]
    public void ASingleSearchTermCannotSpanTwoFields()
    {
        // Fields are joined with a newline, so a term cannot match across the
        // boundary: "uymil" would match if "Buy" and "milk" were concatenated.
        // Note this only constrains one term — a multi-word search is split on
        // whitespace and ANDed, so "buy milk" matches this task quite happily.
        List<MightDoTask> tasks = [Task("Buy", description: "milk")];

        Assert.Empty(new TaskQuery { Search = "uymil" }.Apply(tasks, _config));
        Assert.Single(new TaskQuery { Search = "buy milk" }.Apply(tasks, _config));
    }

    // ---- filters -----------------------------------------------------------

    [Fact]
    public void FiltersByStatusType()
    {
        List<MightDoTask> tasks = [Task("Waiting"), Task("Doing", _active)];

        var result = new TaskQuery { StatusTypes = Set(StatusType.Active) }
            .Apply(tasks, _config);

        Assert.Equal(["Doing"], Summaries(result));
    }

    [Fact]
    public void FiltersByPriority()
    {
        List<MightDoTask> tasks =
        [
            Task("Meh", priority: Priority.Low),
            Task("Now", priority: Priority.Critical),
        ];

        var result = new TaskQuery { Priorities = Set(Priority.Critical) }
            .Apply(tasks, _config);

        Assert.Equal(["Now"], Summaries(result));
    }

    [Fact]
    public void FiltersByTagMatchingAnyOfTheSelectedTags()
    {
        List<MightDoTask> tasks =
        [
            Task("A", tagIds: ["t1"]),
            Task("B", tagIds: ["t2"]),
            Task("C", tagIds: ["t3"]),
        ];

        var result = new TaskQuery { TagIds = Set("t1", "t2") }.Apply(tasks, _config);

        Assert.Equal(2, result.Count);
        Assert.Contains("A", Summaries(result));
        Assert.Contains("B", Summaries(result));
    }

    [Fact]
    public void FiltersByCategoryAndNeverMatchesAnUnsetOne()
    {
        List<MightDoTask> tasks = [Task("Filed", categoryId: "c1"), Task("Loose")];

        var result = new TaskQuery { CategoryIds = Set("c1") }.Apply(tasks, _config);

        Assert.Equal(["Filed"], Summaries(result));
    }

    [Fact]
    public void OverdueOnlyExcludesFutureAndUndatedTasks()
    {
        List<MightDoTask> tasks =
        [
            Task("Late", due: CalendarDate.Today().AddDays(-2)),
            Task("Soon", due: CalendarDate.Today().AddDays(2)),
            Task("Undated"),
        ];

        var result = new TaskQuery { OverdueOnly = true }.Apply(tasks, _config);

        Assert.Equal(["Late"], Summaries(result));
    }

    [Fact]
    public void ATaskDueTodayIsNotYetOverdue()
    {
        List<MightDoTask> tasks = [Task("Today", due: CalendarDate.Today())];

        Assert.Empty(new TaskQuery { OverdueOnly = true }.Apply(tasks, _config));
    }

    [Fact]
    public void FiltersCombineAsAnd()
    {
        List<MightDoTask> tasks =
        [
            Task("Match", _active, Priority.High),
            Task("Wrong priority", _active, Priority.Low),
            Task("Wrong status", priority: Priority.High),
        ];

        var result = new TaskQuery
        {
            Priorities = Set(Priority.High),
            StatusTypes = Set(StatusType.Active),
        }.Apply(tasks, _config);

        Assert.Equal(["Match"], Summaries(result));
    }

    [Fact]
    public void OnlyTheStatusTypeFilterExcludesATaskWhoseStatusIsGone()
    {
        // A task can outlive its status after a sync conflict. It stays visible
        // by default rather than vanishing, but it cannot satisfy a filter that
        // has to resolve the status to answer.
        var orphan = Task("Orphaned") with { StatusId = "01m07z0000000000000000gone" };
        List<MightDoTask> tasks = [orphan];

        Assert.Single(TaskQuery.Default.Apply(tasks, _config));
        Assert.Empty(new TaskQuery { StatusTypes = Set(StatusType.Initial) }
            .Apply(tasks, _config));
    }

    // ---- sorting -----------------------------------------------------------

    [Fact]
    public void SmartSortPutsOverdueFirstThenPriorityThenDueDate()
    {
        List<MightDoTask> tasks =
        [
            Task("Low, undated", priority: Priority.Low),
            Task("Critical, later", priority: Priority.Critical,
                due: CalendarDate.Today().AddDays(5)),
            Task("Critical, sooner", priority: Priority.Critical,
                due: CalendarDate.Today().AddDays(1)),
            Task("Overdue, low", priority: Priority.Low,
                due: CalendarDate.Today().AddDays(-1)),
        ];

        var result = TaskQuery.Default.Apply(tasks, _config);

        Assert.Equal(
            ["Overdue, low", "Critical, sooner", "Critical, later", "Low, undated"],
            Summaries(result));
    }

    [Fact]
    public void UndatedTasksSortAfterDatedOnes()
    {
        List<MightDoTask> tasks =
        [
            Task("Undated"),
            Task("Dated", due: CalendarDate.Today().AddDays(3)),
        ];

        var result = new TaskQuery { Sort = TaskSort.DueDate }.Apply(tasks, _config);

        Assert.Equal(["Dated", "Undated"], Summaries(result));
    }

    [Fact]
    public void SummarySortIsCaseInsensitive()
    {
        List<MightDoTask> tasks = [Task("banana"), Task("Apple"), Task("cherry")];

        var result = new TaskQuery { Sort = TaskSort.Summary }.Apply(tasks, _config);

        Assert.Equal(["Apple", "banana", "cherry"], Summaries(result));
    }

    [Fact]
    public void SummarySortIsOrdinalNotCultureSensitive()
    {
        // A culture-sensitive comparer ignores punctuation and would order these
        // differently — the same class of bug the board rank comparison has.
        List<MightDoTask> tasks = [Task("_underscore"), Task("apple"), Task("Zebra")];

        var result = new TaskQuery { Sort = TaskSort.Summary }.Apply(tasks, _config);

        Assert.Equal(["_underscore", "apple", "zebra"], Summaries(result).Select(s => s.ToLowerInvariant()));
    }

    [Fact]
    public void CreatedAndUpdatedSortNewestFirst()
    {
        var older = Task("Older") with
        {
            CreatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc),
        };
        var newer = Task("Newer") with
        {
            CreatedAt = new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 8, 6, 0, 0, 0, DateTimeKind.Utc),
        };
        List<MightDoTask> tasks = [older, newer];

        Assert.Equal(["Newer", "Older"],
            Summaries(new TaskQuery { Sort = TaskSort.Created }.Apply(tasks, _config)));
        Assert.Equal(["Older", "Newer"],
            Summaries(new TaskQuery { Sort = TaskSort.Updated }.Apply(tasks, _config)));
    }

    [Fact]
    public void TiedTasksComeOutInAStableDefinedOrder()
    {
        // A sort with no total tie-break leaves tied tasks in whatever order
        // the algorithm happens to produce. Here Id settles it, which makes the
        // list reproducible across reloads.
        List<MightDoTask> tasks = [Task("Same"), Task("Same"), Task("Same")];

        var first = Summaries(new TaskQuery { Sort = TaskSort.Summary }.Apply(tasks, _config));
        var again = Summaries(new TaskQuery { Sort = TaskSort.Summary }
            .Apply(Enumerable.Reverse(tasks), _config));

        Assert.Equal(first, again);
    }

    // ---- isFiltered --------------------------------------------------------

    [Fact]
    public void IsFilteredIsFalseForAFreshQuery() =>
        Assert.False(TaskQuery.Default.IsFiltered);

    [Fact]
    public void IsFilteredIsTrueOnceAnythingIsSet()
    {
        Assert.True(new TaskQuery { Search = "x" }.IsFiltered);
        Assert.True(new TaskQuery { OverdueOnly = true }.IsFiltered);
        Assert.True(new TaskQuery { IncludeCompleted = true }.IsFiltered);
    }

    [Fact]
    public void IsFilteredMeansNotTheDefaultViewNotNarrowed()
    {
        // Ticking Completed widens the result, and still counts: the empty state
        // and the clear-filters button both need to react to it. With Completed
        // on and nothing matching, "no tasks match your filters" is the true
        // message rather than "no tasks yet".
        var widened = new TaskQuery { IncludeCompleted = true };

        Assert.True(widened.IsFiltered);
        Assert.True(widened.Apply([], _config).Count == 0);
    }

    [Fact]
    public void SortingAloneIsNotFiltering() =>
        Assert.False(new TaskQuery { Sort = TaskSort.Summary }.IsFiltered);

    [Fact]
    public void WhitespaceOnlySearchIsNotFiltering() =>
        Assert.False(new TaskQuery { Search = "   " }.IsFiltered);

    private static IReadOnlySet<T> Set<T>(params T[] values) => new HashSet<T>(values);
}
