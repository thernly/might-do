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
    private TaskRowViewModel? _selectedTask;

    [ObservableProperty]
    private string _newTaskSummary = "";

    [ObservableProperty]
    private string? _banner;

    private WorkspaceViewModel(
        WorkspaceSession session, AppSettings settings, string root)
    {
        _session = session;
        _settings = settings;
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

        Project();
    }

    public static async Task<WorkspaceViewModel> OpenAsync(
        TaskStore store, AppSettings settings)
    {
        var session = await WorkspaceSession.OpenAsync(store);
        return new WorkspaceViewModel(session, settings, store.Workspace.Root);
    }

    public string Root { get; }

    public ObservableCollection<TaskRowViewModel> Tasks { get; } = [];

    public ObservableCollection<StatusFilterViewModel> Statuses { get; } = [];

    public ObservableCollection<DueReminderViewModel> OutstandingReminders { get; } = [];

    public ObservableCollection<string> Conflicts { get; } = [];

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

        IsFiltered = query.IsFiltered;
        SummaryLine = visible.Count == snapshot.Tasks.Count
            ? $"{visible.Count} task{(visible.Count == 1 ? "" : "s")}"
            : $"{visible.Count} of {snapshot.Tasks.Count} tasks";

        // A rescan can remove whatever was selected.
        if (SelectedTask is { } selected && Tasks.All(row => row.Id != selected.Id))
        {
            SelectedTask = null;
        }
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
