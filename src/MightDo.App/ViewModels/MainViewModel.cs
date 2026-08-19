using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MightDo.Core.Storage;
using MightDo.Platform;

namespace MightDo.App.ViewModels;

/// <summary>Asks the user for a folder. Implemented by the view layer.</summary>
public interface IFolderPicker
{
    Task<string?> PickFolderAsync(string title);
}

/// <summary>
/// The application shell: which workspaces the user has, which one is open, and
/// the switching between them.
/// </summary>
/// <remarks>
/// "Loading" lives here rather than on the session, because a
/// <see cref="Core.Session.WorkspaceSession"/> exists only once its workspace is
/// loaded. Whether one has been chosen at all is a question about the app, not
/// about the workspace.
/// <para>
/// One workspace is open at a time. Each is a folder of its own — work tasks in
/// one, the house in another — and only the open one has a watcher and a
/// reminder scheduler running on it, so switching genuinely closes the one
/// being left rather than keeping both alive.
/// </para>
/// </remarks>
public sealed partial class MainViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly IFolderPicker _picker;
    private readonly IFilePicker _filePicker;
    private readonly WorkspaceServices _services;

    /// <summary>
    /// Whether each remembered folder was there the last time anything looked.
    /// </summary>
    /// <remarks>
    /// Answered from here rather than from the disk, because the question is
    /// asked once per row every time the switcher is rebuilt — which happens
    /// during window open, before the app has painted for the first time. On an
    /// unmounted share or a stalled cloud-sync mount a single
    /// <c>Directory.Exists</c> blocks for seconds, and the UI thread is the one
    /// that was doing the asking.
    /// </remarks>
    private readonly Dictionary<string, bool> _missing = new(StringComparer.Ordinal);

    private readonly PendingWork _pending = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkspace))]
    private WorkspaceViewModel? _workspace;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Whether the switcher is offering to rename the open workspace.</summary>
    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _renameText = "";

    public MainViewModel(
        AppSettings settings,
        IFolderPicker picker,
        IFilePicker filePicker,
        WorkspaceServices? services = null)
    {
        _settings = settings;
        _picker = picker;
        _filePicker = filePicker;

        // The composition root, which is what this type already was for
        // everything else the app is made of.
        _services = services ?? WorkspaceServices.Real;
    }

    /// <summary>A parameterless constructor for the XAML designer.</summary>
    public MainViewModel()
        : this(AppSettings.Load(), new NoPicker(), new NoPicker())
    {
    }

    public bool HasWorkspace => Workspace is not null;

    // ------------------------------------------------------------- the switcher

    /// <summary>
    /// Every workspace the user has added, newest last, for the switcher.
    /// </summary>
    /// <remarks>
    /// Rebuilt outright rather than patched, because every change to it — added,
    /// renamed, forgotten, switched — moves the tick as well as the row, and a
    /// list this short is not worth diffing.
    /// </remarks>
    public ObservableCollection<WorkspaceChoice> Workspaces { get; } = [];

    /// <summary>What the switcher shows when it is closed.</summary>
    public string CurrentWorkspaceName =>
        _settings.CurrentWorkspace?.Name ?? "No workspace";

    public bool HasOtherWorkspaces => Workspaces.Count > 1;

    private void RefreshWorkspaces()
    {
        var current = _settings.CurrentWorkspace?.Path;

        Workspaces.Clear();
        foreach (var workspace in _settings.Workspaces)
        {
            Workspaces.Add(new WorkspaceChoice(
                workspace.Path,
                workspace.Name,
                IsCurrent: workspace.Path == current,
                // Absent from the cache means nobody has looked yet. Assuming
                // present is the kinder guess: a row that flickers from fine to
                // missing is better than one that libels a folder that is there.
                IsMissing: _missing.GetValueOrDefault(workspace.Path)));
        }

        OnPropertyChanged(nameof(CurrentWorkspaceName));
        OnPropertyChanged(nameof(HasOtherWorkspaces));
        OnPropertyChanged(nameof(HasRememberedWorkspaces));

        ProbeInBackground();
    }

    /// <summary>
    /// Asks the filesystem about every remembered folder, off the UI thread, and
    /// marks the rows when the answers come back.
    /// </summary>
    /// <remarks>
    /// The rows are marked in place rather than rebuilt, so the probe cannot
    /// start another probe.
    /// </remarks>
    private void ProbeInBackground()
    {
        var paths = Workspaces.Select(choice => choice.Path).ToList();
        _pending.Add(ProbeAsync(paths));
    }

    /// <summary>The probe in flight, for tests to await.</summary>
    public Task PendingAvailability => _pending.All;

    private async Task ProbeAsync(IReadOnlyList<string> paths)
    {
        try
        {
            var probed = await Task.Run(
                () => paths.Distinct().ToDictionary(
                    path => path, path => WhyUnavailable(path) is not null));

            OnUiThread(() =>
            {
                foreach (var (path, missing) in probed) Mark(path, missing);
            });
        }
        catch (Exception error) when (!IsShutdown(error))
        {
            // A probe that fails is a question that went unanswered, not a
            // workspace that has gone: leave the rows as they are and let the
            // next rebuild ask again. Opening one still checks for itself.
        }
    }

    /// <summary>Whether there is anything to offer someone with nothing open.</summary>
    public bool HasRememberedWorkspaces => Workspaces.Count > 0;

    /// <summary>
    /// Why a remembered workspace cannot be reopened, or null if it can.
    /// </summary>
    /// <remarks>
    /// Reopening is not the same act as choosing a folder, and this is the
    /// difference. Choosing one creates what is missing — that is how a
    /// workspace is made. Reopening one must never create anything: the folder
    /// is expected to be there already, and the ways it can be absent are all
    /// temporary. An unmounted drive comes back; a synced folder arrives before
    /// the files inside it do.
    /// <para>
    /// So a folder that exists but holds no <c>config.json</c> counts as absent
    /// too. Seeding one there would write a second, competing config for a
    /// workspace that is alive on another machine, and leave the user looking
    /// at an empty workspace where their tasks used to be.
    /// </para>
    /// </remarks>
    private string? WhyUnavailable(string path)
    {
        var workspace = new Core.Storage.Workspace(path);

        if (!workspace.Exists)
        {
            return $"Couldn't find “{NameOf(path)}” at {path}. "
                   + "Nothing has been created there. If it is on a drive or in a synced "
                   + "folder, it may come back.";
        }

        if (!workspace.IsInitialised)
        {
            return $"The folder for “{NameOf(path)}” is at {path}, but there is no "
                   + "workspace in it. Nothing has been created there. If it is in a synced "
                   + "folder, its contents may still be on their way down.";
        }

        return null;
    }

    /// <summary>
    /// <see cref="WhyUnavailable"/> without holding up the UI thread.
    /// </summary>
    /// <remarks>
    /// Opening and switching need a fresh answer rather than the cached one —
    /// they are about to create files in that folder if it turns out to be
    /// there — and both already have somewhere to wait.
    /// </remarks>
    private async Task<string?> WhyUnavailableAsync(string path)
    {
        var problem = await Task.Run(() => WhyUnavailable(path));
        Mark(path, missing: problem is not null);
        return problem;
    }

    /// <summary>
    /// Records what was found about a folder, and marks the row for it.
    /// </summary>
    /// <remarks>
    /// The row as well as the cache, and in one place, because otherwise an
    /// answer that arrives after its row was built never reaches it: opening a
    /// remembered workspace that has gone left the switcher showing it as
    /// present, since nothing rebuilt the list between the answer and the
    /// screen.
    /// </remarks>
    private void Mark(string path, bool missing)
    {
        _missing[path] = missing;

        for (var i = 0; i < Workspaces.Count; i++)
        {
            var row = Workspaces[i];
            if (row.Path == path && row.IsMissing != missing)
            {
                Workspaces[i] = row with { IsMissing = missing };
            }
        }
    }

    private string NameOf(string path) =>
        _settings.Workspaces.FirstOrDefault(w => w.Path == path)?.Name
        ?? RememberedWorkspace.NameFor(path);

    /// <summary>Adds a folder as a workspace and opens it.</summary>
    [RelayCommand]
    private async Task AddWorkspaceAsync()
    {
        var chosen = await _picker.PickFolderAsync("Choose a folder for this workspace");
        if (chosen is null) return;

        var added = _settings.AddWorkspace(chosen);
        await OpenAsync(added.Path);
    }

    /// <summary>Opens one of the remembered workspaces.</summary>
    /// <remarks>
    /// Switching to the workspace already open does nothing, rather than
    /// tearing down a live session and rebuilding an identical one — which
    /// would lose the open task and the scroll position for no reason.
    /// <para>
    /// A folder that is not there is reported rather than opened. Opening one
    /// seeds a workspace, so switching to an unmounted drive or a OneDrive
    /// folder that has not synced yet would silently create an empty workspace
    /// over the top of the real one and leave the user looking at no tasks.
    /// </para>
    /// </remarks>
    [RelayCommand]
    private async Task SwitchWorkspaceAsync(WorkspaceChoice? choice)
    {
        if (choice is null || choice.IsCurrent) return;

        IsRenaming = false;

        if (await WhyUnavailableAsync(choice.Path) is { } problem)
        {
            // The one open stays open: it is still working, and closing it
            // would cost the user a workspace as well as the one they wanted.
            RefreshWorkspaces();
            Message = problem;
            return;
        }

        _settings.SetCurrentWorkspace(choice.Path);
        await OpenAsync(choice.Path);
    }

    [RelayCommand]
    private void ClearMessage() => Message = null;

    [RelayCommand]
    private void BeginRename()
    {
        if (_settings.CurrentWorkspace is not { } workspace) return;

        RenameText = workspace.Name;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CommitRename()
    {
        if (_settings.CurrentWorkspace is { } workspace)
        {
            _settings.RenameWorkspace(workspace.Path, RenameText);
            RefreshWorkspaces();
        }

        IsRenaming = false;
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    /// <summary>
    /// Drops the open workspace from the list and closes it. Nothing on disk is
    /// touched — the folder and every task in it stays exactly where it is.
    /// </summary>
    [RelayCommand]
    private void ForgetWorkspace()
    {
        if (_settings.CurrentWorkspace is not { } workspace) return;

        var name = workspace.Name;
        CloseOpenWorkspace();
        _settings.ForgetWorkspace(workspace.Path);
        RefreshWorkspaces();

        Message = $"Forgot “{name}”. Its folder and tasks are untouched.";
    }

    /// <summary>The size the window was last left at, if there is one.</summary>
    public WindowPlacement? WindowPlacement => _settings.WindowPlacement;

    /// <summary>Records the size to reopen at. Called by the window as it closes.</summary>
    public void RememberWindow(WindowPlacement placement) =>
        _settings.SetWindowPlacement(placement);

    /// <summary>
    /// Reopens the remembered workspace, if it is still there.
    /// </summary>
    public async Task InitialiseAsync()
    {
        try
        {
            RefreshWorkspaces();

            var remembered = _settings.RememberedWorkspacePath;
            if (remembered is null) return;

            if (await WhyUnavailableAsync(remembered) is { } problem)
            {
                // Remembered but not there: say so rather than silently starting
                // over on top of it. The switcher is still on the no-workspace
                // screen, so another workspace is one click away.
                Message = problem;
                return;
            }

            await OpenAsync(remembered);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Startup is driven from the window's Opened event, an async void:
            // anything thrown here goes nowhere except the process's unhandled
            // exception. The app comes up with no workspace and an explanation
            // instead, which is a state it already knows how to be in.
            Message = $"Couldn't start up: {e.Message}";
        }
    }

    /// <summary>Adds the first workspace, from the screen shown when none is open.</summary>
    [RelayCommand]
    public Task ChooseWorkspaceAsync() => AddWorkspaceAsync();

    /// <summary>
    /// Closes the open workspace without forgetting it, so it is still in the
    /// switcher to come back to.
    /// </summary>
    [RelayCommand]
    public void CloseWorkspace()
    {
        CloseOpenWorkspace();
        _settings.CloseWorkspace();
        RefreshWorkspaces();
        Message = null;
    }

    /// <summary>
    /// Shuts down the open workspace, flushing how it was left before its
    /// watcher and reminder scheduler go.
    /// </summary>
    private void CloseOpenWorkspace()
    {
        Workspace?.Dispose();
        Workspace = null;
    }

    public async Task OpenAsync(string path)
    {
        IsBusy = true;
        IsRenaming = false;
        try
        {
            // The workspace being left is disposed before the next is opened, so
            // its view state is written and its watcher stopped before another
            // starts. Two live sessions on two folders is a state nothing else
            // in the app is written to expect.
            CloseOpenWorkspace();

            var store = new TaskStore(new Core.Storage.Workspace(path));
            Workspace = await WorkspaceViewModel.OpenAsync(
                store, _settings, _filePicker, _services);
            Message = null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Every way a workspace can refuse to open ends here, as a message.
            // A newer config.json is refused on purpose — it defines the
            // statuses every task refers to, and saving it back from here would
            // strip whatever that version added — and an unreadable one is
            // refused for the same reason. Anything else is a surprise, and a
            // surprise that escapes is a surprise that closes the application:
            // this runs under the window's Opened event, which is an
            // async void. The folder is left untouched either way.
            Message = $"Couldn't open {path}: {e.Message}";
        }
        finally
        {
            RefreshWorkspaces();
            IsBusy = false;
        }
    }

    /// <summary>Picks nothing. For the XAML designer, which has no window to ask.</summary>
    private sealed class NoPicker : IFolderPicker, IFilePicker
    {
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title) => Task.FromResult<string?>(null);
    }
}

/// <summary>One row of the workspace switcher.</summary>
/// <remarks>
/// A missing folder is shown rather than hidden. An unmounted drive or a
/// OneDrive folder that has not synced yet comes back, and a workspace that
/// silently vanished from the list would look like the app had lost it.
/// <para>
/// Top-level rather than nested inside the view model, so XAML can name it in
/// an <c>x:DataType</c> without reaching through a containing type.
/// </para>
/// </remarks>
public sealed record WorkspaceChoice(string Path, string Name, bool IsCurrent, bool IsMissing);
