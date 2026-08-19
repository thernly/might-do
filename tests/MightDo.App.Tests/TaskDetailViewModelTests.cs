using MightDo.App.ViewModels;
using MightDo.Core.Domain;
using MightDo.Core.Session;
using MightDo.Core.Storage;

namespace MightDo.App.Tests;

/// <summary>
/// The detail pane is where every task edit happens, so its behaviour is worth
/// testing directly rather than through a window. Nothing here needs a UI
/// thread: the view model talks to <see cref="WorkspaceSession"/> and holds no
/// Avalonia types.
/// </summary>
public class TaskDetailViewModelTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mightdo-detail-" + Guid.NewGuid().ToString("N")[..8]);

    private WorkspaceSession _session = null!;
    private MightDoTask _task = null!;
    private TaskDetailViewModel _vm = null!;

    public async ValueTask InitializeAsync()
    {
        _session = await WorkspaceSession.OpenAsync(
            new TaskStore(new Core.Storage.Workspace(_root)));
        _task = await _session.CreateTaskAsync("Original summary");
        _vm = new TaskDetailViewModel(_session, _task, new NoFilePicker());
    }

    public ValueTask DisposeAsync()
    {
        _session.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private MightDoTask Current => _session.Snapshot.TaskById(_task.Id)!;

    private Status StatusOfType(StatusType type) =>
        _session.Snapshot.Config.Statuses.First(s => s.Type == type);

    /// <summary>The session writes asynchronously; give it a moment to land.</summary>
    private static async Task Settle() => await Task.Delay(50);

    [Fact]
    public void LoadsTheTaskItWasGiven()
    {
        Assert.Equal("Original summary", _vm.Summary);
        Assert.Equal(_task.Id, _vm.TaskId);
        Assert.Equal(Priority.Medium, _vm.SelectedPriority);
        Assert.Null(_vm.DueDate);
        Assert.Null(_vm.CompletedLabel);
    }

    [Fact]
    public async Task EditingTheSummarySavesIt()
    {
        _vm.Summary = "Edited";
        await Settle();

        Assert.Equal("Edited", Current.Summary);
    }

    [Fact]
    public async Task AnEmptySummaryIsRefusedRatherThanSaved()
    {
        // A task with no summary has no label anywhere it appears.
        _vm.Summary = "   ";
        await Settle();

        Assert.Equal("Original summary", Current.Summary);
    }

    [Fact]
    public async Task EditingTheDueDateKeepsItACalendarDay()
    {
        // The bug CalendarDate exists to prevent: a due date must not shift a
        // day because of a timezone conversion. Both ends of the day are tried,
        // so that whichever way this machine's zone leans, converting rather
        // than reading off the calendar components moves one of them.
        _vm.DueDate = new DateTime(2026, 8, 21, 23, 30, 0, DateTimeKind.Utc);
        await Settle();
        Assert.Equal(new CalendarDate(2026, 8, 21), Current.DueDate);

        _vm.DueDate = new DateTime(2026, 8, 21, 0, 30, 0, DateTimeKind.Utc);
        await Settle();
        Assert.Equal(new CalendarDate(2026, 8, 21), Current.DueDate);
    }

    [Fact]
    public async Task ClearingTheDueDateRemovesIt()
    {
        _vm.DueDate = new DateTime(2026, 8, 21);
        await Settle();
        _vm.DueDate = null;
        await Settle();

        Assert.Null(Current.DueDate);
    }

    [Fact]
    public async Task MovingStatusAppliesTheCompletionRule()
    {
        // Status is not an ordinary field edit — it carries the rule from
        // ADR-0002, so it must go through the session rather than a record edit.
        var done = StatusOfType(StatusType.Final);

        _vm.SelectedStatus = new StatusOption(done.Id, done.Name);
        await Settle();

        Assert.Equal(done.Id, Current.StatusId);
        Assert.NotNull(Current.CompletedAt);
    }

    [Fact]
    public async Task LeavingAFinalStatusClearsTheCompletionDate()
    {
        var done = StatusOfType(StatusType.Final);
        var active = StatusOfType(StatusType.Active);

        _vm.SelectedStatus = new StatusOption(done.Id, done.Name);
        await Settle();
        _vm.Refresh(Current);
        _vm.SelectedStatus = new StatusOption(active.Id, active.Name);
        await Settle();

        Assert.Null(Current.CompletedAt);
    }

    [Fact]
    public async Task AStatusMoveIsNotUndoneByAnEditTypedRightAfterIt()
    {
        // The pane has not been told about the move yet when the summary
        // changes, so the summary save is built from the pre-move task.
        var done = StatusOfType(StatusType.Final);

        _vm.SelectedStatus = new StatusOption(done.Id, done.Name);
        _vm.Summary = "Edited after moving";
        await _vm.PendingSave;
        await Settle();

        Assert.Equal(done.Id, Current.StatusId);
        Assert.NotNull(Current.CompletedAt);
        Assert.Equal("Edited after moving", Current.Summary);
    }

    [Fact]
    public async Task TwoQuickFieldEditsBothLand()
    {
        _vm.Summary = "Renamed";
        _vm.EstimateMinutes = "45";
        await _vm.PendingSave;
        await Settle();

        Assert.Equal("Renamed", Current.Summary);
        Assert.Equal(45, Current.EstimateMinutes);
    }

    [Fact]
    public async Task NonNumericEstimatesAreTreatedAsUnset()
    {
        _vm.EstimateMinutes = "not a number";
        await Settle();

        Assert.Null(Current.EstimateMinutes);
    }

    [Fact]
    public async Task EstimateAndActualProduceAVariance()
    {
        _vm.EstimateMinutes = "60";
        await Settle();
        _vm.TotalTimeMinutes = "90";
        await Settle();

        Assert.Equal(30, Current.EstimateVariance);
        _vm.Refresh(Current);
        Assert.Contains("over", _vm.VarianceLabel);
    }

    [Fact]
    public async Task RefreshingDoesNotWriteAnything()
    {
        // A rescan calls Refresh constantly. If re-reading echoed back as a
        // write, the app would fight the sync client forever.
        _vm.Summary = "Settled";
        await Settle();
        var before = Current.UpdatedAt;

        for (var i = 0; i < 5; i++) _vm.Refresh(Current);
        await Settle();

        Assert.Equal(before, Current.UpdatedAt);
    }

    [Fact]
    public async Task AddingAStepDoesNotCompleteTheTask()
    {
        await _vm.AddStepCommand.ExecuteAsync(null!);
        _vm.NewStepText = "One";
        await _vm.AddStepCommand.ExecuteAsync(null!);
        _vm.Refresh(Current);

        var step = Assert.Single(Current.Steps);
        Assert.Equal("One", step.Text);

        await _vm.ToggleStepCommand.ExecuteAsync(new StepViewModel(step with { Done = true }));
        await Settle();

        Assert.True(Current.Steps.Single().Done);
        Assert.False(Current.IsComplete);
    }

    [Fact]
    public async Task AddingANoteClearsTheBoxAndKeepsOrder()
    {
        _vm.NewNoteBody = "First";
        await _vm.AddNoteCommand.ExecuteAsync(null!);
        Assert.Equal("", _vm.NewNoteBody);

        _vm.NewNoteBody = "Second";
        await _vm.AddNoteCommand.ExecuteAsync(null!);

        Assert.Equal(["First", "Second"], Current.Notes.Select(n => n.Body));
    }

    [Fact]
    public async Task AddingAReminderCombinesTheDateAndTime()
    {
        _vm.NewReminderDate = new DateTime(2026, 9, 1);
        _vm.NewReminderTime = new TimeSpan(14, 30, 0);

        await _vm.AddReminderCommand.ExecuteAsync(null!);

        var reminder = Assert.Single(Current.Reminders);
        var local = reminder.RemindAt.ToLocalTime();
        Assert.Equal(new DateTime(2026, 9, 1, 14, 30, 0), local);
    }

    [Fact]
    public async Task TypingTagNamesReusesExistingTagsRatherThanDuplicating()
    {
        var existing = await _session.AddTagAsync("urgent");

        _vm.TagNames = "urgent, Urgent, waiting";
        await _vm.CommitTagsCommand.ExecuteAsync(null!);

        // Case-insensitive reuse, and the duplicate collapses.
        Assert.Equal(2, _session.Snapshot.Config.Tags.Count);
        Assert.Equal(2, Current.TagIds.Count);
        Assert.Contains(existing.Id, Current.TagIds);
    }

    [Fact]
    public async Task TagsAreCappedAtTheDocumentedMaximum()
    {
        _vm.TagNames = string.Join(", ", Enumerable.Range(0, 15).Select(i => $"tag{i}"));
        await _vm.CommitTagsCommand.ExecuteAsync(null!);

        Assert.Equal(MightDoTask.MaxTags, Current.TagIds.Count);
    }

    [Fact]
    public async Task SaysSoWhenAnAttachmentsBytesHaveGone()
    {
        var source = Path.Combine(_root, "original.txt");
        await File.WriteAllTextAsync(source, "the original bytes");
        var attached = await _session.AttachFileAsync(_task, source);
        var stored = attached.Attachments[0].StoredName;

        _vm.Refresh(attached);
        Assert.False(Assert.Single(_vm.Attachments).IsMissing);

        // Something else in the sync folder removed the file. The record is
        // still there, and looks exactly like a working attachment until it
        // says otherwise.
        File.Delete(_session.Workspace.AttachmentFile(stored));
        _vm.Refresh(attached);

        var attachment = Assert.Single(_vm.Attachments);
        Assert.True(attachment.IsMissing);
        Assert.Equal("file missing", attachment.Size);
    }

    private sealed class NoFilePicker : IFilePicker
    {
        public Task<string?> PickFileAsync(string title) => Task.FromResult<string?>(null);
    }
}
