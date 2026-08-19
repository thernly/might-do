using MightDo.Core.Domain;
using MightDo.Core.Query;
using MightDo.Core.Session;
using MightDo.Core.Storage;

namespace MightDo.Core.Tests;

/// <summary>
/// Ports <c>test/app/workspace_controller_test.dart</c>, then adds the cases
/// that only matter once the layer is not single-threaded.
/// </summary>
public class WorkspaceSessionTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mightdo-session-" + Guid.NewGuid().ToString("N")[..8]);

    private WorkspaceSession _session = null!;

    public async Task InitializeAsync() =>
        _session = await WorkspaceSession.OpenAsync(
            new TaskStore(new Core.Storage.Workspace(_root)));

    public Task DisposeAsync()
    {
        _session.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    private Status StatusOfType(StatusType type) =>
        _session.Snapshot.Config.Statuses.First(s => s.Type == type);

    private MightDoTask Reload(MightDoTask task) => _session.Snapshot.TaskById(task.Id)!;

    // ---- creating tasks ----------------------------------------------------

    [Fact]
    public async Task NewTasksStartInTheDefaultInitialStatusWithNoCompletionDate()
    {
        var task = await _session.CreateTaskAsync("Write the thing");

        var config = _session.Snapshot.Config;
        Assert.Equal(config.DefaultStatusId, task.StatusId);
        Assert.Equal(StatusType.Initial, config.StatusById(task.StatusId)!.Type);
        Assert.Null(task.CompletedAt);
        Assert.False(task.IsComplete);
    }

    [Fact]
    public async Task CreatingATaskCapsTagsAtTheDocumentedMaximum()
    {
        var task = await _session.CreateTaskAsync(
            "Over-tagged", tagIds: [.. Enumerable.Range(0, 15).Select(i => $"tag-{i}")]);

        Assert.Equal(MightDoTask.MaxTags, task.TagIds.Count);
    }

    [Fact]
    public async Task UpdatingTagsCapsThemToo()
    {
        // The cap is one rule in one place, rather than something create
        // applies and update leaves to the UI to re-check.
        var task = await _session.CreateTaskAsync("Tagged");

        var updated = await _session.SetTagsAsync(
            task, [.. Enumerable.Range(0, 15).Select(i => $"tag-{i}")]);

        Assert.Equal(MightDoTask.MaxTags, updated.TagIds.Count);
    }

    [Fact]
    public async Task NewTasksAppendToTheBottomOfTheirColumn()
    {
        var first = await _session.CreateTaskAsync("First");
        var second = await _session.CreateTaskAsync("Second");

        Assert.True(string.CompareOrdinal(first.BoardRank, second.BoardRank) < 0);
    }

    // ---- edits against a stale record --------------------------------------

    [Fact]
    public async Task AnEditBuiltFromAStaleRecordOnlyChangesItsOwnField()
    {
        var task = await _session.CreateTaskAsync("Original");
        await _session.EditTaskAsync(task, current => current with { Description = "Notes" });

        // `task` predates the description edit, as a pane that has not been
        // told about it yet would hold.
        await _session.EditTaskAsync(task, current => current with { Priority = Priority.High });

        Assert.Equal("Notes", Reload(task).Description);
        Assert.Equal(Priority.High, Reload(task).Priority);
    }

    [Fact]
    public async Task AStaleEditDoesNotUndoAStatusMove()
    {
        // The damaging case: a status move carries the completion-date rule,
        // and a whole-record write from before the move would put both back.
        var task = await _session.CreateTaskAsync("Finish");
        var done = StatusOfType(StatusType.Final);
        await _session.MoveToStatusAsync(task, done.Id);

        await _session.EditTaskAsync(task, current => current with { Summary = "Finish it" });

        Assert.Equal(done.Id, Reload(task).StatusId);
        Assert.NotNull(Reload(task).CompletedAt);
        Assert.Equal("Finish it", Reload(task).Summary);
    }

    [Fact]
    public async Task ConcurrentEditsToDifferentFieldsBothSurvive()
    {
        var task = await _session.CreateTaskAsync("Race");

        // Both are launched from the same record, so whichever the gate lets
        // through second is working from a stale snapshot.
        var first = _session.EditTaskAsync(task, c => c with { Summary = "Renamed" });
        var second = _session.EditTaskAsync(task, c => c with { EstimateMinutes = 30 });
        await Task.WhenAll(first, second);

        Assert.Equal("Renamed", Reload(task).Summary);
        Assert.Equal(30, Reload(task).EstimateMinutes);
    }

    [Fact]
    public async Task AnEditThatChangesNothingWritesNothing()
    {
        var task = await _session.CreateTaskAsync("Unchanged");
        var before = Reload(task).UpdatedAt;

        await _session.EditTaskAsync(task, current => current);

        Assert.Equal(before, Reload(task).UpdatedAt);
    }

    // ---- where a status move puts the card ---------------------------------

    [Fact]
    public async Task AStatusMoveKeepsTheCardsRank()
    {
        // Pinned rather than assumed: the parity fixture records that a status
        // move carries the card's rank into the new column, so two cards in one
        // column can end up sharing a rank — which is why BoardProjection has to
        // place a drop between two of them without asking for the impossible.
        var initial = StatusOfType(StatusType.Initial);
        var active = StatusOfType(StatusType.Active);

        var here = await _session.CreateTaskAsync("First of its column", active.Id);
        var arriving = await _session.CreateTaskAsync("First of another", initial.Id);
        Assert.Equal(here.BoardRank, arriving.BoardRank);

        await _session.MoveToStatusAsync(arriving, active.Id);

        var column = BoardProjection.Column(_session.Snapshot.Tasks, active.Id);
        Assert.Equal(2, column.Count);
        Assert.Equal(column[0].BoardRank, column[1].BoardRank);
    }

    // ---- the completion-date rule ------------------------------------------

    [Fact]
    public async Task CompletionIsStampedOnEnteringAnyFinalStatus()
    {
        var task = await _session.CreateTaskAsync("Finish");

        await _session.MoveToStatusAsync(task, StatusOfType(StatusType.Final).Id);

        Assert.NotNull(Reload(task).CompletedAt);
        Assert.True(Reload(task).IsComplete);
    }

    [Fact]
    public async Task CompletionIsClearedOnLeavingAFinalStatus()
    {
        var task = await _session.CreateTaskAsync("Reopened");
        await _session.MoveToStatusAsync(task, StatusOfType(StatusType.Final).Id);

        await _session.MoveToStatusAsync(Reload(task), StatusOfType(StatusType.Active).Id);

        Assert.Null(Reload(task).CompletedAt);
    }

    [Fact]
    public async Task CompletionIsPreservedBetweenTwoFinalStatuses()
    {
        var finals = _session.Snapshot.Config.Statuses
            .Where(s => s.Type == StatusType.Final).ToList();
        Assert.True(finals.Count >= 2, "seed should provide Done and Abandoned");

        var task = await _session.CreateTaskAsync("Done then abandoned");
        await _session.MoveToStatusAsync(task, finals[0].Id);
        var stampedAt = Reload(task).CompletedAt;

        await _session.MoveToStatusAsync(Reload(task), finals[1].Id);

        Assert.Equal(stampedAt, Reload(task).CompletedAt);
    }

    [Fact]
    public async Task CompletionIsNotSetByActiveStatuses()
    {
        var task = await _session.CreateTaskAsync("Working");

        await _session.MoveToStatusAsync(task, StatusOfType(StatusType.Active).Id);

        Assert.Null(Reload(task).CompletedAt);
    }

    [Fact]
    public async Task MovingStatusWithoutARankKeepsTheBoardPosition()
    {
        // The kind of thing a null-means-unset sentinel gets backwards: a
        // move with no rank must leave the rank alone, not clear it.
        var task = await _session.CreateTaskAsync("Stays put");
        var rank = task.BoardRank;

        await _session.MoveToStatusAsync(task, StatusOfType(StatusType.Active).Id);

        Assert.Equal(rank, Reload(task).BoardRank);
    }

    // ---- board reordering --------------------------------------------------

    [Fact]
    public async Task DropsATaskBetweenTwoOthers()
    {
        var a = await _session.CreateTaskAsync("A");
        var b = await _session.CreateTaskAsync("B");
        var c = await _session.CreateTaskAsync("C");

        await _session.ReorderOnBoardAsync(c, a.StatusId, above: a, below: b);

        var column = BoardProjection.Column(_session.Snapshot.Tasks, a.StatusId);
        Assert.Equal(["A", "C", "B"], column.Select(t => t.Summary));
    }

    [Fact]
    public async Task MovingToAnotherColumnChangesStatusAndStampsCompletion()
    {
        var task = await _session.CreateTaskAsync("Drag me");
        var done = StatusOfType(StatusType.Final);

        await _session.ReorderOnBoardAsync(task, done.Id);

        var moved = Reload(task);
        Assert.Equal(done.Id, moved.StatusId);
        Assert.NotNull(moved.CompletedAt);
    }

    // ---- deleting a status -------------------------------------------------

    [Fact]
    public void DeletingTheDefaultStatusIsBlocked() =>
        Assert.Equal(
            StatusDeletionBlocker.IsDefault,
            _session.StatusDeletionBlockerFor(_session.Snapshot.Config.DefaultStatusId));

    [Fact]
    public async Task DeletingTheLastStatusOfATypeIsBlocked()
    {
        var actives = _session.Snapshot.Config.Statuses
            .Where(s => s.Type == StatusType.Active).ToList();
        for (var i = 0; i < actives.Count - 1; i++)
        {
            await _session.DeleteStatusAsync(actives[i].Id, actives[^1].Id);
        }

        var last = _session.Snapshot.Config.Statuses.First(s => s.Type == StatusType.Active);

        Assert.Equal(StatusDeletionBlocker.LastOfItsType,
            _session.StatusDeletionBlockerFor(last.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _session.DeleteStatusAsync(last.Id, last.Id));
    }

    [Fact]
    public async Task DeletingAStatusMovesItsTasksRatherThanDeletingThem()
    {
        var blocked = await _session.AddStatusAsync("Blocked elsewhere", StatusType.Active);
        var task = await _session.CreateTaskAsync("Stuck");
        await _session.MoveToStatusAsync(task, blocked.Id);
        var replacement = StatusOfType(StatusType.Active);

        await _session.DeleteStatusAsync(blocked.Id, replacement.Id);

        Assert.Null(_session.Snapshot.Config.StatusById(blocked.Id));
        Assert.Single(_session.Snapshot.Tasks);
        Assert.Equal(replacement.Id, Reload(task).StatusId);
    }

    [Fact]
    public async Task ReassigningIntoAFinalStatusIsStillACompletion()
    {
        var extra = await _session.AddStatusAsync("Shipping", StatusType.Active);
        var task = await _session.CreateTaskAsync("Ship it");
        await _session.MoveToStatusAsync(task, extra.Id);
        Assert.Null(Reload(task).CompletedAt);

        await _session.DeleteStatusAsync(extra.Id, StatusOfType(StatusType.Final).Id);

        Assert.NotNull(Reload(task).CompletedAt);
    }

    [Fact]
    public async Task DeletingAStatusRenumbersTheRestSoBoardOrderStaysContiguous()
    {
        var extra = await _session.AddStatusAsync("Temporary", StatusType.Active);

        await _session.DeleteStatusAsync(extra.Id, StatusOfType(StatusType.Active).Id);

        var orders = _session.Snapshot.Config.Statuses.Select(s => s.Order).ToList();
        Assert.Equal(Enumerable.Range(0, orders.Count), orders);
    }

    [Fact]
    public async Task DeletingAStatusIsOneChangeNotOnePerTask()
    {
        // Writing and notifying once per affected task would redraw the whole
        // list once per task and show the migration halfway through. One batch,
        // one event.
        var extra = await _session.AddStatusAsync("Doomed", StatusType.Active);
        foreach (var i in Enumerable.Range(0, 5))
        {
            var task = await _session.CreateTaskAsync($"Task {i}");
            await _session.MoveToStatusAsync(task, extra.Id);
        }

        var changes = 0;
        _session.Changed += (_, _) => Interlocked.Increment(ref changes);

        await _session.DeleteStatusAsync(extra.Id, StatusOfType(StatusType.Active).Id);

        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task ReassignmentMustNameAStatusThatExists()
    {
        var extra = await _session.AddStatusAsync("Doomed", StatusType.Active);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _session.DeleteStatusAsync(extra.Id, "01m07z0000000000000000gone"));
    }

    // ---- default status ----------------------------------------------------

    [Fact]
    public async Task TheDefaultStatusMustBeAnInitialStatus() =>
        await Assert.ThrowsAsync<ArgumentException>(
            () => _session.SetDefaultStatusAsync(StatusOfType(StatusType.Active).Id));

    [Fact]
    public async Task TheDefaultStatusCanMoveToAnotherInitialStatus()
    {
        var config = _session.Snapshot.Config;
        var other = config.Statuses.First(
            s => s.Type == StatusType.Initial && s.Id != config.DefaultStatusId);

        await _session.SetDefaultStatusAsync(other.Id);

        Assert.Equal(other.Id, _session.Snapshot.Config.DefaultStatusId);
    }

    // ---- categories and tags -----------------------------------------------

    [Fact]
    public async Task DeletingACategoryClearsItFromTasksByDefault()
    {
        var category = await _session.AddCategoryAsync("Home", 0xFF00FF00);
        var task = await _session.CreateTaskAsync("Fix the door", categoryId: category.Id);

        await _session.DeleteCategoryAsync(category.Id);

        Assert.Empty(_session.Snapshot.Config.Categories);
        Assert.Null(Reload(task).CategoryId);
        Assert.Single(_session.Snapshot.Tasks);
    }

    [Fact]
    public async Task DeletingACategoryCanReassignInstead()
    {
        var from = await _session.AddCategoryAsync("Old", 0xFF00FF00);
        var to = await _session.AddCategoryAsync("New", 0xFF0000FF);
        var task = await _session.CreateTaskAsync("Move me", categoryId: from.Id);

        await _session.DeleteCategoryAsync(from.Id, to.Id);

        Assert.Equal(to.Id, Reload(task).CategoryId);
    }

    [Fact]
    public async Task AddingAnExistingTagByNameReusesIt()
    {
        var first = await _session.AddTagAsync("urgent");
        var second = await _session.AddTagAsync("URGENT");

        Assert.Equal(first.Id, second.Id);
        Assert.Single(_session.Snapshot.Config.Tags);
    }

    [Fact]
    public async Task DeletingATagDetachesItFromEveryTask()
    {
        var tag = await _session.AddTagAsync("waiting");
        var task = await _session.CreateTaskAsync("Tagged", tagIds: [tag.Id]);

        await _session.DeleteTagAsync(tag.Id);

        Assert.Empty(_session.Snapshot.Config.Tags);
        Assert.Empty(Reload(task).TagIds);
    }

    // ---- steps, notes and reminders ----------------------------------------

    [Fact]
    public async Task TickingEveryStepDoesNotCompleteTheTask()
    {
        var task = await _session.CreateTaskAsync("Multi-step");
        await _session.AddStepAsync(Reload(task), "One");
        await _session.AddStepAsync(Reload(task), "Two");

        foreach (var step in Reload(task).Steps)
        {
            await _session.SetStepDoneAsync(Reload(task), step.Id, true);
        }

        var updated = Reload(task);
        Assert.Equal(2, updated.StepsDone);
        Assert.False(updated.IsComplete);
    }

    [Fact]
    public async Task NotesAccumulateWithUtcTimestamps()
    {
        var task = await _session.CreateTaskAsync("Logged");
        await _session.AddNoteAsync(Reload(task), "First");
        await _session.AddNoteAsync(Reload(task), "Second");

        var notes = Reload(task).Notes;
        Assert.Equal(["First", "Second"], notes.Select(n => n.Body));
        Assert.All(notes, note => Assert.Equal(DateTimeKind.Utc, note.CreatedAt.Kind));
    }

    [Fact]
    public async Task ADismissedReminderStopsBeingOutstanding()
    {
        var task = await _session.CreateTaskAsync("Remind me");
        await _session.AddReminderAsync(Reload(task), DateTime.UtcNow.AddHours(-1));

        var due = Reload(task).OutstandingReminders(DateTime.UtcNow);
        Assert.Single(due);

        await _session.DismissRemindersAsync(Reload(task), Ids(due[0].Id));

        Assert.Empty(Reload(task).OutstandingReminders(DateTime.UtcNow));
    }

    [Fact]
    public async Task AFutureReminderIsNotOutstandingYet()
    {
        var task = await _session.CreateTaskAsync("Later");
        await _session.AddReminderAsync(Reload(task), DateTime.UtcNow.AddDays(1));

        Assert.Empty(Reload(task).OutstandingReminders(DateTime.UtcNow));
    }

    [Fact]
    public async Task AFiredButUndismissedReminderIsStillOutstanding()
    {
        // ADR-0004: the in-app surface is the contract, so a reminder stays
        // there until acknowledged even after an OS notification fired.
        var task = await _session.CreateTaskAsync("Nagging");
        await _session.AddReminderAsync(Reload(task), DateTime.UtcNow.AddHours(-1));
        var reminder = Reload(task).Reminders.Single();

        await _session.MarkRemindersFiredAsync(Reload(task), Ids(reminder.Id));

        var updated = Reload(task).Reminders.Single();
        Assert.NotNull(updated.FiredAt);
        Assert.False(updated.IsPending);
        Assert.True(updated.IsOutstanding);
        Assert.Single(Reload(task).OutstandingReminders(DateTime.UtcNow));
    }

    [Fact]
    public async Task TwoRemindersDueAtOnceBothStayFired()
    {
        // Marking reminders one at a time from a task snapshot captured before
        // the loop would have the second write discard the first's firedAt, and
        // that reminder would re-fire on every tick, forever.
        var task = await _session.CreateTaskAsync("Two reminders");
        await _session.AddReminderAsync(Reload(task), DateTime.UtcNow.AddHours(-2));
        await _session.AddReminderAsync(Reload(task), DateTime.UtcNow.AddHours(-1));
        var ids = Reload(task).Reminders.Select(r => r.Id).ToHashSet();

        await _session.MarkRemindersFiredAsync(Reload(task), ids);

        Assert.All(Reload(task).Reminders, r => Assert.NotNull(r.FiredAt));
        Assert.DoesNotContain(Reload(task).Reminders, r => r.IsPending);
    }

    // ---- trashing and persistence ------------------------------------------

    [Fact]
    public async Task TrashingRemovesTheTaskFromTheWorkingSet()
    {
        var task = await _session.CreateTaskAsync("Mistake");

        await _session.TrashTaskAsync(task);

        Assert.Empty(_session.Snapshot.Tasks);
        Assert.Null(_session.Snapshot.TaskById(task.Id));
    }

    [Fact]
    public async Task TheTrashCanBeListedAndRestoredFrom()
    {
        var task = await _session.CreateTaskAsync("Trashed by accident");
        await _session.TrashTaskAsync(task);

        var trashed = await _session.LoadTrashAsync();
        Assert.Equal(task.Id, Assert.Single(trashed).Id);

        var restored = await _session.RestoreTaskAsync(task.Id);

        Assert.Equal(task.Id, restored!.Id);
        Assert.NotNull(_session.Snapshot.TaskById(task.Id));
        Assert.Empty(await _session.LoadTrashAsync());
    }

    [Fact]
    public async Task RestoringNothingIsANoOp()
    {
        Assert.Null(await _session.RestoreTaskAsync("not-a-task-id"));
    }

    [Fact]
    public async Task ChangesSurviveAReloadFromDisk()
    {
        var task = await _session.CreateTaskAsync("Persisted");
        await _session.MoveToStatusAsync(task, StatusOfType(StatusType.Active).Id);
        await _session.AddNoteAsync(Reload(task), "A note");

        using var reopened = await WorkspaceSession.OpenAsync(
            new TaskStore(new Core.Storage.Workspace(_root)));

        var loaded = reopened.Snapshot.TaskById(task.Id)!;
        Assert.Equal("Persisted", loaded.Summary);
        Assert.Equal("A note", loaded.Notes.Single().Body);
        Assert.Equal(StatusType.Active,
            reopened.Snapshot.Config.StatusById(loaded.StatusId)!.Type);
    }

    // ---- things that only matter once it isn't single-threaded --------------

    [Fact]
    public async Task ConcurrentWritesAllLandAndNoneClobberAnother()
    {
        // Dart got this free from its single isolate. .NET has real threads, so
        // it has to be arranged.
        await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(i => _session.CreateTaskAsync($"Task {i}")));

        Assert.Equal(50, _session.Snapshot.Tasks.Count);

        using var reopened = await WorkspaceSession.OpenAsync(
            new TaskStore(new Core.Storage.Workspace(_root)));
        Assert.Equal(50, reopened.Snapshot.Tasks.Count);

        // Every task got its own board rank rather than racing for the same one.
        Assert.Equal(50, _session.Snapshot.Tasks.Select(t => t.BoardRank).Distinct().Count());
    }

    [Fact]
    public async Task InterleavedRefreshesDoNotLoseWrites()
    {
        var writes = Enumerable.Range(0, 25).Select(i => _session.CreateTaskAsync($"Task {i}"));
        var refreshes = Enumerable.Range(0, 10).Select(_ => _session.RefreshAsync());

        await Task.WhenAll(writes.Cast<Task>().Concat(refreshes));

        Assert.Equal(25, _session.Snapshot.Tasks.Count);
    }

    [Fact]
    public async Task ARefreshThatFindsNothingNewSaysNothing()
    {
        // ADR-0003 makes reloads frequent and idempotent. Announcing an
        // unchanged workspace would redraw the UI every time a sync client
        // touched a file.
        await _session.CreateTaskAsync("Settled");
        var changes = 0;
        _session.Changed += (_, _) => Interlocked.Increment(ref changes);

        await _session.RefreshAsync();

        Assert.Equal(0, changes);
    }

    /// <remarks>
    /// The case above depends on the machine's clock producing a digit the file
    /// cannot hold, which only some do. This one supplies the awkward moment
    /// itself, through the caller-facing door a reminder time comes in by, so it
    /// asks the same question everywhere.
    /// </remarks>
    [Fact]
    public async Task ARefreshSaysNothingAboutAMomentFinerThanTheFileCanHold()
    {
        var task = await _session.CreateTaskAsync("Settled");
        await _session.AddReminderAsync(
            Reload(task),
            new DateTime(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc).AddTicks(1_234_567));

        var changes = 0;
        _session.Changed += (_, _) => Interlocked.Increment(ref changes);

        await _session.RefreshAsync();

        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task ARefreshThatFindsAnExternalEditAnnouncesIt()
    {
        var task = await _session.CreateTaskAsync("Edited elsewhere");
        var changes = 0;
        _session.Changed += (_, _) => Interlocked.Increment(ref changes);

        // Another machine's edit, arriving through the sync client.
        await Elsewhere(task with { Summary = "Edited on the laptop" });

        await _session.RefreshAsync();

        Assert.Equal(1, changes);
        Assert.Equal("Edited on the laptop", Reload(task).Summary);
    }

    // ---- writes that meet an external edit ---------------------------------

    [Fact]
    public async Task SavingOverAnExternalEditKeepsTheOtherVersionAsAConflict()
    {
        var task = await _session.CreateTaskAsync("Ours");

        // The sync client lands another machine's edit after we loaded, and
        // before we save. Nothing tells the session about it in time.
        await Elsewhere(task with { Summary = "Theirs" });

        await _session.EditTaskAsync(task, current => current with { Description = "Notes" });

        // Our save still wins the file the user is looking at...
        var ours = await ReadTaskFile(task.Id);
        Assert.Equal("Ours", ours!.Summary);
        Assert.Equal("Notes", ours.Description);

        // ...but their edit is beside it rather than gone.
        var conflict = Assert.Single(ConflictCopies());
        Assert.Contains("\"summary\": \"Theirs\"", await File.ReadAllTextAsync(conflict));
        Assert.Contains(task.Id, Path.GetFileName(conflict));
    }

    [Fact]
    public async Task APreservedConflictReachesTheSnapshotOnTheNextRefresh()
    {
        var task = await _session.CreateTaskAsync("Ours");
        await Elsewhere(task with { Summary = "Theirs" });
        await _session.EditTaskAsync(task, current => current with { Priority = Priority.High });

        await _session.RefreshAsync();

        var conflict = Assert.Single(_session.Snapshot.Conflicts);
        Assert.Equal(task.Id, conflict.TaskId);
    }

    [Fact]
    public async Task OurOwnRepeatedWritesAreNeverMistakenForSomebodyElsesEdit()
    {
        var task = await _session.CreateTaskAsync("Ours");

        for (var i = 0; i < 5; i++)
        {
            await _session.EditTaskAsync(task, current => current with { Summary = $"Edit {i}" });
        }

        await _session.RefreshAsync();
        Assert.Empty(ConflictCopies());
        Assert.Empty(_session.Snapshot.Conflicts);
    }

    [Fact]
    public async Task AnExternalEditWeHaveAlreadyReloadedIsNotAConflict()
    {
        var task = await _session.CreateTaskAsync("Ours");
        await Elsewhere(task with { Summary = "Theirs" });

        // Having seen their version, our next save is an edit of it, not a
        // blind overwrite of something we never read.
        await _session.RefreshAsync();
        await _session.EditTaskAsync(task, current => current with { Description = "Notes" });

        Assert.Empty(ConflictCopies());
        Assert.Equal("Theirs", Reload(task).Summary);
    }

    [Fact]
    public async Task ATaskDeletedElsewhereIsRewrittenRatherThanPreserved()
    {
        var task = await _session.CreateTaskAsync("Ours");
        File.Delete(_session.Workspace.TaskFile(task.Id));

        await _session.EditTaskAsync(task, current => current with { Summary = "Ours again" });

        // There was nothing left to keep, so a conflict copy would only be noise.
        Assert.Empty(ConflictCopies());
        Assert.Equal("Ours again", (await ReadTaskFile(task.Id))!.Summary);
    }

    [Fact]
    public async Task SavingConfigOverAnExternalEditKeepsTheOtherVersionToo()
    {
        // The config is a single file, so two machines editing different parts
        // of it collide on every save rather than only on the same task.
        var theirs = _session.Snapshot.Config;
        await WorkspaceFiles.WriteJsonAtomicAsync(
            _session.Workspace.ConfigFile,
            theirs with
            {
                Categories = [.. theirs.Categories, new Category(Ulid.New(), "Theirs", 0xFF2196F3)],
            });

        await _session.AddStatusAsync("Ours", StatusType.Active);

        await _session.RefreshAsync();
        Assert.Contains(_session.Snapshot.Config.Statuses, s => s.Name == "Ours");
        var conflict = Assert.Single(_session.Snapshot.Conflicts);
        Assert.Contains("\"name\": \"Theirs\"", await File.ReadAllTextAsync(conflict.FullPath));
    }

    /// <summary>
    /// Another writer of the same folder — a sync client, or a second copy of
    /// the app — dropping a task file the session knows nothing about.
    /// </summary>
    private Task Elsewhere(MightDoTask task) =>
        WorkspaceFiles.WriteJsonAtomicAsync(_session.Workspace.TaskFile(task.Id), task);

    private Task<MightDoTask?> ReadTaskFile(string taskId) =>
        WorkspaceFiles.ReadJsonAsync<MightDoTask>(_session.Workspace.TaskFile(taskId));

    /// <summary>Files in <c>tasks/</c> that are not tasks.</summary>
    private string[] ConflictCopies() =>
        [.. Directory.EnumerateFiles(Path.Combine(_root, "tasks"))
            .Where(path => !WorkspaceFiles.IsOwnTaskFile(Path.GetFileName(path)))];

    [Fact]
    public async Task AttachingAFileCopiesItIntoTheWorkspace()
    {
        var source = Path.Combine(_root, "original.txt");
        await File.WriteAllTextAsync(source, "the original bytes");
        var task = await _session.CreateTaskAsync("Has a file");

        var updated = await _session.AttachFileAsync(task, source);

        var attachment = Assert.Single(updated.Attachments);
        Assert.Equal("original.txt", attachment.OriginalName);
        Assert.StartsWith(attachment.Id, attachment.StoredName);
        Assert.Equal(18, attachment.SizeBytes);

        // The copy is authoritative: the user's original can vanish afterwards.
        File.Delete(source);
        Assert.True(File.Exists(_session.Workspace.AttachmentFile(attachment.StoredName)));
    }

    [Fact]
    public async Task AFailedAttachLeavesNoCopyBehind()
    {
        var source = Path.Combine(_root, "original.txt");
        await File.WriteAllTextAsync(source, "the original bytes");
        var task = await _session.CreateTaskAsync("Save will fail");

        // The bytes are copied before the record that points at them, so the
        // save is the step that has to be made to fail.
        BlockTaskFile(task.Id);

        await Assert.ThrowsAnyAsync<Exception>(() => _session.AttachFileAsync(task, source));

        Assert.Empty(Directory.GetFiles(_session.Workspace.AttachmentsDir));
        Assert.Empty(Reload(task).Attachments);
    }

    [Fact]
    public async Task DeletingAnAttachmentKeepsItsBytesInTheTrash()
    {
        var source = Path.Combine(_root, "original.txt");
        await File.WriteAllTextAsync(source, "the original bytes");
        var task = await _session.CreateTaskAsync("Has a file");
        var attached = await _session.AttachFileAsync(task, source);
        var stored = Assert.Single(attached.Attachments).StoredName;

        var updated = await _session.DeleteAttachmentAsync(attached, attached.Attachments[0].Id);

        Assert.Empty(updated.Attachments);
        Assert.False(File.Exists(_session.Workspace.AttachmentFile(stored)));
        Assert.True(File.Exists(_session.Workspace.TrashedAttachmentFile(stored)));
    }

    /// <summary>
    /// Makes every write to a task's file fail, by putting a directory where
    /// the file goes. Nothing can rename onto it or read it as a file.
    /// </summary>
    private void BlockTaskFile(string taskId)
    {
        var path = _session.Workspace.TaskFile(taskId);
        File.Delete(path);
        Directory.CreateDirectory(path);
    }

    private static IReadOnlySet<string> Ids(params string[] ids) => new HashSet<string>(ids);
}
