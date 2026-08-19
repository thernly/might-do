using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MightDo.Core.Domain;
using MightDo.Core.Session;
using MightDo.Platform;

namespace MightDo.App.ViewModels;

/// <summary>
/// Managing the things a workspace is made of: its Statuses, Categories and
/// Tags.
/// </summary>
/// <remarks>
/// Deleting any of them is where the care is. A Status in use cannot simply go —
/// its tasks have to be told where to move — and the same for a Category. Tags
/// are the exception: they are deliberately lightweight, so deleting one just
/// detaches it everywhere.
/// </remarks>
public sealed partial class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly WorkspaceSession _session;
    private readonly AppSettings _settings;
    private bool _loading;
    private bool _disposed;

    [ObservableProperty] private string _newStatusName = "";
    [ObservableProperty] private StatusType _newStatusType = StatusType.Active;
    [ObservableProperty] private string _newCategoryName = "";
    [ObservableProperty] private string _newCategoryColor = "FF4F6D7A";
    [ObservableProperty] private string _newTagName = "";
    [ObservableProperty] private string? _error;

    /// <summary>The row awaiting a "where should its tasks go?" answer, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingStatusDelete))]
    private StatusRowViewModel? _statusPendingDelete;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingCategoryDelete))]
    private CategoryRowViewModel? _categoryPendingDelete;

    [ObservableProperty] private StatusOption? _statusReassignTarget;
    [ObservableProperty] private CategoryOption? _categoryReassignTarget;

    public SettingsViewModel(WorkspaceSession session, AppSettings settings)
    {
        _session = session;
        _settings = settings;
        _session.Changed += OnWorkspaceChanged;
        Refresh();
        RefreshTrashInBackground();
    }

    public ObservableCollection<StatusRowViewModel> Statuses { get; } = [];
    public ObservableCollection<CategoryRowViewModel> Categories { get; } = [];
    public ObservableCollection<TagRowViewModel> Tags { get; } = [];
    public ObservableCollection<TrashRowViewModel> TrashedTasks { get; } = [];

    public ObservableCollection<StatusOption> StatusReassignOptions { get; } = [];
    public ObservableCollection<CategoryOption> CategoryReassignOptions { get; } = [];

    public IReadOnlyList<StatusType> StatusTypes { get; } = Enum.GetValues<StatusType>();

    public bool IsConfirmingStatusDelete => StatusPendingDelete is not null;
    public bool IsConfirmingCategoryDelete => CategoryPendingDelete is not null;

    // ---- appearance --------------------------------------------------------

    /// <summary>
    /// The colour scheme. Unlike everything else on this page it belongs to the
    /// machine rather than to the workspace, so switching workspace leaves it
    /// alone.
    /// </summary>
    public ThemePreference Theme => _settings.Theme;

    public bool IsAutoTheme => Theme == ThemePreference.Auto;

    public bool IsLightTheme => Theme == ThemePreference.Light;

    public bool IsDarkTheme => Theme == ThemePreference.Dark;

    /// <summary>
    /// Chooses a colour scheme, applies it and remembers it.
    /// </summary>
    /// <remarks>
    /// Applied before it is announced so the window has already repainted by
    /// the time the radio buttons update, rather than the other way round.
    /// </remarks>
    [RelayCommand]
    private void SetTheme(ThemePreference theme)
    {
        if (Theme == theme) return;

        _settings.SetTheme(theme);
        MightDo.App.Theme.Apply(theme);

        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(IsAutoTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    // ---- statuses ----------------------------------------------------------

    [RelayCommand]
    private Task AddStatusAsync() => Guarded(async () =>
    {
        var name = NewStatusName.Trim();
        if (name.Length == 0) return;

        NewStatusName = "";
        await _session.AddStatusAsync(name, NewStatusType);
    });

    [RelayCommand]
    private Task RenameStatusAsync(StatusRowViewModel? row) => Guarded(async () =>
    {
        if (_loading || row is null) return;

        var status = _session.Snapshot.Config.StatusById(row.Id);
        var name = row.Name.Trim();
        if (status is null || name.Length == 0 || status.Name == name) return;

        await _session.UpdateStatusAsync(status with { Name = name });
    });

    [RelayCommand]
    private Task SetStatusTypeAsync(StatusRowViewModel? row) => Guarded(async () =>
    {
        if (_loading || row is null) return;

        var status = _session.Snapshot.Config.StatusById(row.Id);
        if (status is null || status.Type == row.Type) return;

        await _session.UpdateStatusAsync(status with { Type = row.Type });
    });

    [RelayCommand]
    private Task SetStatusHiddenAsync(StatusRowViewModel? row) => Guarded(async () =>
    {
        if (_loading || row is null) return;

        var status = _session.Snapshot.Config.StatusById(row.Id);
        if (status is null || status.HiddenFromBoard == row.HiddenFromBoard) return;

        await _session.UpdateStatusAsync(status with { HiddenFromBoard = row.HiddenFromBoard });
    });

    [RelayCommand]
    private async Task MakeDefaultAsync(StatusRowViewModel? row)
    {
        if (row is null) return;

        await Guarded(() => _session.SetDefaultStatusAsync(row.Id));
    }

    [RelayCommand]
    private Task MoveStatusUpAsync(StatusRowViewModel? row) => MoveStatusAsync(row, -1);

    [RelayCommand]
    private Task MoveStatusDownAsync(StatusRowViewModel? row) => MoveStatusAsync(row, +1);

    /// <summary>Reordering statuses is also reordering the board's columns.</summary>
    private Task MoveStatusAsync(StatusRowViewModel? row, int offset) => Guarded(async () =>
    {
        if (row is null) return;

        var index = Statuses.IndexOf(row);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= Statuses.Count) return;

        var ordered = Statuses
            .Select(candidate => _session.Snapshot.Config.StatusById(candidate.Id))
            .OfType<Status>()
            .ToList();

        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        await _session.ReorderStatusesAsync(ordered);
    });

    [RelayCommand]
    private void BeginDeleteStatus(StatusRowViewModel? row)
    {
        Error = null;
        if (row is null || !row.CanDelete) return;

        // Tasks are never orphaned by a settings change, so a replacement has to
        // be chosen before the status can go.
        Replace(StatusReassignOptions, _session.Snapshot.Config.Statuses
            .Where(status => status.Id != row.Id)
            .Select(status => new StatusOption(status.Id, status.Name)));

        StatusReassignTarget = StatusReassignOptions.FirstOrDefault();
        StatusPendingDelete = row;
    }

    [RelayCommand]
    private void CancelDeleteStatus() => StatusPendingDelete = null;

    [RelayCommand]
    private async Task ConfirmDeleteStatusAsync()
    {
        if (StatusPendingDelete is not { } row || StatusReassignTarget is not { } target) return;

        StatusPendingDelete = null;
        await Guarded(() => _session.DeleteStatusAsync(row.Id, target.Id));
    }

    // ---- categories --------------------------------------------------------

    [RelayCommand]
    private Task AddCategoryAsync() => Guarded(async () =>
    {
        var name = NewCategoryName.Trim();
        if (name.Length == 0) return;

        if (!TryParseColor(NewCategoryColor, out var color))
        {
            Error = "A colour is eight hex digits, alpha first — FF4F6D7A.";
            return;
        }

        NewCategoryName = "";
        await _session.AddCategoryAsync(name, color);
    });

    [RelayCommand]
    private Task RenameCategoryAsync(CategoryRowViewModel? row) => Guarded(async () =>
    {
        if (_loading || row is null) return;

        var category = _session.Snapshot.Config.CategoryById(row.Id);
        var name = row.Name.Trim();
        if (category is null || name.Length == 0 || category.Name == name) return;

        await _session.UpdateCategoryAsync(category with { Name = name });
    });

    [RelayCommand]
    private Task SetCategoryColorAsync(CategoryRowViewModel? row) => Guarded(async () =>
    {
        if (_loading || row is null) return;

        var category = _session.Snapshot.Config.CategoryById(row.Id);
        if (category is null) return;

        if (!TryParseColor(row.ColorHex, out var color))
        {
            Error = "A colour is eight hex digits, alpha first — FF4F6D7A.";
            return;
        }

        if (category.Color == color) return;

        await _session.UpdateCategoryAsync(category with { Color = color });
    });

    [RelayCommand]
    private void BeginDeleteCategory(CategoryRowViewModel? row)
    {
        Error = null;
        if (row is null) return;

        // Unlike a Status, a Category may simply be cleared — a task is allowed
        // to have none.
        Replace(CategoryReassignOptions, new[] { new CategoryOption(null, "Clear it") }
            .Concat(_session.Snapshot.Config.Categories
                .Where(category => category.Id != row.Id)
                .Select(category => new CategoryOption(category.Id, $"Move to {category.Name}"))));

        CategoryReassignTarget = CategoryReassignOptions.FirstOrDefault();
        CategoryPendingDelete = row;
    }

    [RelayCommand]
    private void CancelDeleteCategory() => CategoryPendingDelete = null;

    [RelayCommand]
    private async Task ConfirmDeleteCategoryAsync()
    {
        if (CategoryPendingDelete is not { } row) return;

        var target = CategoryReassignTarget?.Id;
        CategoryPendingDelete = null;
        await Guarded(() => _session.DeleteCategoryAsync(row.Id, target));
    }

    // ---- tags --------------------------------------------------------------

    [RelayCommand]
    private Task AddTagAsync() => Guarded(async () =>
    {
        var name = NewTagName.Trim();
        if (name.Length == 0) return;

        NewTagName = "";
        await _session.AddTagAsync(name);
    });

    [RelayCommand]
    private Task RenameTagAsync(TagRowViewModel? row) => Guarded(async () =>
    {
        if (_loading || row is null) return;

        var tag = _session.Snapshot.Config.TagById(row.Id);
        var name = row.Name.Trim();
        if (tag is null || name.Length == 0 || tag.Name == name) return;

        await _session.UpdateTagAsync(tag with { Name = name });
    });

    /// <summary>
    /// Deletes a tag outright. No prompt, unlike Statuses and Categories: tags
    /// are deliberately lightweight and detaching one loses nothing else.
    /// </summary>
    [RelayCommand]
    private async Task DeleteTagAsync(TagRowViewModel? row)
    {
        if (row is null) return;

        await Guarded(() => _session.DeleteTagAsync(row.Id));
    }

    // ---- plumbing ----------------------------------------------------------

    /// <summary>
    /// Rebuilds this window from a snapshot that may have arrived on any thread.
    /// </summary>
    /// <remarks>
    /// <see cref="WorkspaceSession.Changed"/> is raised on whichever thread
    /// finished the work, and for a rescan that is a background one. Everything
    /// below rebuilds collections this window is bound to, so it has to be put
    /// back on the UI thread first — without this, a sync client touching a file
    /// while the settings window is open mutates a live <c>ItemsControl</c> from
    /// the threadpool.
    /// </remarks>
    private void OnWorkspaceChanged(object? sender, WorkspaceChangedEventArgs e) =>
        OnUiThread(() =>
        {
            Refresh();

            // Trashing from the detail pane while this window is open should show
            // up here, and the trash lives on disk, not in the snapshot.
            RefreshTrashInBackground();
        });

    // ---- trash -------------------------------------------------------------

    /// <summary>
    /// Guards overlapping refreshes: the Changed handler and an explicit
    /// command can run at once, and interleaved Clear/Add duplicates rows.
    /// Only the newest call gets to write.
    /// </summary>
    /// <remarks>
    /// Stamped through <see cref="Interlocked"/> because the two callers are not
    /// on the same thread — a rescan arrives on the threadpool, a command on the
    /// UI thread — and a plain <c>++</c> is not one operation.
    /// </remarks>
    private int _trashRefreshStamp;

    /// <summary>
    /// Reloads the trash without a caller to fail to. See <see cref="Guarded"/>.
    /// </summary>
    private void RefreshTrashInBackground() => PendingTrashRefresh = RefreshTrashAsync();

    /// <summary>The trash reload in flight, for tests to await.</summary>
    public Task PendingTrashRefresh { get; private set; } = Task.CompletedTask;

    [RelayCommand]
    private Task RefreshTrashAsync() => Guarded(async () =>
    {
        var stamp = Interlocked.Increment(ref _trashRefreshStamp);
        var tasks = await _session.LoadTrashAsync();

        // Reading the trash is I/O and finishes on whichever thread carried it,
        // so the rows go back on the UI thread before they reach the window.
        OnUiThread(() =>
        {
            if (_disposed || stamp != Volatile.Read(ref _trashRefreshStamp)) return;

            var config = _session.Snapshot.Config;

            Replace(TrashedTasks, tasks
                .OrderBy(task => task.Summary, StringComparer.CurrentCultureIgnoreCase)
                .Select(task => new TrashRowViewModel(task, config)));
        });
    });

    [RelayCommand]
    private Task RestoreTaskAsync(TrashRowViewModel? row) => Guarded(async () =>
    {
        if (row is null) return;

        await _session.RestoreTaskAsync(row.Id);
        await RefreshTrashAsync();
    });

    private void Refresh()
    {
        if (_disposed) return;

        _loading = true;
        try
        {
            var snapshot = _session.Snapshot;
            var config = snapshot.Config;

            Replace(Statuses, config.Statuses.Select(status => new StatusRowViewModel(
                status,
                isDefault: status.Id == config.DefaultStatusId,
                taskCount: snapshot.TasksUsingStatus(status.Id),
                blocker: _session.StatusDeletionBlockerFor(status.Id))));

            Replace(Categories, config.Categories.Select(category =>
                new CategoryRowViewModel(category, snapshot.TasksUsingCategory(category.Id))));

            Replace(Tags, config.Tags.Select(tag =>
                new TagRowViewModel(tag, snapshot.TasksUsingTag(tag.Id))));
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Runs a command that writes to the workspace, showing what went wrong
    /// rather than letting it escape.
    /// </summary>
    /// <remarks>
    /// Every command on this page goes through here, not only the ones the
    /// domain can refuse. An <c>AsyncRelayCommand</c> rethrows onto the UI
    /// thread, so anything not caught here is an unhandled exception and the end
    /// of the process — and the workspace lives in a folder that can be
    /// unmounted, filled, or made read-only while the window is open, which is
    /// the ordinary case rather than the exotic one.
    /// <para>
    /// A refusal the domain states — deleting the default status, reassigning to
    /// one that does not exist — is already written for the user, so its message
    /// is shown as it stands. Anything else is a surprise and is labelled as
    /// one, because "Cannot delete this status: IsDefault" and "The device is
    /// not ready" want reading very differently.
    /// </para>
    /// </remarks>
    private async Task Guarded(Func<Task> action)
    {
        try
        {
            Error = null;
            await action();
        }
        catch (Exception e) when (IsShutdown(e))
        {
            // The workspace is closing. That is what the user asked for.
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            OnUiThread(() => Error = e.Message);
        }
        catch (PartiallyAppliedException e)
        {
            // Also written for the user, and its whole point is that part of the
            // change *was* saved — "could not be saved" would be wrong.
            OnUiThread(() => Error = e.Message);
        }
        catch (Exception e)
        {
            OnUiThread(() => Error = $"That change could not be saved: {e.Message}");
        }
    }

    private static bool TryParseColor(string value, out uint color) =>
        uint.TryParse(
            value.Trim().TrimStart('#'),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out color);

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Changed -= OnWorkspaceChanged;
    }
}

public sealed partial class StatusRowViewModel : ObservableObject
{
    [ObservableProperty] private string _name;
    [ObservableProperty] private StatusType _type;
    [ObservableProperty] private bool _hiddenFromBoard;

    public StatusRowViewModel(
        Status status, bool isDefault, int taskCount, StatusDeletionBlocker blocker)
    {
        Id = status.Id;
        _name = status.Name;
        _type = status.Type;
        _hiddenFromBoard = status.HiddenFromBoard;
        IsDefault = isDefault;
        TaskCount = taskCount;

        // The reason arrives as a value; the wording is a UI concern.
        BlockerMessage = blocker switch
        {
            StatusDeletionBlocker.None => null,
            StatusDeletionBlocker.IsDefault =>
                "This is the status new tasks start in. Make another Initial status the default first.",
            StatusDeletionBlocker.LastOfItsType =>
                $"This is the only {status.Type.Label()} status, and every workspace needs at least one of each.",
            _ => "That status no longer exists.",
        };
    }

    public string Id { get; }
    public bool IsDefault { get; }
    public int TaskCount { get; }
    public string? BlockerMessage { get; }

    public bool CanDelete => BlockerMessage is null;
    public bool CanMakeDefault => !IsDefault && Type == StatusType.Initial;
    public string TaskCountLabel => TaskCount == 1 ? "1 task" : $"{TaskCount} tasks";
}

public sealed partial class CategoryRowViewModel : ObservableObject
{
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _colorHex;

    public CategoryRowViewModel(Category category, int taskCount)
    {
        Id = category.Id;
        _name = category.Name;
        _colorHex = category.Color.ToString("X8", CultureInfo.InvariantCulture);
        TaskCount = taskCount;
    }

    public string Id { get; }
    public int TaskCount { get; }
    public string TaskCountLabel => TaskCount == 1 ? "1 task" : $"{TaskCount} tasks";
}

public sealed partial class TagRowViewModel : ObservableObject
{
    [ObservableProperty] private string _name;

    public TagRowViewModel(Tag tag, int taskCount)
    {
        Id = tag.Id;
        _name = tag.Name;
        TaskCount = taskCount;
    }

    public string Id { get; }
    public int TaskCount { get; }
    public string TaskCountLabel => TaskCount == 1 ? "1 task" : $"{TaskCount} tasks";
}

/// <summary>One task in the trash, as the settings window lists it.</summary>
public sealed class TrashRowViewModel(MightDoTask task, WorkspaceConfig config)
{
    public string Id { get; } = task.Id;

    public string Summary { get; } = task.Summary;

    /// <summary>The status may itself have been deleted since.</summary>
    public string StatusName { get; } =
        config.StatusById(task.StatusId)?.Name ?? "Deleted status";
}

