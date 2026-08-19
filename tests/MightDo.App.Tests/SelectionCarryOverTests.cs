using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MightDo.App.ViewModels;
using MightDo.App.Views;
using MightDo.Core.Domain;
using MightDo.Core.Storage;
using MightDo.Platform;

namespace MightDo.App.Tests;

/// <summary>
/// Opening one task after another, through the real pane in a real window.
/// </summary>
/// <remarks>
/// Regression tests for a pane that wrote the previously open task's dropdown
/// selections onto the task being opened. The view model's own tests cannot see
/// it: it comes from the controls, which re-read their bindings when the pane is
/// pointed at a different view model and push what they were already showing
/// back into it.
/// <para>
/// The assertions compare the whole task, not one field. Anything the act of
/// opening a task writes to it is the bug, whichever field it lands in.
/// </para>
/// </remarks>
public class SelectionCarryOverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mightdo-carryover-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly List<IDisposable> _disposables = [];
    private TaskStore _store = null!;

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact]
    public async Task OpeningATaskLeavesItExactlyAsItWas()
    {
        var (window, workspace) = await OpenAsync();
        var second = IdOf(workspace, "Second");

        await EditEveryFieldOfAsync(window, workspace, IdOf(workspace, "First"));
        var before = await OnDiskAsync(second);

        workspace.SelectTaskById(second);
        await SettleAsync(workspace);

        Assert.True((await OnDiskAsync(second)).HasSameContentAs(before));
    }

    [AvaloniaFact]
    public async Task ClosingTheOpenTaskFirstDoesNotCarryAnythingAcrossEither()
    {
        // The pane is torn down and rebuilt on this path rather than re-pointed,
        // so the controls start over — but only if nothing survives the gap.
        var (window, workspace) = await OpenAsync();
        var second = IdOf(workspace, "Second");

        await EditEveryFieldOfAsync(window, workspace, IdOf(workspace, "First"));
        var before = await OnDiskAsync(second);

        workspace.SelectTaskById(null);
        Dispatcher.UIThread.RunJobs();
        workspace.SelectTaskById(second);
        await SettleAsync(workspace);

        Assert.True((await OnDiskAsync(second)).HasSameContentAs(before));
    }

    [AvaloniaFact]
    public async Task TheTaskBeingLeftKeepsWhatWasJustTypedIntoIt()
    {
        var (window, workspace) = await OpenAsync();
        var first = IdOf(workspace, "First");

        await EditEveryFieldOfAsync(window, workspace, first);
        var before = await OnDiskAsync(first);

        workspace.SelectTaskById(IdOf(workspace, "Second"));
        await SettleAsync(workspace);

        Assert.True((await OnDiskAsync(first)).HasSameContentAs(before));
    }

    [AvaloniaFact]
    public async Task TheOpenedTaskIsWhatThePaneShows()
    {
        var (window, workspace) = await OpenAsync();
        await EditEveryFieldOfAsync(window, workspace, IdOf(workspace, "First"));

        var second = IdOf(workspace, "Second");
        workspace.SelectTaskById(second);
        await SettleAsync(workspace);

        var task = await OnDiskAsync(second);
        var detail = workspace.Detail!;

        Assert.Equal(second, detail.TaskId);
        Assert.Equal("Second", detail.Summary);
        Assert.Equal(task.StatusId, detail.SelectedStatus?.Id);
        Assert.Equal("No category", detail.SelectedCategory?.Name);
        Assert.Equal(Priority.Medium, detail.SelectedPriority);
        Assert.Null(detail.DueDate);
        Assert.Equal("", detail.EstimateMinutes);
        Assert.Equal("", detail.TagNames);

        // Drafts belong to the task they were typed against.
        Assert.Equal("", detail.NewStepText);
        Assert.Equal("", detail.NewNoteBody);
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Puts a distinct value in every field of the pane, so anything that
    /// carries across has something to carry.
    /// </summary>
    private async Task EditEveryFieldOfAsync(
        Window window, WorkspaceViewModel workspace, string taskId)
    {
        workspace.SelectTaskById(taskId);
        Dispatcher.UIThread.RunJobs();

        // Through the controls, which is where the carry-over comes from.
        foreach (var name in new[] { "StatusBox", "CategoryBox" })
        {
            var box = Box(window, name);
            box.SelectedIndex = box.SelectedIndex == 0 ? 1 : 0;
            Dispatcher.UIThread.RunJobs();
        }

        var detail = workspace.Detail!;
        detail.Description = "First's description";
        detail.SelectedPriority = Priority.High;
        detail.DueDate = new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Unspecified);
        detail.EstimateMinutes = "90";
        detail.TotalTimeMinutes = "45";
        detail.TagNames = "urgent";
        await detail.CommitTagsCommand.ExecuteAsync(null!);

        detail.NewStepText = "half-typed step";
        detail.NewNoteBody = "half-typed note";

        await SettleAsync(workspace);
    }

    private async Task<MightDoTask> OnDiskAsync(string taskId) =>
        (await _store.LoadAsync()).Tasks.First(task => task.Id == taskId);

    private static string IdOf(WorkspaceViewModel workspace, string summary) =>
        workspace.Tasks.First(row => row.Summary == summary).Id;

    private static ComboBox Box(Window window, string name) =>
        window.GetVisualDescendants().OfType<ComboBox>()
            .Where(box => box.IsEffectivelyVisible)
            .First(box => box.Name == name);

    /// <summary>Lets the pending write land and the rescan it causes run.</summary>
    private static async Task SettleAsync(WorkspaceViewModel workspace)
    {
        if (workspace.Detail is { } detail) await detail.PendingSave;
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
    }

    private async Task<(Window Window, WorkspaceViewModel Workspace)> OpenAsync()
    {
        var settings = AppSettings.Load(Path.Combine(_root, "settings.json"));
        _store = new TaskStore(new Core.Storage.Workspace(Path.Combine(_root, "ws")));

        var workspace = await WorkspaceViewModel.OpenAsync(_store, settings, new NoPicker());
        _disposables.Add(workspace);

        var window = new MainWindow
        {
            DataContext = new MainViewModel(settings, new NoPicker(), new NoPicker())
            {
                Workspace = workspace,
            },
        };
        window.Show();

        // The default config ships no categories, and a dropdown holding only
        // "No category" cannot be used to pick one.
        var settingsVm = workspace.CreateSettingsViewModel();
        foreach (var name in new[] { "Work", "Home" })
        {
            settingsVm.NewCategoryName = name;
            await settingsVm.AddCategoryCommand.ExecuteAsync(null!);
            Dispatcher.UIThread.RunJobs();
        }

        foreach (var summary in new[] { "First", "Second" })
        {
            workspace.NewTaskSummary = summary;
            await workspace.CreateTaskCommand.ExecuteAsync(null!);
            Dispatcher.UIThread.RunJobs();
        }

        window.Measure(window.ClientSize);
        window.Arrange(new Rect(window.ClientSize));

        return (window, workspace);
    }

    private sealed class NoPicker : IFolderPicker, IFilePicker
    {
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title) => Task.FromResult<string?>(null);
    }
}
