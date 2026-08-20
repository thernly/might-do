using MightDo.Core.Domain;
using MightDo.Core.Interchange;
using MightDo.Core.Session;
using MightDo.Core.Storage;

namespace MightDo.Core.Tests;

/// <summary>
/// Import: parse, plan, then apply. The tests are in that order, because the
/// first two write nothing and the third is the half that can destroy data.
/// </summary>
public class CsvImportTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "mightdo-csv-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    private WorkspaceSession _session = null!;

    /// <summary>A copy of the shared corpus, so a test may write to it.</summary>
    public async ValueTask InitializeAsync()
    {
        CopyInto(Fixtures.Path("workspace-v1"), _root);
        _session = await WorkspaceSession.OpenAsync(new TaskStore(new Core.Storage.Workspace(_root)));
    }

    public ValueTask DisposeAsync()
    {
        _session.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private static void CopyInto(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, target, StringComparison.Ordinal));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, target, StringComparison.Ordinal), overwrite: true);
        }
    }

    private WorkspaceConfig Config => _session.Snapshot.Config;

    private string StatusNamed(StatusType type) =>
        Config.Statuses.First(status => status.Type == type).Name;

    private ImportPlan PlanFor(string csv, ImportOptions? options = null) =>
        ImportPlan.Build(TaskCsv.Read(csv, Config), _session.Snapshot.Tasks, Config, options: options);

    private static string Csv(params string[] lines) => string.Join("\r\n", lines) + "\r\n";

    // ---- the round trip ----------------------------------------------------

    /// <summary>
    /// The single most valuable test in this feature: exporting a workspace and
    /// importing the result must be a genuine no-op, not two hundred rewritten
    /// files and two hundred fresh <c>updatedAt</c> stamps.
    /// </summary>
    [Fact]
    public async Task ExportingTheWorkspaceAndImportingItBackWritesNothing()
    {
        var before = _session.Snapshot.Tasks.ToDictionary(task => task.Id);
        var csv = TaskCsv.Write([.. before.Values], Config);

        var plan = await _session.PlanImportAsync(csv);

        Assert.Empty(plan.Errors);
        Assert.Equal(0, plan.CreateCount);
        Assert.Equal(0, plan.UpdateCount);
        Assert.Equal(before.Count, plan.UnchangedCount);
        Assert.False(plan.WritesAnything);

        var raised = 0;
        _session.Changed += (_, _) => raised++;
        await _session.ImportAsync(plan);

        Assert.Equal(0, raised);
        Assert.All(
            _session.Snapshot.Tasks,
            task => Assert.Equal(before[task.Id].UpdatedAt, task.UpdatedAt));
    }

    // ---- row errors --------------------------------------------------------

    [Fact]
    public void EveryDocumentedRowErrorIsReportedAtItsOwnLineAndStopsNothingElse()
    {
        var plan = PlanFor(Fixtures.ReadText("csv-v1", "errors", "every-error.csv"));

        Assert.Equal(
            [(2, "summary"), (3, "status"), (4, "status"), (5, "priority"), (6, "dueDate"),
             (7, "estimateMinutes"), (8, "reminders"), (9, "id"), (10, "id"), (11, "id")],
            plan.Errors.Select(error => (error.Line, error.Column)));

        // The one good row is applied regardless: a bad date on one line must
        // not refuse the file.
        var change = Assert.Single(plan.Changes);
        Assert.Equal(12, change.Line);
        Assert.Equal("Fine", change.Task.Summary);
    }

    [Fact]
    public void AnUnknownStatusNamesItselfAndSaysWhereToAddIt()
    {
        var plan = PlanFor(Csv("summary,status", "Anything,Doing It Later"));

        var error = Assert.Single(plan.Errors);
        Assert.Contains("Doing It Later", error.Message, StringComparison.Ordinal);
        Assert.Contains("Settings", error.Message, StringComparison.Ordinal);
        Assert.Empty(plan.Changes);
    }

    /// <summary>
    /// Quietly recreating a trashed task beside its own trashed copy is how a
    /// user ends up with two of it.
    /// </summary>
    [Fact]
    public async Task ARowNamingATrashedTaskIsRefusedAndSaysToRestoreIt()
    {
        var trashed = (await _session.LoadTrashAsync()).First();

        var plan = await _session.PlanImportAsync(
            Csv("id,summary,status", $"{trashed.Id},{trashed.Summary},{StatusNamed(StatusType.Initial)}"));

        var error = Assert.Single(plan.Errors);
        Assert.Contains("trash", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(plan.Changes);
    }

    // ---- categories and tags -----------------------------------------------

    [Fact]
    public void UnknownCategoriesAndTagsAreCreatedWhenTheOptionIsOn()
    {
        var plan = PlanFor(
            Csv(
                "summary,status,category,tags",
                $"One,{StatusNamed(StatusType.Initial)},Allotment,seedlings;spring",
                $"Two,{StatusNamed(StatusType.Initial)},Allotment,spring"));

        // Named twice, created once.
        Assert.Equal(["Allotment"], plan.NewCategories.Select(category => category.Name));
        Assert.Equal(["seedlings", "spring"], plan.NewTags.Select(tag => tag.Name));
    }

    [Fact]
    public void UnknownCategoriesAndTagsAreSkippedWhenTheOptionIsOff()
    {
        var plan = PlanFor(
            Csv("summary,status,category,tags", $"One,{StatusNamed(StatusType.Initial)},Allotment,seedlings"),
            new ImportOptions(CreateCategoriesAndTags: false));

        Assert.Empty(plan.NewCategories);
        Assert.Empty(plan.NewTags);

        // Not an error: the row still lands, without a category it cannot name.
        var change = Assert.Single(plan.Changes);
        Assert.Null(change.Task.CategoryId);
        Assert.Empty(change.Task.TagIds);
    }

    // ---- what an update may and may not touch ------------------------------

    [Fact]
    public void AColumnAbsentFromTheFileLeavesThatFieldAlone()
    {
        var existing = _session.Snapshot.Tasks.First(task => task.Notes.Count > 0);

        // The file speaks about the summary and nothing else. Deleting the
        // notes column from a spreadsheet must not delete everybody's notes.
        var plan = PlanFor(Csv("id,summary", $"{existing.Id},Renamed"));

        var change = Assert.Single(plan.Changes);
        Assert.Equal(ImportRowKind.Update, change.Kind);
        Assert.Equal("Renamed", change.Task.Summary);
        Assert.Equal(existing.Notes.Count, change.Task.Notes.Count);
        Assert.Equal(0, plan.NotesRemoved);
    }

    [Fact]
    public void ACellThatIsPresentAndEmptyClearsTheField()
    {
        var existing = _session.Snapshot.Tasks.First(task => task.DueDate is not null);

        var plan = PlanFor(Csv("id,dueDate", $"{existing.Id},"));

        Assert.Null(Assert.Single(plan.Changes).Task.DueDate);
    }

    [Fact]
    public void RemovingNotesAndStepsIsCountedSeparately()
    {
        var existing = _session.Snapshot.Tasks
            .First(task => task.Notes.Count > 0 && task.Steps.Count > 0);

        var plan = PlanFor(Csv("id,steps,notes", $"{existing.Id},,"));

        Assert.Equal(existing.Notes.Count, plan.NotesRemoved);
        Assert.Equal(existing.Steps.Count, plan.StepsRemoved);
    }

    [Fact]
    public void ATaskWithNoRowInTheFileIsLeftAlone()
    {
        var untouched = _session.Snapshot.Tasks.Count - 1;
        var one = _session.Snapshot.Tasks.First();

        var plan = PlanFor(Csv("id,summary", $"{one.Id},Renamed"));

        Assert.Single(plan.Changes);
        Assert.Equal(untouched, _session.Snapshot.Tasks.Count - 1);
    }

    /// <summary>
    /// Exporting, moving one line in a spreadsheet and importing must renumber
    /// only what moved — otherwise every round trip churns every step id.
    /// </summary>
    [Fact]
    public void ReorderingOneStepKeepsTheIdsOfTheStepsThatDidNotMove()
    {
        var existing = _session.Snapshot.Tasks.First(task => task.Steps.Count == 3);
        var steps = existing.Steps;

        var plan = PlanFor(Csv(
            "id,steps",
            $"{existing.Id},\"[{Mark(steps[0])}] {steps[0].Text}\n[{Mark(steps[2])}] {steps[2].Text}\n[{Mark(steps[1])}] {steps[1].Text}\""));

        var written = Assert.Single(plan.Changes).Task;
        Assert.Equal(steps[0].Id, written.Steps[0].Id);
        Assert.NotEqual(steps[1].Id, written.Steps[1].Id);
        Assert.NotEqual(steps[2].Id, written.Steps[2].Id);

        static string Mark(Step step) => step.Done ? "x" : " ";
    }

    // ---- completion dates --------------------------------------------------

    [Fact]
    public void ACompletionDateIsHonouredCreatingIntoAFinalStatus()
    {
        var completed = new DateTime(2025, 3, 4, 11, 30, 0, DateTimeKind.Utc);

        var plan = PlanFor(Csv(
            "summary,status,completedAt",
            $"Migrated,{StatusNamed(StatusType.Final)},{Instants.ToIso(completed)}"));

        Assert.Equal(completed, Assert.Single(plan.Changes).Task.CompletedAt);
    }

    [Fact]
    public void ACompletionDateIsIgnoredCreatingIntoAnyOtherStatus()
    {
        var plan = PlanFor(Csv(
            "summary,status,completedAt",
            $"Not done,{StatusNamed(StatusType.Active)},2025-03-04T11:30:00.000Z"));

        Assert.Null(Assert.Single(plan.Changes).Task.CompletedAt);
    }

    [Fact]
    public void MovingBetweenTwoFinalStatusesKeepsTheOriginalMoment()
    {
        var done = _session.Snapshot.Tasks.First(task => task.CompletedAt is not null);
        var other = Config.Statuses.First(
            status => status.Type == StatusType.Final && status.Id != done.StatusId);

        var plan = PlanFor(Csv(
            "id,status,completedAt",
            $"{done.Id},{other.Name},2001-01-01T00:00:00.000Z"));

        // The status rule owns the completion date on an update: the cell is
        // informational, and honouring both would let the two disagree.
        Assert.Equal(done.CompletedAt, Assert.Single(plan.Changes).Task.CompletedAt);
    }

    // ---- applying ----------------------------------------------------------

    [Fact]
    public async Task ImportingRaisesExactlyOneChangedEventAndWritesTheTasks()
    {
        var raised = 0;
        _session.Changed += (_, _) => raised++;

        var plan = await _session.PlanImportAsync(Csv(
            "summary,status,category,tags",
            $"First import,{StatusNamed(StatusType.Initial)},Allotment,seedlings",
            $"Second import,{StatusNamed(StatusType.Active)},Allotment,"));

        var outcome = await _session.ImportAsync(plan);

        Assert.Equal(1, raised);
        Assert.Equal(2, outcome.Created);

        // Two hundred calls to EditTaskAsync would mean two hundred snapshots
        // and two hundred redraws; this is why there is one entry point.
        Assert.Contains(_session.Snapshot.Tasks, task => task.Summary == "First import");
        Assert.Contains(Config.Categories, category => category.Name == "Allotment");
        Assert.Contains(Config.Tags, tag => tag.Name == "seedlings");
    }

    [Fact]
    public async Task ImportedTasksGoToTheBottomOfTheirColumn()
    {
        var status = Config.Statuses.First(s => s.Type == StatusType.Active);
        var bottom = _session.Snapshot.Tasks
            .Where(task => task.StatusId == status.Id)
            .Max(task => task.BoardRank)!;

        var plan = await _session.PlanImportAsync(Csv(
            "summary,status", $"One,{status.Name}", $"Two,{status.Name}"));
        await _session.ImportAsync(plan);

        var added = _session.Snapshot.Tasks
            .Where(task => task.Summary is "One" or "Two")
            .OrderBy(task => task.BoardRank, StringComparer.Ordinal)
            .ToList();

        // boardRank is not a column: new tasks land at the bottom, in file order.
        Assert.Equal("One", added[0].Summary);
        Assert.True(string.CompareOrdinal(bottom, added[0].BoardRank) < 0);
        Assert.True(string.CompareOrdinal(added[0].BoardRank, added[1].BoardRank) < 0);
    }

    [Fact]
    public async Task AnImportIsAnEditSoUpdatedTasksSayTheyWereJustUpdated()
    {
        var existing = _session.Snapshot.Tasks.First();

        var plan = await _session.PlanImportAsync(Csv("id,summary", $"{existing.Id},Renamed by import"));
        await _session.ImportAsync(plan);

        var written = _session.Snapshot.TaskById(existing.Id)!;
        Assert.Equal("Renamed by import", written.Summary);
        Assert.True(written.UpdatedAt > existing.UpdatedAt);
    }

    [Fact]
    public async Task AFailedWritePartwayReportsWhatWasAlreadyApplied()
    {
        var plan = await _session.PlanImportAsync(Csv(
            "summary,status",
            $"Lands,{StatusNamed(StatusType.Initial)}",
            $"Does not,{StatusNamed(StatusType.Initial)}"));

        // A directory where the second task's file has to go. Contrived, but it
        // fails the write the same way a full disk or a read-only synced folder
        // does, and only that one write.
        var blocked = plan.Changes[1].Task.Id;
        Directory.CreateDirectory(_session.Workspace.TaskFile(blocked));

        await Assert.ThrowsAsync<PartiallyAppliedException>(() => _session.ImportAsync(plan));

        // What landed stays written, and the session has re-read the workspace,
        // so what the user sees is what is there.
        Assert.Contains(_session.Snapshot.Tasks, task => task.Summary == "Lands");
        Assert.DoesNotContain(_session.Snapshot.Tasks, task => task.Summary == "Does not");
    }
}
