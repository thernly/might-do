using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MightDo.App.ViewModels;
using MightDo.App.Views;
using MightDo.Core.Query;
using MightDo.Core.Storage;
using MightDo.Platform;

namespace MightDo.App.Tests;

/// <summary>
/// Several workspaces, and moving between them.
/// </summary>
/// <remarks>
/// The point of the feature is that the workspaces are separate: what is in one
/// is not in the other, and how you left one is how you find it. Both halves are
/// asserted here, because a switcher that opened the right folder but carried
/// the previous workspace's filters across would look like it had lost tasks.
/// </remarks>
public class WorkspaceSwitchingTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "mightdo-switching-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private AppSettings Settings() => AppSettings.Load(Path.Combine(_root, "settings.json"));

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private MainViewModel Shell(AppSettings settings, params string[] folders)
    {
        var main = new MainViewModel(settings, new QueuedFolderPicker(folders), new NoPicker());
        _disposables.Add(new Closer(main));
        return main;
    }

    /// <summary>Adds a workspace through the picker, as the switcher does.</summary>
    private static async Task<string> AddAsync(MainViewModel main)
    {
        await main.AddWorkspaceCommand.ExecuteAsync(null);
        return main.Workspace!.Root;
    }

    /// <summary>
    /// Creates the workspace in a folder, which is what choosing that folder in
    /// the app does. Needed wherever a test puts a workspace straight into
    /// settings: reopening a remembered workspace deliberately creates nothing,
    /// so one that was never made cannot be opened.
    /// </summary>
    private static async Task SeedAsync(string path) =>
        await new TaskStore(new Core.Storage.Workspace(path)).LoadAsync(
            TestContext.Current.CancellationToken);

    /// <summary>Adds a task the way the toolbar does.</summary>
    private static async Task CreateTaskAsync(WorkspaceViewModel workspace, string summary)
    {
        workspace.NewTaskSummary = summary;
        await workspace.CreateTaskCommand.ExecuteAsync(null!);
    }

    private static WorkspaceChoice Choice(MainViewModel main, string name) =>
        main.Workspaces.First(w => w.Name == name);

    // ---- the list -----------------------------------------------------------

    [AvaloniaFact]
    public async Task AddingAWorkspaceOpensItAndNamesItAfterItsFolder()
    {
        var main = Shell(Settings(), Folder("work"));

        await AddAsync(main);

        Assert.True(main.HasWorkspace);
        Assert.Equal("work", main.CurrentWorkspaceName);
        Assert.Equal(["work"], main.Workspaces.Select(w => w.Name));
        Assert.True(main.Workspaces[0].IsCurrent);
    }

    [AvaloniaFact]
    public async Task SwitchingShowsTheOtherWorkspacesTasksAndOnlyThose()
    {
        var main = Shell(Settings(), Folder("work"), Folder("home"));

        await AddAsync(main);
        await CreateTaskAsync(main.Workspace!, "File the VAT return");

        await AddAsync(main);
        await CreateTaskAsync(main.Workspace!, "Book the boiler service");

        Assert.Equal(
            ["Book the boiler service"], main.Workspace!.Tasks.Select(t => t.Summary));

        await main.SwitchWorkspaceCommand.ExecuteAsync(Choice(main, "work"));

        Assert.Equal("work", main.CurrentWorkspaceName);
        Assert.Equal(["File the VAT return"], main.Workspace!.Tasks.Select(t => t.Summary));
    }

    [AvaloniaFact]
    public async Task SwitchingToTheOpenWorkspaceIsANoOp()
    {
        // Tearing down a live session to rebuild an identical one would close
        // the open task for nothing.
        var main = Shell(Settings(), Folder("work"));
        await AddAsync(main);

        var open = main.Workspace;
        await main.SwitchWorkspaceCommand.ExecuteAsync(Choice(main, "work"));

        Assert.Same(open, main.Workspace);
    }

    [AvaloniaFact]
    public async Task ForgettingAWorkspaceLeavesItsFolderAndTasksAlone()
    {
        var main = Shell(Settings(), Folder("work"));
        await AddAsync(main);
        var root = main.Workspace!.Root;
        await CreateTaskAsync(main.Workspace, "Still here afterwards");

        main.ForgetWorkspaceCommand.Execute(null);

        Assert.False(main.HasWorkspace);
        Assert.Empty(main.Workspaces);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(root, "tasks"), "*.json"));
    }

    [AvaloniaFact]
    public async Task ClosingAWorkspaceKeepsItInTheSwitcherToComeBackTo()
    {
        var main = Shell(Settings(), Folder("work"));
        await AddAsync(main);

        main.CloseWorkspaceCommand.Execute(null);

        Assert.False(main.HasWorkspace);
        Assert.True(main.HasRememberedWorkspaces);
        Assert.Equal(["work"], main.Workspaces.Select(w => w.Name));
        Assert.Equal("No workspace", main.CurrentWorkspaceName);
    }

    [AvaloniaFact]
    public async Task RenamingChangesWhatTheSwitcherShowsAndNothingOnDisk()
    {
        var main = Shell(Settings(), Folder("work"));
        await AddAsync(main);
        var root = main.Workspace!.Root;

        main.BeginRenameCommand.Execute(null);
        Assert.True(main.IsRenaming);
        Assert.Equal("work", main.RenameText);

        main.RenameText = "The day job";
        main.CommitRenameCommand.Execute(null);

        Assert.False(main.IsRenaming);
        Assert.Equal("The day job", main.CurrentWorkspaceName);
        Assert.True(Directory.Exists(root));
    }

    [AvaloniaFact]
    public async Task AWorkspaceWhoseFolderHasGoneIsReportedAndNotRecreated()
    {
        // Reopening is not the same act as choosing a folder: choosing creates,
        // reopening must not. Recreating it would show an empty workspace where
        // the user's tasks are supposed to be.
        var main = Shell(Settings(), Folder("work"), Folder("home"));
        await AddAsync(main);
        var work = main.Workspace!.Root;
        await AddAsync(main);

        Directory.Delete(work, recursive: true);
        await main.SwitchWorkspaceCommand.ExecuteAsync(Choice(main, "work"));

        Assert.False(Directory.Exists(work));
        Assert.Equal("home", main.CurrentWorkspaceName);
        Assert.True(main.Workspaces.First(w => w.Name == "work").IsMissing);
        Assert.Contains("Couldn't find", main.Message);
        Assert.Contains("Nothing has been created", main.Message);
    }

    [AvaloniaFact]
    public async Task AFolderThatIsThereButHoldsNoWorkspaceIsNotSeededEither()
    {
        // A synced folder often arrives before the files inside it do. Seeding
        // a config there would write a second, competing one for a workspace
        // that is alive on another machine.
        var main = Shell(Settings(), Folder("work"), Folder("home"));
        await AddAsync(main);
        var work = main.Workspace!.Root;
        await AddAsync(main);

        File.Delete(Path.Combine(work, "config.json"));
        await main.SwitchWorkspaceCommand.ExecuteAsync(Choice(main, "work"));

        Assert.False(File.Exists(Path.Combine(work, "config.json")));
        Assert.Equal("home", main.CurrentWorkspaceName);
        Assert.True(main.Workspaces.First(w => w.Name == "work").IsMissing);
        Assert.Contains("no workspace in it", main.Message);
    }

    [AvaloniaFact]
    public async Task AMissingWorkspaceAtStartupLeavesTheOthersOneClickAway()
    {
        // What the user is left with: an explanation, and a way on.
        var settings = Settings();
        var main = Shell(settings, Folder("work"), Folder("home"));
        await AddAsync(main);
        var work = main.Workspace!.Root;
        await AddAsync(main);
        main.Workspace!.Dispose();

        // "work" is what a restart would reopen.
        settings.SetCurrentWorkspace(work);
        Directory.Delete(work, recursive: true);

        var restarted = Shell(AppSettings.Load(Path.Combine(_root, "settings.json")));
        await restarted.InitialiseAsync();

        Assert.False(restarted.HasWorkspace);
        Assert.False(Directory.Exists(work));
        Assert.Contains("Couldn't find", restarted.Message);
        Assert.True(restarted.HasRememberedWorkspaces);

        await restarted.SwitchWorkspaceCommand.ExecuteAsync(Choice(restarted, "home"));

        Assert.True(restarted.HasWorkspace);
        Assert.Equal("home", restarted.CurrentWorkspaceName);
    }

    [AvaloniaFact]
    public async Task WithNoOtherWorkspaceTheUserIsLeftAtThePicker()
    {
        var settings = Settings();
        var main = Shell(settings, Folder("work"));
        await AddAsync(main);
        var work = main.Workspace!.Root;
        main.Workspace.Dispose();
        Directory.Delete(work, recursive: true);

        var restarted = Shell(AppSettings.Load(Path.Combine(_root, "settings.json")));
        await restarted.InitialiseAsync();

        Assert.False(restarted.HasWorkspace);
        Assert.NotNull(restarted.Message);

        // The workspace is still listed — it may come back — and choosing a
        // folder is the way on.
        Assert.Equal(["work"], restarted.Workspaces.Select(w => w.Name));
        Assert.True(restarted.Workspaces[0].IsMissing);
    }

    [AvaloniaFact]
    public async Task ChoosingAFolderStillCreatesTheWorkspaceInIt()
    {
        // The other half of the rule: picking a folder is what makes one.
        var main = Shell(Settings(), Folder("work"));

        await AddAsync(main);

        Assert.True(File.Exists(Path.Combine(main.Workspace!.Root, "config.json")));
    }

    // ---- per-workspace view state -------------------------------------------

    [AvaloniaFact]
    public async Task EachWorkspaceComesBackInTheViewItWasLeftIn()
    {
        var main = Shell(Settings(), Folder("work"), Folder("home"));

        await AddAsync(main);
        main.Workspace!.ShowBoardCommand.Execute(null);
        main.Workspace.Sort = TaskSort.DueDate;
        main.Workspace.Search = "invoice";
        main.Workspace.OverdueOnly = true;

        await AddAsync(main);
        Assert.True(main.Workspace!.IsListView);
        Assert.Equal("", main.Workspace.Search);
        Assert.False(main.Workspace.OverdueOnly);

        await main.SwitchWorkspaceCommand.ExecuteAsync(Choice(main, "work"));

        Assert.True(main.Workspace!.IsBoardView);
        Assert.Equal(TaskSort.DueDate, main.Workspace.Sort);
        Assert.Equal("invoice", main.Workspace.Search);
        Assert.True(main.Workspace.OverdueOnly);
    }

    [AvaloniaFact]
    public async Task TheFiltersAWorkspaceWasLeftWithAreSelectedAgain()
    {
        var settings = Settings();
        var main = Shell(settings, Folder("work"));
        await AddAsync(main);
        var workspace = main.Workspace!;

        var settingsView = workspace.CreateSettingsViewModel();
        settingsView.NewTagName = "finance";
        await settingsView.AddTagCommand.ExecuteAsync(null!);

        var tag = workspace.TagFilters.First(t => t.Name == "finance");
        tag.IsSelected = true;
        var priority = workspace.Priorities.First(p => p.Name == "High");
        priority.IsSelected = true;
        workspace.FlushViewState();

        // Reopening from the same settings is what a restart does.
        var reopened = Shell(AppSettings.Load(Path.Combine(_root, "settings.json")));
        await reopened.InitialiseAsync();

        var restored = reopened.Workspace!;
        Assert.True(restored.TagFilters.First(t => t.Name == "finance").IsSelected);
        Assert.True(restored.Priorities.First(p => p.Name == "High").IsSelected);
        Assert.True(restored.FiltersOpen);
    }

    [AvaloniaFact]
    public async Task AFilterNamingSomethingSinceDeletedIsSimplyDropped()
    {
        // A tag deleted on another machine, or a sort this build no longer has:
        // the workspace still opens, just without that filter.
        var settings = Settings();
        var work = Folder("work");
        await SeedAsync(work);
        settings.AddWorkspace(work);
        settings.SaveViewState(work, new WorkspaceViewState
        {
            Sort = "AlphabeticalByVibes",
            TagIds = ["01no-such-tag"],
            Priorities = ["High"],
        });

        var main = Shell(settings);
        await main.InitialiseAsync();

        var workspace = main.Workspace!;
        Assert.Equal(TaskSort.Smart, workspace.Sort);
        Assert.True(workspace.Priorities.First(p => p.Name == "High").IsSelected);
        Assert.DoesNotContain(workspace.TagFilters, t => t.IsSelected);
    }

    [AvaloniaFact]
    public async Task ClearingTheFiltersDoesNotBringTheRestoredOnesBack()
    {
        var settings = Settings();
        var work = Folder("work");
        await SeedAsync(work);
        settings.AddWorkspace(work);
        settings.SaveViewState(work, new WorkspaceViewState
        {
            Search = "invoice",
            Priorities = ["High"],
        });

        var main = Shell(settings);
        await main.InitialiseAsync();
        var workspace = main.Workspace!;

        workspace.ClearFiltersCommand.Execute(null);

        Assert.Equal("", workspace.Search);
        Assert.DoesNotContain(workspace.Priorities, p => p.IsSelected);

        // A rescan rebuilds every toggle, which is where a still-pending
        // restored id would reappear.
        await workspace.RefreshCommand.ExecuteAsync(null!);
        Assert.DoesNotContain(workspace.Priorities, p => p.IsSelected);
    }

    [AvaloniaFact]
    public async Task AnUpgradeFindsTheWorkspaceTheOldSettingsFileNamed()
    {
        var work = Folder("work");
        await SeedAsync(work);

        var path = Path.Combine(_root, "settings.json");
        await File.WriteAllTextAsync(
            path,
            $$"""{ "workspacePath": {{System.Text.Json.JsonSerializer.Serialize(work)}} }""",
            TestContext.Current.CancellationToken);

        var main = Shell(AppSettings.Load(path));
        await main.InitialiseAsync();

        Assert.True(main.HasWorkspace);
        Assert.Equal("work", main.CurrentWorkspaceName);
    }

    // ---- the switcher in the window ----------------------------------------

    [AvaloniaFact]
    public async Task TheToolbarShowsWhichWorkspaceIsOpen()
    {
        // The switcher is bound to the shell from inside the part of the window
        // bound to the open workspace, which is exactly the kind of binding
        // that resolves against the wrong object and fails only when shown.
        var main = Shell(Settings(), Folder("work"), Folder("home"));
        var window = new MainWindow { DataContext = main };
        window.Show();

        await AddAsync(main);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(TextsIn(window), text => text == "work");

        await AddAsync(main);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(TextsIn(window), text => text == "home");
        Assert.DoesNotContain(TextsIn(window), text => text == "work");
    }

    [AvaloniaFact]
    public async Task ChoosingAWorkspaceSwitchesToItAndClosesTheSwitcher()
    {
        // Both halves in one test, and clicked rather than commanded, because
        // the way this breaks is that closing the menu stops the click from
        // ever reaching the command.
        var main = Shell(Settings(), Folder("work"), Folder("home"));
        var window = Showing(main);

        await AddAsync(main);
        await AddAsync(main);
        Settle(window);

        var switcher = Switcher(window);
        Click(window, switcher);

        var flyout = (Flyout)switcher.Flyout!;
        Assert.True(flyout.IsOpen);

        Click(window, RowFor(flyout, "work"));
        await main.SwitchWorkspaceCommand.ExecutionTask!;
        Settle(window);

        Assert.Equal("work", main.CurrentWorkspaceName);
        Assert.False(flyout.IsOpen);
    }

    [AvaloniaFact]
    public async Task AddingAWorkspaceFromTheSwitcherStillWorks()
    {
        var main = Shell(Settings(), Folder("work"), Folder("home"));
        var window = Showing(main);

        await AddAsync(main);
        Settle(window);

        var switcher = Switcher(window);
        Click(window, switcher);
        Click(window, ButtonLabelled(window, "Add workspace…"));
        await main.AddWorkspaceCommand.ExecutionTask!;
        Settle(window);

        Assert.Equal("home", main.CurrentWorkspaceName);
        Assert.False(((Flyout)switcher.Flyout!).IsOpen);
    }

    [AvaloniaFact]
    public async Task ForgettingAWorkspaceFromTheSwitcherStillWorks()
    {
        var main = Shell(Settings(), Folder("work"));
        var window = Showing(main);

        await AddAsync(main);
        Settle(window);

        var switcher = Switcher(window);
        Click(window, switcher);
        Click(window, ButtonLabelled(window, "Forget this one"));
        Settle(window);

        Assert.Empty(main.Workspaces);
        Assert.False(main.HasWorkspace);
        Assert.False(((Flyout)switcher.Flyout!).IsOpen);
    }

    // ---- what a workspace owns ---------------------------------------------

    /// <summary>
    /// The Settings window is a view onto one workspace's session. Left open
    /// across a switch it would show the workspace the user has left and post
    /// its edits into a disposed session, which is swallowed as shutdown — the
    /// user would see their edits do nothing at all.
    /// </summary>
    [AvaloniaFact]
    public async Task SwitchingWorkspaceClosesSettingsAndTheNextOneOpensOnTheNewWorkspace()
    {
        var main = Shell(Settings(), Folder("work"), Folder("home"));
        var window = Showing(main);

        await AddAsync(main);
        Settle(window);

        Click(window, SettingsButton(window));
        var settings = (SettingsViewModel)Assert.Single(window.OwnedWindows).DataContext!;

        // Something only this workspace has, so the page that comes back after
        // the switch can be shown to be looking somewhere else.
        settings.NewStatusName = "Waiting on the plumber";
        await settings.AddStatusCommand.ExecuteAsync(null!);
        Settle(window);
        Assert.Contains(settings.Statuses, status => status.Name == "Waiting on the plumber");

        await AddAsync(main);
        Settle(window);

        Assert.True(settings.IsDisposed);
        Assert.Empty(window.OwnedWindows);

        Click(window, SettingsButton(window));
        var reopened = (SettingsViewModel)Assert.Single(window.OwnedWindows).DataContext!;

        Assert.NotSame(settings, reopened);
        Assert.DoesNotContain(reopened.Statuses, status => status.Name == "Waiting on the plumber");
    }

    /// <summary>
    /// Startup opens the remembered workspace from the window's Opened event
    /// while Add and Switch stay pressable, so two opens can be in flight at
    /// once. Both used to be built and both assigned, and the one that lost kept
    /// its watcher, reminder clock and session running on a folder nothing was
    /// showing.
    /// </summary>
    [AvaloniaFact]
    public async Task OverlappingOpensLeaveOneLiveWorkspaceAndCloseTheOther()
    {
        var settings = Settings();
        var opened = new List<WorkspaceViewModel>();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var main = new MainViewModel(settings, new NoPicker(), new NoPicker(), open: async store =>
        {
            // Held open so the second request arrives while the first is still
            // building, which is the overlap the real thing is too quick to
            // reproduce.
            await held.Task;

            var workspace = await WorkspaceViewModel.OpenAsync(store, settings, new NoPicker());
            opened.Add(workspace);
            return workspace;
        });
        _disposables.Add(new Closer(main));

        var work = Folder("work");
        var home = Folder("home");

        var first = main.OpenAsync(work);
        var second = main.OpenAsync(home);

        Assert.False(main.CanOpenWorkspace);

        held.SetResult();
        await Task.WhenAll(first, second);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, opened.Count);
        Assert.Same(opened[1], main.Workspace);
        Assert.Equal(home, main.Workspace!.Root);
        Assert.True(opened[0].IsDisposed);
        Assert.False(opened[1].IsDisposed);
        Assert.True(main.CanOpenWorkspace);
    }

    /// <summary>The Settings button, which is labelled rather than named.</summary>
    private static Button SettingsButton(Window window) =>
        window.GetVisualDescendants().OfType<Button>()
            .First(button => button.GetVisualDescendants().OfType<TextBlock>()
                .Any(text => text.Text == "Settings"));

    /// <summary>Shows the window and keeps it laid out, so clicks land.</summary>
    private MainWindow Showing(MainViewModel main)
    {
        var window = new MainWindow { DataContext = main };
        window.Show();
        return window;
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.Measure(window.ClientSize);
        window.Arrange(new Rect(window.ClientSize));
        Dispatcher.UIThread.RunJobs();
    }

    private static Button Switcher(Window window) =>
        window.GetVisualDescendants().OfType<Button>()
            .First(button => button.Name == "WorkspaceSwitcher");

    private static Button RowFor(Flyout flyout, string name) =>
        ((Control)flyout.Content!).GetVisualDescendants().OfType<Button>()
            .First(button => button.DataContext is WorkspaceChoice choice && choice.Name == name);

    private static Button ButtonLabelled(Window window, string content) =>
        window.GetVisualDescendants().OfType<Button>()
            .First(button => button.Content as string == content);

    private static void Click(Window window, Visual control)
    {
        Settle(window);

        var point = control.TranslatePoint(
            new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("the control is not in the window");

        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }

    private static List<string> TextsIn(Visual root) =>
        [.. root.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible)
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)];

    /// <summary>Hands out the folders a test lined up, in order.</summary>
    private sealed class QueuedFolderPicker(params string[] folders) : IFolderPicker
    {
        private readonly Queue<string> _folders = new(folders);

        public Task<string?> PickFolderAsync(string title) =>
            Task.FromResult(_folders.Count > 0 ? _folders.Dequeue() : null);
    }

    private sealed class NoPicker : IFolderPicker, IFilePicker
    {
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title) => Task.FromResult<string?>(null);
    }

    /// <summary>Closes whatever a shell has open when the test ends.</summary>
    private sealed class Closer(MainViewModel main) : IDisposable
    {
        public void Dispose() => main.Workspace?.Dispose();
    }
}
