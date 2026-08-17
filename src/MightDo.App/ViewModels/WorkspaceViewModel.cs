using System.Collections.ObjectModel;
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
    private string? _selectedTaskId;
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
    private StatusFilterViewModel? _selectedStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private TaskRowViewModel? _selectedTask;

    [ObservableProperty]
    private TaskDetailViewModel? _detail;

    [ObservableProperty]
    private string _newTaskSummary = "";

    [ObservableProperty]
    private string? _banner;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListView))]
    [NotifyPropertyChangedFor(nameof(IsBoardView))]
    private ViewMode _viewMode = ViewMode.List;

    private WorkspaceViewModel(
        WorkspaceSession session, AppSettings settings, IFilePicker filePicker, string root)
    {
        _session = session;
        _settings = settings;
        _filePicker = filePicker;
        Root = root;

        _session.Changed += OnWorkspaceChanged;

        // ADR-0003: the watcher only ever asks for a rescan. It holds no session
        // and cannot write, and RefreshAsync is the same path the manual refresh
        // button uses.
        _watcher = new WorkspaceWatcher(session.Workspace);
        _watcher.RescanRequested += (_, _) => _ = RefreshAsync();
        _watcher.RootVanished += (_, _) => OnUiThread(() =>
            Banner = "This workspace folder is no longer there. "
                     + "If it is on a drive or a synced folder, it may come back.");
        _watcher.Start();

        _reminders = new ReminderScheduler(session, ReminderNotifiers.ForCurrentPlatform());
        _reminders.Fired += (_, _) => OnUiThread(Project);
        _reminders.Start();

        ViewMode = settings.ViewMode;
        Project();
    }

    public static async Task<WorkspaceViewModel> OpenAsync(
        TaskStore store, AppSettings settings, IFilePicker filePicker)
    {
        var session = await WorkspaceSession.OpenAsync(store);
        return new WorkspaceViewModel(session, settings, filePicker, store.Workspace.Root);
    }

    public string Root { get; }

    public ObservableCollection<TaskRowViewModel> Tasks { get; } = [];

    public ObservableCollection<StatusFilterViewModel> Statuses { get; } = [];

    public ObservableCollection<DueReminderViewModel> OutstandingReminders { get; } = [];

    public ObservableCollection<string> Conflicts { get; } = [];

    public ObservableCollection<BoardColumnViewModel> Columns { get; } = [];

    public IReadOnlyList<TaskSort> SortOptions { get; } = Enum.GetValues<TaskSort>();

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
        StatusIds = SelectedStatus?.Id is { } id
            ? new HashSet<string> { id }
            : new HashSet<string>(),
    };

    partial void OnSearchChanged(string value) => Project();

    partial void OnSortChanged(TaskSort value) => Project();

    partial void OnIncludeCompletedChanged(bool value) => Project();

    partial void OnOverdueOnlyChanged(bool value) => Project();

    partial void OnSelectedStatusChanged(StatusFilterViewModel? value) => Project();

    public bool IsListView => ViewMode == ViewMode.List;

    public bool IsBoardView => ViewMode == ViewMode.Board;

    partial void OnViewModeChanged(ViewMode value)
    {
        _settings.SetViewMode(value);
        Project();
    }

    [RelayCommand]
    private void ShowList() => ViewMode = ViewMode.List;

    [RelayCommand]
    private void ShowBoard() => ViewMode = ViewMode.Board;

    /// <summary>
    /// Moves a card, dropping it above <paramref name="beforeTaskId"/> or at the
    /// bottom of the column when that is null.
    /// </summary>
    public async Task MoveOnBoardAsync(string taskId, string statusId, string? beforeTaskId)
    {
        var snapshot = _session.Snapshot;
        var task = snapshot.TaskById(taskId);
        if (task is null) return;

        // Where the card lands is board logic, not view logic, so the view model
        // only asks and then applies the answer. A null answer means the drop is
        // a no-op or cannot be placed — do nothing rather than guess.
        if (BoardProjection.DropTarget(snapshot.Tasks, statusId, taskId, beforeTaskId)
            is not { } target)
        {
            return;
        }

        await _session.ReorderOnBoardAsync(task, statusId, target.Above, target.Below);
    }

    public bool HasSelection => SelectedTask is not null;

    /// <summary>
    /// Opening a task in the detail pane. Keyed by id rather than by row, so a
    /// rescan that rebuilds every row does not close the pane under the user.
    /// </summary>
    partial void OnSelectedTaskChanged(TaskRowViewModel? value)
    {
        _selectedTaskId = value?.Id;
        SyncDetail();
    }

    private void SyncDetail()
    {
        var task = _selectedTaskId is null ? null : _session.Snapshot.TaskById(_selectedTaskId);

        if (task is null)
        {
            Detail = null;
            return;
        }

        // Refresh in place when it is the same task, so an edit landing back
        // through the session does not replace the pane the user is typing in.
        if (Detail is { } existing && existing.TaskId == task.Id) existing.Refresh(task);
        else Detail = new TaskDetailViewModel(_session, task, _filePicker);
    }

    [RelayCommand]
    private async Task CreateTaskAsync()
    {
        var summary = NewTaskSummary.Trim();
        if (summary.Length == 0) return;

        NewTaskSummary = "";
        await _session.CreateTaskAsync(summary);
    }

    [RelayCommand]
    private Task RefreshAsync() => _session.RefreshAsync();

    [RelayCommand]
    private void CloseDetail() => SelectedTask = null;

    /// <summary>
    /// A settings view model over this workspace's session. Created per window
    /// so it can unsubscribe when that window closes.
    /// </summary>
    public SettingsViewModel CreateSettingsViewModel() => new(_session);

    [RelayCommand]
    private void ClearFilters()
    {
        Search = "";
        IncludeCompleted = false;
        OverdueOnly = false;
        SelectedStatus = null;
    }

    [RelayCommand]
    private async Task DismissReminderAsync(DueReminderViewModel? reminder)
    {
        if (reminder is null) return;
        var task = _session.Snapshot.TaskById(reminder.TaskId);
        if (task is null) return;

        await _session.DismissRemindersAsync(
            task, new HashSet<string> { reminder.ReminderId });
    }

    [RelayCommand]
    private async Task TrashTaskAsync(TaskRowViewModel? row)
    {
        if (row is null) return;
        var task = _session.Snapshot.TaskById(row.Id);
        if (task is not null) await _session.TrashTaskAsync(task);
    }

    private void OnWorkspaceChanged(object? sender, WorkspaceChangedEventArgs e) =>
        OnUiThread(Project);

    /// <summary>
    /// Rebuilds everything the view shows from the current snapshot and query.
    /// </summary>
    private void Project()
    {
        if (_disposed) return;

        var snapshot = _session.Snapshot;
        var query = Query;
        var visible = query.Apply(snapshot.Tasks, snapshot.Config);

        Replace(Tasks, visible.Select(task => new TaskRowViewModel(task, snapshot.Config)));

        Replace(Statuses, snapshot.Config.Statuses.Select(
            status => new StatusFilterViewModel(status.Id, status.Name)));

        Replace(OutstandingReminders, snapshot
            .OutstandingReminders(DateTime.UtcNow)
            .Select(due => new DueReminderViewModel(
                due.Task.Id, due.Reminder.Id, due.Task.Summary, due.Reminder.RemindAt)));

        Replace(Conflicts, snapshot.Conflicts.Select(conflict => conflict.FileName));

        // The board always shows Final columns populated, even though the list
        // hides completed work by default: a column headed "Done" holding
        // nothing would be worse than useless. That is a decision about this
        // view, so it is applied here rather than in the query's defaults.
        var boardTasks = (query with { IncludeCompleted = true })
            .Apply(snapshot.Tasks, snapshot.Config);

        Replace(Columns, BoardProjection
            .Columns(boardTasks, snapshot.Config)
            .Select(column => new BoardColumnViewModel(
                column.Status,
                column.Tasks.Select(task => new BoardCardViewModel(task, snapshot.Config)))));

        IsFiltered = query.IsFiltered;

        // The two views show different sets — the board populates Final columns
        // the list hides, and omits statuses flagged off the board — so the
        // count has to follow whichever view is on screen.
        var shown = IsBoardView ? Columns.Sum(column => column.Cards.Count) : visible.Count;
        SummaryLine = shown == snapshot.Tasks.Count
            ? $"{shown} task{(shown == 1 ? "" : "s")}"
            : $"{shown} of {snapshot.Tasks.Count} tasks";

        // Every row is a new object after a rescan, so reattach the selection by
        // id. Without this the pane closes whenever anything on disk changes.
        SelectedTask = _selectedTaskId is null
            ? null
            : Tasks.FirstOrDefault(row => row.Id == _selectedTaskId);

        SyncDetail();
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    /// <summary>
    /// <see cref="WorkspaceSession.Changed"/> is raised on whichever thread
    /// finished the work, which for a rescan is a background one.
    /// </summary>
    private static void OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _session.Changed -= OnWorkspaceChanged;
        _watcher.Dispose();
        _reminders.Dispose();
        _session.Dispose();
    }
}

/// <summary>One row of the list view, with everything it displays already resolved.</summary>
public sealed class TaskRowViewModel(MightDoTask task, WorkspaceConfig config)
{
    public string Id { get; } = task.Id;

    public string Summary { get; } = task.Summary;

    public string StatusName { get; } = config.StatusById(task.StatusId)?.Name ?? "Unknown status";

    public string PriorityLabel { get; } = task.Priority.Label();

    public string? CategoryName { get; } = config.CategoryById(task.CategoryId)?.Name;

    public string TagNames { get; } =
        string.Join(", ", config.TagsByIds(task.TagIds).Select(tag => tag.Name));

    public string DueLabel { get; } = task.DueDate?.ToIso() ?? "";

    public bool IsOverdue { get; } = task.IsOverdue;

    public bool IsComplete { get; } = task.IsComplete;

    public string StepsLabel { get; } =
        task.Steps.Count == 0 ? "" : $"{task.StepsDone}/{task.Steps.Count}";

    public bool HasSteps { get; } = task.Steps.Count > 0;

    public bool HasCategory { get; } = task.CategoryId is not null;

    public bool HasTags { get; } = task.TagIds.Count > 0;

    public bool HasDue { get; } = task.DueDate is not null;
}

public sealed record StatusFilterViewModel(string Id, string Name);

public sealed record DueReminderViewModel(
    string TaskId, string ReminderId, string Summary, DateTime RemindAt)
{
    public string When => RemindAt.ToLocalTime().ToString("g");
}
