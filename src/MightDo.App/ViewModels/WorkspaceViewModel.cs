using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MightDo.Core.Domain;
using MightDo.Core.Query;
using MightDo.Core.Reminders;
using MightDo.Core.Session;
using MightDo.Core.Storage;
using MightDo.Platform;

namespace MightDo.App.ViewModels;

/// <summary>
/// An open workspace, projected for the list view.
/// </summary>
/// <remarks>
/// Holds the query — which is view state, not workspace state — and re-projects
/// whenever either the query or the underlying snapshot changes.
/// </remarks>
public sealed partial class WorkspaceViewModel : ViewModelBase, IDisposable
{
    private readonly WorkspaceSession _session;
    private readonly WorkspaceWatcher _watcher;
    private readonly ReminderScheduler _reminders;
    private readonly AppSettings _settings;
    private readonly IFilePicker _filePicker;
    private readonly IFileSaver? _fileSaver;

    /// <summary>
    /// Filter selections read from settings that no toggle exists for yet.
    /// </summary>
    /// <remarks>
    /// The toggles are built from the workspace's config, which is not loaded
    /// when the restored state arrives, so the ids wait here and are claimed by
    /// the first projection. Ids naming something since deleted — a tag removed
    /// on another machine — are simply never claimed.
    /// </remarks>
    private readonly HashSet<string> _restoredFilterIds = [];
    private readonly Timer _saveViewState;
    private string? _selectedTaskId;
    private bool _projecting;
    private bool _restoring;
    private WorkspaceViewState? _pendingViewState;

    /// <summary>The banner text this view model put up for a background failure.</summary>
    /// <remarks>
    /// Kept so a later success can take down its own message without also
    /// clearing one somebody else raised — a dialog's, say, or one the user has
    /// not read yet.
    /// <para>
    /// The missing-folder message is one of these. A reload cannot succeed
    /// while the folder is missing — the store refuses to read or write a
    /// workspace that is not there — so the rescan that clears it is the one
    /// the watcher asks for when the folder comes back, which is exactly when
    /// the message stops being true.
    /// </para>
    /// </remarks>
    private string? _backgroundBanner;

    private bool _disposed;

    [ObservableProperty]
    private string _search = "";

    [ObservableProperty]
    private TaskSort _sort = TaskSort.Smart;

    [ObservableProperty]
    private bool _includeCompleted;

    [ObservableProperty]
    private bool _overdueOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PanelFilterCount))]
    [NotifyPropertyChangedFor(nameof(HasPanelFilters))]
    private bool _filtersOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionHiddenFromList))]
    private TaskRowViewModel? _selectedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectionHiddenFromList))]
    private TaskDetailViewModel? _detail;

    [ObservableProperty]
    private string _newTaskSummary = "";

    [ObservableProperty]
    private string? _banner;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListView))]
    [NotifyPropertyChangedFor(nameof(IsBoardView))]
    [NotifyPropertyChangedFor(nameof(SelectionHiddenFromList))]
    private ViewMode _viewMode = ViewMode.List;

    private WorkspaceViewModel(
        WorkspaceSession session,
        AppSettings settings,
        IFilePicker filePicker,
        IFileSaver? fileSaver,
        string root,
        WorkspaceServices services)
    {
        _session = session;
        _settings = settings;
        _filePicker = filePicker;
        _fileSaver = fileSaver;
        Root = root;

        _session.Changed += OnWorkspaceChanged;

        // ADR-0003: the watcher only ever asks for a rescan. It holds no session
        // and cannot write, and RefreshAsync is the same path the manual refresh
        // button uses.
        _watcher = services.Watcher(session);
        _watcher.RescanRequested += (_, _) => RefreshInBackground();

        // Nothing is written to the folder while it is away: the store refuses
        // every save rather than building a fresh workspace at the old path.
        _watcher.RootVanished += (_, _) => OnUiThread(() =>
        {
            _backgroundBanner = "This workspace folder is no longer there. "
                                + "If it is on a drive or a synced folder, it may come back.";
            Banner = _backgroundBanner;
        });
        _watcher.Start();

        _reminders = services.Reminders(session);
        _reminders.Fired += (_, _) => OnUiThread(Project);
        _reminders.Failed += (_, error) => Report(error, "Reminders could not be updated");
        _reminders.Start(services.ReminderInterval);

        // Written on a threadpool tick from state captured on the UI thread, so
        // typing in the search box does not put a file write behind every
        // keystroke. A timer callback has no caller, so it must not be able to
        // throw: losing a scroll position cannot be allowed to end the process.
        _saveViewState = new Timer(
            _ => SafelyFlushViewState(), null, Timeout.Infinite, Timeout.Infinite);

        Restore(settings.ViewStateFor(root));
        Project();
    }

    /// <param name="services">
    /// What to build around the session, defaulting to the real watcher and the
    /// real reminder clock. Passing fakes is how the integration between a
    /// rescan, a reminder tick and this projection is testable at all — every
    /// one of those is driven by a clock, and with the real ones the only way to
    /// wait for a tick is to sleep and hope.
    /// </param>
    public static async Task<WorkspaceViewModel> OpenAsync(
        TaskStore store,
        AppSettings settings,
        IFilePicker filePicker,
        WorkspaceServices? services = null,
        IFileSaver? fileSaver = null)
    {
        var session = await WorkspaceSession.OpenAsync(store);
        try
        {
            return new WorkspaceViewModel(
                session,
                settings,
                filePicker,
                fileSaver,
                store.Workspace.Root,
                services ?? WorkspaceServices.Real);
        }
        catch
        {
            // Nothing else holds the session yet, so a view model that fails to
            // come up would leave a watcher and a reminder clock running on a
            // workspace with no window.
            session.Dispose();
            throw;
        }
    }

    public string Root { get; }

    /// <summary>
    /// Puts back the view this workspace was left in.
    /// </summary>
    /// <remarks>
    /// Applied with projection suppressed and then projected once, rather than
    /// letting each property change redraw the list on its way past.
    /// </remarks>
    private void Restore(WorkspaceViewState state)
    {
        _restoring = true;
        try
        {
            ViewMode = state.ViewMode;
            Search = state.Search;
            IncludeCompleted = state.IncludeCompleted;
            OverdueOnly = state.OverdueOnly;

            // An unparseable sort is a sort this build no longer has. Falling
            // back beats refusing to open the workspace over it.
            if (Enum.TryParse<TaskSort>(state.Sort, out var sort)) Sort = sort;

            foreach (var id in state.SelectedFilterIds) _restoredFilterIds.Add(id);

            // Only worth opening the panel when there is something in it to see.
            FiltersOpen = _restoredFilterIds.Count > 0 || IncludeCompleted || OverdueOnly;
        }
        finally
        {
            _restoring = false;
        }
    }

    /// <summary>The view as it stands, in the shape settings stores.</summary>
    private WorkspaceViewState CurrentViewState() => new()
    {
        ViewMode = ViewMode,
        Sort = Sort.ToString(),
        Search = Search,
        IncludeCompleted = IncludeCompleted,
        OverdueOnly = OverdueOnly,
        StatusIds = [.. SelectedIds(Statuses)],
        StatusTypes = [.. SelectedIds(StatusTypes)],
        CategoryIds = [.. SelectedIds(Categories)],
        TagIds = [.. SelectedIds(TagFilters)],
        Priorities = [.. SelectedIds(Priorities)],
    };

    private static IEnumerable<string> SelectedIds(IEnumerable<FilterToggle> toggles) =>
        toggles.Where(toggle => toggle.IsSelected).Select(toggle => toggle.Id);

    /// <summary>
    /// Captures the view now and writes it shortly.
    /// </summary>
    /// <remarks>
    /// The capture happens here, on the UI thread, so the timer never reads a
    /// collection the projection is midway through rebuilding.
    /// </remarks>
    private void ScheduleViewStateSave()
    {
        if (_restoring || _disposed) return;

        _pendingViewState = CurrentViewState();
        _saveViewState.Change(TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);
    }

    /// <summary>Writes any captured view state immediately.</summary>
    /// <remarks>
    /// Called as the workspace closes — switching to another one, or quitting —
    /// so the last change before it does is not the one that gets lost.
    /// </remarks>
    public void FlushViewState()
    {
        if (Interlocked.Exchange(ref _pendingViewState, null) is { } state)
        {
            _settings.SaveViewState(Root, state);
        }
    }

    /// <summary>
    /// <see cref="FlushViewState"/> for the timer, which has nowhere to throw.
    /// </summary>
    /// <remarks>
    /// An exception out of a <see cref="Timer"/> callback is unhandled and ends
    /// the process. <see cref="AppSettings"/> already declines to throw on a
    /// failed write, so this is the belt to that braces — the callback stays
    /// incapable of taking the application down however the settings layer is
    /// changed later.
    /// </remarks>
    private void SafelyFlushViewState()
    {
        try
        {
            FlushViewState();
        }
        catch (Exception error) when (!IsShutdown(error))
        {
            Report(error, "How this workspace was left could not be saved", background: false);
        }
        catch (Exception)
        {
            // Closing. Nothing to say.
        }
    }

    public ObservableCollection<TaskRowViewModel> Tasks { get; } = [];

    /// <summary>Every filter control that lives inside the panel.</summary>
    public ObservableCollection<FilterToggle> Statuses { get; } = [];

    public ObservableCollection<FilterToggle> StatusTypes { get; } = [];

    public ObservableCollection<FilterToggle> Categories { get; } = [];

    public ObservableCollection<FilterToggle> TagFilters { get; } = [];

    public ObservableCollection<FilterToggle> Priorities { get; } = [];

    public ObservableCollection<DueReminderViewModel> OutstandingReminders { get; } = [];

    public ObservableCollection<string> Conflicts { get; } = [];

    /// <summary>Task files that were on disk but could not be loaded.</summary>
    /// <remarks>
    /// A task the store refused — unparseable, misnamed, or written in a schema
    /// version newer than this build — is simply absent from
    /// <see cref="Tasks"/>, which on its own looks like the task was lost.
    /// Naming the file and the reason is what turns that into something the user
    /// can act on.
    /// </remarks>
    public ObservableCollection<string> Unreadable { get; } = [];

    public ObservableCollection<BoardColumnViewModel> Columns { get; } = [];

    /// <summary>
    /// The sorts, carrying the text to show for each.
    /// </summary>
    /// <remarks>
    /// <see cref="TaskSortExtensions.Label"/> has always existed and was never
    /// reached from here, so the drop-down offered "Smart" and "DueDate" — enum
    /// names, not something a user is meant to read.
    /// </remarks>
    public IReadOnlyList<SortOption> SortOptions { get; } =
        [.. Enum.GetValues<TaskSort>().Select(sort => new SortOption(sort, sort.Label()))];

    [ObservableProperty]
    private string _summaryLine = "";

    [ObservableProperty]
    private bool _isFiltered;

    /// <summary>The query the list view is currently showing.</summary>
    public TaskQuery Query => new()
    {
        Search = Search,
        Sort = Sort,
        IncludeCompleted = IncludeCompleted,
        OverdueOnly = OverdueOnly,
        StatusIds = Selected(Statuses),
        StatusTypes = new HashSet<StatusType>(
            StatusTypes.Where(t => t.IsSelected).Select(t => Enum.Parse<StatusType>(t.Id))),
        CategoryIds = Selected(Categories),
        TagIds = Selected(TagFilters),
        Priorities = new HashSet<Priority>(
            Priorities.Where(p => p.IsSelected).Select(p => Enum.Parse<Priority>(p.Id))),
    };

    private static IReadOnlySet<string> Selected(IEnumerable<FilterToggle> toggles) =>
        new HashSet<string>(toggles.Where(t => t.IsSelected).Select(t => t.Id));

    /// <summary>
    /// How many controls inside the filter panel are active.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="TaskQuery.IsFiltered"/>, which asks a
    /// different question — is this view narrowed at all — and so counts the
    /// search box. This counts only what is hidden behind the panel button,
    /// because the search box is a visible field outside it whose contents you
    /// can already see. It is a fact about a UI arrangement, which is why it
    /// lives here rather than on the query.
    /// </remarks>
    public int PanelFilterCount =>
        (Statuses.Any(t => t.IsSelected) ? 1 : 0)
        + (StatusTypes.Any(t => t.IsSelected) ? 1 : 0)
        + (Categories.Any(t => t.IsSelected) ? 1 : 0)
        + (TagFilters.Any(t => t.IsSelected) ? 1 : 0)
        + (Priorities.Any(t => t.IsSelected) ? 1 : 0)
        + (OverdueOnly ? 1 : 0)
        + (IncludeCompleted ? 1 : 0);

    public bool HasPanelFilters => PanelFilterCount > 0;

    partial void OnSearchChanged(string value) => Project();

    partial void OnSortChanged(TaskSort value) => Project();

    partial void OnIncludeCompletedChanged(bool value) => Project();

    partial void OnOverdueOnlyChanged(bool value) => Project();

    [RelayCommand]
    private void ToggleFilters() => FiltersOpen = !FiltersOpen;

    /// <summary>Called by every toggle in the panel when the user changes it.</summary>
    private void OnFilterToggled() => Project();

    public bool IsListView => ViewMode == ViewMode.List;

    public bool IsBoardView => ViewMode == ViewMode.Board;

    partial void OnViewModeChanged(ViewMode value) => Project();

    [RelayCommand]
    private void ShowList() => ViewMode = ViewMode.List;

    [RelayCommand]
    private void ShowBoard() => ViewMode = ViewMode.Board;

    /// <summary>
    /// Moves a card, dropping it above <paramref name="beforeTaskId"/> or at the
    /// bottom of the column when that is null.
    /// </summary>
    /// <remarks>
    /// Guarded, because the only caller is a drop handler — an <c>async void</c>
    /// with nowhere to throw. A rank that a hand-edited or sync-merged file left
    /// unusable, or a folder that has gone read-only under the drag, would
    /// otherwise end the process rather than the gesture.
    /// </remarks>
    public Task MoveOnBoardAsync(string taskId, string statusId, string? beforeTaskId) =>
        Guarded(async () =>
        {
            var snapshot = _session.Snapshot;
            var task = snapshot.TaskById(taskId);
            if (task is null) return;

            // Where the card lands is board logic, not view logic, so the view
            // model only asks and then applies the answer. A null answer means
            // the drop is a no-op or cannot be placed — do nothing rather than
            // guess.
            if (BoardProjection.DropTarget(snapshot.Tasks, statusId, taskId, beforeTaskId)
                is not { } target)
            {
                return;
            }

            await _session.ReorderOnBoardAsync(task, statusId, target.Above, target.Below);
        }, "This card could not be moved");

    /// <summary>Whether the detail pane has anything to show.</summary>
    /// <remarks>
    /// Asked of the pane rather than of the list row, because the board can
    /// select a task the list is not showing — a completed one, most obviously.
    /// </remarks>
    public bool HasSelection => Detail is not null;

    /// <summary>
    /// Whether the open task is absent from the rows the list is showing.
    /// </summary>
    /// <remarks>
    /// The pane closes only when a task leaves the workspace, so it outlives the
    /// filter that used to hide it — marking something Done no longer shuts the
    /// pane mid-edit. The cost is a pane with nothing selected behind it and no
    /// reason given, which this exists to explain.
    /// <para>
    /// List view only. The board carries completed work regardless of the
    /// filter and marks the open card itself, so there is nothing unexplained
    /// there.
    /// </para>
    /// </remarks>
    public bool SelectionHiddenFromList =>
        Detail is not null && SelectedTask is null && IsListView;

    /// <summary>
    /// Opening a task in the detail pane. Keyed by id rather than by row, so a
    /// rescan that rebuilds every row does not close the pane under the user,
    /// and so both views can select through the same door.
    /// </summary>
    public void SelectTaskById(string? taskId)
    {
        _selectedTaskId = taskId;

        _projecting = true;
        try
        {
            SelectedTask = taskId is null
                ? null
                : Tasks.FirstOrDefault(row => row.Id == taskId);
        }
        finally
        {
            _projecting = false;
        }

        MarkSelectedCard();
        SyncDetail();
    }

    /// <summary>The list view's own selection, which is one way in among two.</summary>
    partial void OnSelectedTaskChanged(TaskRowViewModel? value)
    {
        // Rebuilding Tasks makes the ListBox report a null selection on its way
        // past, which would otherwise close the pane on every rescan.
        if (_projecting) return;

        SelectTaskById(value?.Id);
    }

    private void MarkSelectedCard()
    {
        foreach (var column in Columns)
        {
            foreach (var card in column.Cards) card.IsSelected = card.Id == _selectedTaskId;
        }
    }

    /// <summary>
    /// Points the detail pane at whatever is selected.
    /// </summary>
    /// <remarks>
    /// The pane closes when the task leaves the workspace — trashed, or deleted
    /// by another machine — and not merely when it leaves the current view. A
    /// filter that hides the task you are editing, or a status change that does,
    /// should not shut the pane in the middle of the edit that caused it.
    /// </remarks>
    private void SyncDetail()
    {
        var task = _selectedTaskId is null ? null : _session.Snapshot.TaskById(_selectedTaskId);

        if (task is null)
        {
            Detail = null;
            return;
        }

        // Refresh in place, whether it is the same task or another one: an edit
        // landing back through the session must not replace the pane the user is
        // typing in, and a pane rebound to a new view model writes the previous
        // task's dropdown selections onto the new one. See
        // TaskDetailViewModel.Refresh.
        if (Detail is { } existing) existing.Refresh(task);
        else Detail = new TaskDetailViewModel(_session, task, _filePicker);
    }

    [RelayCommand]
    private Task CreateTaskAsync() => Guarded(async () =>
    {
        var summary = NewTaskSummary.Trim();
        if (summary.Length == 0) return;

        NewTaskSummary = "";

        try
        {
            await _session.CreateTaskAsync(summary);
        }
        catch
        {
            // Give the user back what they typed. Clearing the box before the
            // write is what makes the field feel immediate, but a failed write
            // that also swallows the summary makes them type it again.
            OnUiThread(() => NewTaskSummary = summary);
            throw;
        }
    }, "This task could not be created");

    /// <remarks>
    /// Reported as background work even though a button started it: what the
    /// user asked for is precisely that the list be brought up to date, so
    /// "what you see may be out of date" is the accurate thing to say when it
    /// could not be.
    /// </remarks>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            await _session.RefreshAsync();
            OnUiThread(ClearBackgroundBanner);
        }
        catch (Exception error)
        {
            Report(error, "This workspace could not be reloaded");
        }
    }

    /// <summary>
    /// The rescan the watcher asks for, which has no caller to fail to.
    /// </summary>
    /// <remarks>
    /// A rescan launched and forgotten fails silently, leaving the list showing
    /// state that is quietly out of date — the one thing live reload exists to
    /// prevent. The task is therefore kept, so a failure reaches the banner and
    /// tests can wait for it.
    /// </remarks>
    public void RefreshInBackground() => _pending.Add(ReloadAsync());

    /// <summary>The background rescan in flight, for tests to await.</summary>
    public Task PendingBackgroundWork => _pending.All;

    private readonly PendingWork _pending = new();

    private async Task ReloadAsync()
    {
        try
        {
            await _session.RefreshAsync();
            OnUiThread(ClearBackgroundBanner);
        }
        catch (Exception error)
        {
            Report(error, "This workspace could not be reloaded");
        }
    }

    /// <summary>
    /// Puts a background failure where the user can see it.
    /// </summary>
    /// <remarks>
    /// Everything that runs without a caller — the watcher's rescan, the
    /// reminder clock — reports here, so there is one place a failure can
    /// surface rather than one per producer. Shutting down is not reported: a
    /// cancelled or disposed session is this workspace closing, which the user
    /// asked for.
    /// </remarks>
    private void Report(Exception error, string what) => Report(error, what, background: true);

    /// <param name="background">
    /// Whether this failure came from work nobody is watching. A rescan that
    /// fails leaves the list showing something that may no longer be true, which
    /// is worth saying; a command that fails has already not happened, and
    /// telling the user to press Refresh over it would be an instruction to fix
    /// nothing.
    /// </param>
    private void Report(Exception error, string what, bool background)
    {
        if (IsShutdown(error)) return;

        OnUiThread(() =>
        {
            if (_disposed) return;

            _backgroundBanner = background
                ? $"{what}: {error.Message} "
                  + "What you see may be out of date — press Refresh to try again."
                : $"{what}: {error.Message}";
            Banner = _backgroundBanner;
        });
    }

    /// <summary>
    /// Runs a command that writes to the workspace, putting any failure in the
    /// banner rather than letting it escape.
    /// </summary>
    /// <remarks>
    /// An <c>AsyncRelayCommand</c> rethrows a failed command onto the UI thread,
    /// where nothing catches it and the process ends. The workspace is a folder
    /// that can be unmounted, filled or made read-only while the app is looking
    /// at it — ordinary conditions for the storage this is designed for, and not
    /// ones worth closing over. A success also takes down a banner it put up, so
    /// a failure that has since been fixed does not sit there.
    /// </remarks>
    private async Task Guarded(Func<Task> work, string what)
    {
        try
        {
            await work();
            OnUiThread(ClearBackgroundBanner);
        }
        catch (Exception error)
        {
            Report(error, what, background: false);
        }
    }

    private void ClearBackgroundBanner()
    {
        if (_backgroundBanner is null) return;
        if (Banner == _backgroundBanner) Banner = null;
        _backgroundBanner = null;
    }

    [RelayCommand]
    private void CloseDetail() => SelectTaskById(null);

    /// <summary>
    /// A settings view model over this workspace's session. Created per window
    /// so it can unsubscribe when that window closes.
    /// </summary>
    public SettingsViewModel CreateSettingsViewModel() =>
        new(_session, _settings, _filePicker, _fileSaver, CurrentExportSelection);

    /// <summary>
    /// The rows the list is showing, in the order it is showing them.
    /// </summary>
    /// <remarks>
    /// Asked for when Export is pressed rather than held by the settings page,
    /// because the filter is view state and the user can change it with both
    /// windows open. The board is governed by the same query, so switching view
    /// does not change the answer.
    /// </remarks>
    private ExportSelection CurrentExportSelection()
    {
        var snapshot = _session.Snapshot;
        var query = Query;
        var name = _settings.CurrentWorkspace?.Name is { Length: > 0 } chosen
            ? chosen
            : Path.GetFileName(Root.TrimEnd(Path.DirectorySeparatorChar));

        var suggested = $"{name} tasks {DateTime.Now:yyyy-MM-dd}.csv";
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            suggested = suggested.Replace(invalid, '-');
        }

        return new ExportSelection(
            query.Apply(snapshot.Tasks, snapshot.Config), query.IsFiltered, suggested);
    }

    [RelayCommand]
    private void ClearFilters()
    {
        Search = "";
        IncludeCompleted = false;
        OverdueOnly = false;

        foreach (var toggle in Statuses.Concat(StatusTypes).Concat(Categories)
                     .Concat(TagFilters).Concat(Priorities))
        {
            toggle.ClearWithoutNotifying();
        }

        Project();
    }

    [RelayCommand]
    private Task DismissReminderAsync(DueReminderViewModel? reminder) => Guarded(async () =>
    {
        if (reminder is null) return;
        var task = _session.Snapshot.TaskById(reminder.TaskId);
        if (task is null) return;

        await _session.DismissRemindersAsync(
            task, new HashSet<string> { reminder.ReminderId });
    }, "This reminder could not be dismissed");

    [RelayCommand]
    private Task TrashTaskAsync(TaskRowViewModel? row) => Guarded(async () =>
    {
        if (row is null) return;
        var task = _session.Snapshot.TaskById(row.Id);
        if (task is not null) await _session.TrashTaskAsync(task);
    }, "This task could not be moved to the trash");

    /// <summary>
    /// Moves the task the detail pane is showing to the workspace's trash
    /// folder. Off the pane rather than the row, because the pane can show a
    /// task no list row holds — completed, or hidden by the filter.
    /// </summary>
    [RelayCommand]
    private Task TrashOpenTaskAsync() => Guarded(async () =>
    {
        if (Detail is null) return;
        var task = _session.Snapshot.TaskById(Detail.TaskId);
        if (task is not null) await _session.TrashTaskAsync(task);
    }, "This task could not be moved to the trash");

    private void OnWorkspaceChanged(object? sender, WorkspaceChangedEventArgs e) =>
        OnUiThread(Project);

    /// <summary>
    /// Rebuilds everything the view shows from the current snapshot and query.
    /// </summary>
    private void Project()
    {
        if (_disposed || _restoring) return;

        // Held for the whole rebuild: emptying Tasks makes the ListBox report a
        // null selection, which without this would look like the user closing
        // the pane.
        _projecting = true;
        try
        {
            Rebuild();
        }
        finally
        {
            _projecting = false;
        }

        SelectTaskById(_selectedTaskId);
        ScheduleViewStateSave();
    }

    /// <summary>
    /// Rebuilds the view on screen, and empties the one that is not.
    /// </summary>
    /// <remarks>
    /// This runs on every keystroke in the search box, every filter toggle,
    /// every reminder tick and every rescan, and each run allocates a view model
    /// per row. Doing that for the list <i>and</i> the board when only one of
    /// them is visible doubled the cost of typing for a view nobody could see.
    /// The other view is emptied rather than left stale, so nothing can bind to
    /// rows that no longer describe the workspace, and switching view projects
    /// again on its way past.
    /// </remarks>
    private void Rebuild()
    {
        var snapshot = _session.Snapshot;
        var query = Query;
        var visible = IsListView
            ? query.Apply(snapshot.Tasks, snapshot.Config)
            : [];

        Replace(Tasks, visible.Select(task => new TaskRowViewModel(task, snapshot.Config)));

        // Rebuilt from the config each time, so a status renamed in settings
        // shows its new name here — carrying the selection across by id.
        ReplaceToggles(Statuses, snapshot.Config.Statuses
            .Select(status => (status.Id, status.Name)));
        ReplaceToggles(StatusTypes, Enum.GetValues<StatusType>()
            .Select(type => (type.ToString(), type.Label())));
        ReplaceToggles(Categories, snapshot.Config.Categories
            .Select(category => (category.Id, category.Name)));
        ReplaceToggles(TagFilters, snapshot.Config.Tags.Select(tag => (tag.Id, tag.Name)));
        ReplaceToggles(Priorities, Enum.GetValues<Priority>()
            .Select(priority => (priority.ToString(), priority.Label())));

        Replace(OutstandingReminders, snapshot
            .OutstandingReminders(DateTime.UtcNow)
            .Select(due => new DueReminderViewModel(
                due.Task.Id, due.Reminder.Id, due.Task.Summary, due.Reminder.RemindAt)));

        Replace(Conflicts, snapshot.Conflicts.Select(conflict => conflict.FileName));

        Replace(Unreadable, snapshot.Failures
            .Select(failure => $"{failure.FileName} — {failure.Error.Message}"));

        // The board always shows Final columns populated, even though the list
        // hides completed work by default: a column headed "Done" holding
        // nothing would be worse than useless. That is a decision about this
        // view, so it is applied here rather than in the query's defaults.
        var columns = IsBoardView
            ? BoardProjection.Columns(
                (query with { IncludeCompleted = true }).Apply(snapshot.Tasks, snapshot.Config),
                snapshot.Config)
            : [];

        Replace(Columns, columns
            .Select(column => new BoardColumnViewModel(
                column.Status,
                column.Tasks.Select(task => new BoardCardViewModel(task, snapshot.Config)))));

        // The restored selections have now been offered to every group, so they
        // stop being pending. Holding them longer would re-select a filter the
        // user had just cleared.
        _restoredFilterIds.Clear();

        IsFiltered = query.IsFiltered;
        OnPropertyChanged(nameof(PanelFilterCount));
        OnPropertyChanged(nameof(HasPanelFilters));

        // The two views show different sets — the board populates Final columns
        // the list hides, and omits statuses flagged off the board — so the
        // count has to follow whichever view is on screen.
        var shown = IsBoardView ? Columns.Sum(column => column.Cards.Count) : visible.Count;
        SummaryLine = shown == snapshot.Tasks.Count
            ? $"{shown} task{(shown == 1 ? "" : "s")}"
            : $"{shown} of {snapshot.Tasks.Count} tasks";
    }

    /// <summary>
    /// Rebuilds a set of toggles, preserving which were selected. Replacing them
    /// wholesale on every rescan would clear the user's filters whenever a sync
    /// client touched a file.
    /// </summary>
    private void ReplaceToggles(
        ObservableCollection<FilterToggle> target, IEnumerable<(string Id, string Name)> items)
    {
        var selected = target.Where(t => t.IsSelected).Select(t => t.Id).ToHashSet();
        selected.UnionWith(_restoredFilterIds);

        target.Clear();
        foreach (var (id, name) in items)
        {
            target.Add(new FilterToggle(id, name, selected.Contains(id), OnFilterToggled));
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    /// <summary>
    /// Whether this workspace has been closed: its watcher, reminder clock and
    /// session all stopped.
    /// </summary>
    /// <remarks>
    /// The shell owns exactly one live workspace, and everything a workspace
    /// runs keeps running until it is disposed, so whether the one being
    /// replaced was let go of is worth being able to ask.
    /// </remarks>
    public bool IsDisposed => _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        FlushViewState();
        _saveViewState.Dispose();

        // Producers first, then the thing they drive. Stopping the watcher and
        // the reminder clock before the session means nothing new is handed to
        // a session that is closing, and disposing the session then tells
        // whatever is still queued behind its gate to give up rather than write
        // to a workspace the user has left.
        _watcher.Dispose();
        _reminders.Dispose();
        _session.Changed -= OnWorkspaceChanged;
        _session.Dispose();
    }
}

/// <summary>
/// What a live workspace is wired to besides its session.
/// </summary>
/// <remarks>
/// <see cref="WorkspaceSession"/> deliberately knows nothing about watching or
/// reminders — wiring those together is the composition root's job — but the
/// view model had been doing that job itself, with a <c>new</c> of each in its
/// constructor. That left no way to open a workspace without a real
/// <c>FileSystemWatcher</c> on a real folder and a notifier that shells out to
/// the desktop, and so no way to test the one thing most likely to break: what
/// this projection does when a rescan and a reminder tick land on it.
/// <para>
/// Factories rather than finished objects, because both take the session the
/// view model is being built around, and the view model owns and disposes them.
/// </para>
/// </remarks>
public sealed record WorkspaceServices(
    Func<WorkspaceSession, WorkspaceWatcher> Watcher,
    Func<WorkspaceSession, ReminderScheduler> Reminders)
{
    /// <summary>What the application uses: a real watcher and this platform's notifier.</summary>
    public static WorkspaceServices Real { get; } = new(
        session => new WorkspaceWatcher(session.Workspace),
        session => new ReminderScheduler(session, ReminderNotifiers.ForCurrentPlatform()));

    /// <summary>How often the reminder clock ticks, or null for its own default.</summary>
    public TimeSpan? ReminderInterval { get; init; }
}

/// <summary>One row of the list view, with everything it displays already resolved.</summary>
public sealed class TaskRowViewModel(MightDoTask task, WorkspaceConfig config)
{
    public string Id { get; } = task.Id;

    public string Summary { get; } = task.Summary;

    public string StatusName { get; } = config.StatusById(task.StatusId)?.Name ?? "Unknown status";

    // Style-class hooks: the XAML toggles classes off these, which is how a
    // chip or dot gets its colour without the view model naming a brush.
    public bool IsInitialStatus { get; } = config.StatusById(task.StatusId)?.Type == StatusType.Initial;

    public bool IsActiveStatus { get; } = config.StatusById(task.StatusId)?.Type == StatusType.Active;

    public bool IsFinalStatus { get; } = config.StatusById(task.StatusId)?.Type == StatusType.Final;

    public string PriorityLabel { get; } = task.Priority.Label();

    public bool IsLowPriority { get; } = task.Priority == Priority.Low;

    public bool IsMediumPriority { get; } = task.Priority == Priority.Medium;

    public bool IsHighPriority { get; } = task.Priority == Priority.High;

    public bool IsCriticalPriority { get; } = task.Priority == Priority.Critical;

    public string? CategoryName { get; } = config.CategoryById(task.CategoryId)?.Name;

    /// <summary>The category's stored colour, shown as a dot in its chip.</summary>
    public IBrush CategoryBrush { get; } =
        new ImmutableSolidColorBrush(config.CategoryById(task.CategoryId)?.Color ?? 0);

    public string TagNames { get; } =
        string.Join(", ", config.TagsByIds(task.TagIds).Select(tag => tag.Name));

    /// <summary>
    /// The one date the row carries: when it is due, or — once the task has
    /// landed in a Final status — when it was completed. Matches the board's
    /// cards; see <see cref="BoardCardViewModel.DateLabel"/>.
    /// </summary>
    public string DateLabel { get; } = task.CompletedAt is { } completed
        ? $"Completed {completed.ToLocalTime():yyyy-MM-dd}"
        : task.DueDate?.ToIso() ?? "";

    public bool IsOverdue { get; } = task.IsOverdue;

    public bool IsComplete { get; } = task.IsComplete;

    public string StepsLabel { get; } =
        task.Steps.Count == 0 ? "" : $"{task.StepsDone}/{task.Steps.Count}";

    public bool HasSteps { get; } = task.Steps.Count > 0;

    public bool HasCategory { get; } = task.CategoryId is not null;

    public bool HasTags { get; } = task.TagIds.Count > 0;

    public bool HasDate { get; } = task.CompletedAt is not null || task.DueDate is not null;
}

/// <summary>
/// One selectable value in the filter panel — a Status, a Status Type, a
/// Category, a Tag or a Priority. They behave identically, so they share a type.
/// </summary>
public sealed partial class FilterToggle : ObservableObject
{
    private readonly Action _onToggled;
    private bool _suppress;

    [ObservableProperty] private bool _isSelected;

    public FilterToggle(string id, string name, bool isSelected, Action onToggled)
    {
        Id = id;
        Name = name;
        _isSelected = isSelected;
        _onToggled = onToggled;
    }

    public string Id { get; }
    public string Name { get; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (!_suppress) _onToggled();
    }

    /// <summary>Clears without re-querying, so a bulk clear queries once.</summary>
    public void ClearWithoutNotifying()
    {
        _suppress = true;
        try
        {
            IsSelected = false;
        }
        finally
        {
            _suppress = false;
        }
    }
}

public sealed record DueReminderViewModel(
    string TaskId, string ReminderId, string Summary, DateTime RemindAt)
{
    public string When => RemindAt.ToLocalTime().ToString("g");
}

/// <summary>One entry in the sort drop-down: the value, and what to show for it.</summary>
public sealed record SortOption(TaskSort Value, string Label);
