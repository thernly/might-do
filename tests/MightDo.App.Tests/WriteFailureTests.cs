using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MightDo.App.ViewModels;
using MightDo.Core.Domain;
using MightDo.Core.Session;
using MightDo.Core.Storage;
using MightDo.Platform;

namespace MightDo.App.Tests;

/// <summary>
/// What happens when the workspace will not take a write.
/// </summary>
/// <remarks>
/// The folder is the user's own — inside OneDrive, on a removable drive, on a
/// volume that can fill up — so a refused write is an ordinary event rather than
/// an exotic one. Commands run through <c>AsyncRelayCommand</c>, which rethrows
/// a failed command onto the UI thread where nothing catches it, so a command
/// that lets a write failure escape closes the application. Every one of them
/// must say so instead.
/// <para>
/// The failure is arranged by putting a <i>directory</i> where the task's JSON
/// file belongs: the rename that publishes the file cannot overwrite it, on
/// every platform, and nothing has to change permissions to arrange it.
/// </para>
/// </remarks>
public class WriteFailureTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mightdo-writefail-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly List<WorkspaceViewModel> _opened = [];

    private WorkspaceSession _session = null!;
    private MightDoTask _task = null!;

    public async ValueTask InitializeAsync()
    {
        _session = await WorkspaceSession.OpenAsync(
            new TaskStore(new Core.Storage.Workspace(_root)));
        _task = await _session.CreateTaskAsync("A task to break saving for");
    }

    public ValueTask DisposeAsync()
    {
        foreach (var workspace in _opened) workspace.Dispose();
        _session.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    /// <summary>Makes every further write of <paramref name="task"/> fail.</summary>
    private void BlockWritesTo(MightDoTask task)
    {
        var file = _session.Workspace.TaskFile(task.Id);
        File.Delete(file);
        Directory.CreateDirectory(file);
    }

    /// <summary>
    /// Makes trashing fail, by putting a file where <c>.trash/tasks</c> goes.
    /// </summary>
    /// <remarks>
    /// Trashing moves the file rather than writing it, so a directory in the
    /// task's place is not in its way — an absent source is a no-op by design.
    /// The destination is what has to be unavailable.
    /// </remarks>
    private void BlockTrashing()
    {
        var dir = _session.Workspace.TrashTasksDir;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        File.WriteAllText(dir, "not a directory");
    }

    private TaskDetailViewModel Detail() =>
        new(_session, _session.Snapshot.TaskById(_task.Id)!, new NoFilePicker());

    // ---- the detail pane ---------------------------------------------------

    [Fact]
    public async Task AddingAStepToAnUnwritableTaskIsReportedRatherThanThrown()
    {
        var detail = Detail();
        BlockWritesTo(_task);

        detail.NewStepText = "Buy the thing";
        await detail.AddStepCommand.ExecuteAsync(null);

        Assert.NotNull(detail.SaveError);
        Assert.Contains("could not be saved", detail.SaveError);
    }

    [Fact]
    public async Task EveryDetailPaneWriteReportsRatherThanThrows()
    {
        var detail = Detail();
        BlockWritesTo(_task);

        // The whole command surface, because it is the inconsistency that bites:
        // one guarded path and eleven unguarded ones is worse than none.
        detail.NewStepText = "A step";
        await detail.AddStepCommand.ExecuteAsync(null);
        Assert.NotNull(detail.SaveError);

        detail.NewNoteBody = "A note";
        await detail.AddNoteCommand.ExecuteAsync(null);
        Assert.NotNull(detail.SaveError);

        detail.NewReminderDate = DateTime.Today;
        await detail.AddReminderCommand.ExecuteAsync(null);
        Assert.NotNull(detail.SaveError);

        detail.TagNames = "urgent";
        await detail.CommitTagsCommand.ExecuteAsync(null);
        Assert.NotNull(detail.SaveError);
    }

    [Fact]
    public async Task AWriteThatWorksAgainClearsTheError()
    {
        var detail = Detail();
        var file = _session.Workspace.TaskFile(_task.Id);

        BlockWritesTo(_task);
        detail.NewStepText = "First try";
        await detail.AddStepCommand.ExecuteAsync(null);
        Assert.NotNull(detail.SaveError);

        Directory.Delete(file);

        detail.NewStepText = "Second try";
        await detail.AddStepCommand.ExecuteAsync(null);

        Assert.Null(detail.SaveError);
    }

    // ---- the workspace -----------------------------------------------------

    [AvaloniaFact]
    public async Task MovingACardThatCannotBeSavedIsReportedRatherThanThrown()
    {
        // The only caller is a drop handler, which is an async void: anything
        // this throws ends the process rather than the gesture.
        var workspace = await OpenWorkspaceAsync();

        // The status is read from the filter list rather than from a board
        // column: only the view on screen is projected, and this workspace opens
        // on the list.
        var target = workspace.Statuses.Last();

        BlockWritesTo(_task);

        await workspace.MoveOnBoardAsync(_task.Id, target.Id, beforeTaskId: null);

        Assert.NotNull(workspace.Banner);
        Assert.Contains("could not be moved", workspace.Banner);
    }

    [AvaloniaFact]
    public async Task TrashingATaskThatCannotBeMovedIsReportedRatherThanThrown()
    {
        var workspace = await OpenWorkspaceAsync();
        BlockTrashing();

        await workspace.TrashTaskCommand.ExecuteAsync(
            workspace.Tasks.Single(row => row.Id == _task.Id));

        Assert.NotNull(workspace.Banner);
        Assert.Contains("could not be moved to the trash", workspace.Banner);
    }

    /// <summary>
    /// A command failure says what happened and stops there — it does not tell
    /// the user to press Refresh, which would be an instruction to fix nothing.
    /// A failed rescan still does, because there the list really may be stale.
    /// </summary>
    [AvaloniaFact]
    public async Task ACommandFailureDoesNotAskForARefresh()
    {
        var workspace = await OpenWorkspaceAsync();
        BlockWritesTo(_task);

        await workspace.MoveOnBoardAsync(
            _task.Id, workspace.Statuses.Last().Id, beforeTaskId: null);

        Assert.NotNull(workspace.Banner);
        Assert.DoesNotContain("press Refresh", workspace.Banner);
    }

    private async Task<WorkspaceViewModel> OpenWorkspaceAsync()
    {
        // Its own session, because the view model owns and disposes what it opens.
        var settings = AppSettings.Load(Path.Combine(_root, "settings.json"));
        var workspace = await WorkspaceViewModel.OpenAsync(
            new TaskStore(new Core.Storage.Workspace(_root)), settings, new NoFilePicker());

        _opened.Add(workspace);
        return workspace;
    }

    private sealed class NoFilePicker : IFilePicker
    {
        public Task<string?> PickFileAsync(string title) => Task.FromResult<string?>(null);
    }
}

/// <summary>
/// The settings page rebuilds itself from a snapshot that arrives on whichever
/// thread finished the work, and for a rescan that is a background one.
/// </summary>
/// <remarks>
/// Everything it rebuilds is bound to a live window, so it has to be put back on
/// the UI thread first. This asserts the thread rather than the outcome: the
/// rows appearing is not evidence, because they appeared before this was fixed
/// too — from the wrong thread.
/// </remarks>
public class SettingsThreadingTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mightdo-settings-thread-" + Guid.NewGuid().ToString("N")[..8]);

    private WorkspaceSession _session = null!;

    public async ValueTask InitializeAsync() =>
        _session = await WorkspaceSession.OpenAsync(
            new TaskStore(new Core.Storage.Workspace(_root)));

    public ValueTask DisposeAsync()
    {
        _session.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    [AvaloniaFact]
    public async Task ABackgroundChangeRebuildsTheRowsOnTheUiThread()
    {
        using var vm = new SettingsViewModel(
            _session, AppSettings.Load(Path.Combine(_root, "settings.json")));

        var offUiThread = false;
        vm.Statuses.CollectionChanged += (_, _) =>
        {
            if (!Dispatcher.UIThread.CheckAccess()) offUiThread = true;
        };

        // Raised from the threadpool, as a rescan started by the watcher is.
        await Task.Run(() => _session.AddStatusAsync("Waiting on someone", StatusType.Active));

        // Let anything the handler posted actually run.
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        Assert.False(offUiThread, "the settings page rebuilt its rows off the UI thread");
        Assert.Contains(vm.Statuses, status => status.Name == "Waiting on someone");
    }
}
