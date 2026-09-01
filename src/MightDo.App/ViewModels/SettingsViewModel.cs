using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MightDo.Core.Domain;
using MightDo.Core.Interchange;
using MightDo.Core.Session;
using MightDo.Platform;

namespace MightDo.App.ViewModels;

/// <summary>Asks the user where to write a file. Implemented by the view layer.</summary>
public interface IFileSaver
{
    Task<string?> PickSaveFileAsync(string title, string suggestedName);
}

/// <summary>
/// What Export would write, as the list view currently stands.
/// </summary>
/// <remarks>
/// Exactly the rows the list is showing, in the order it is showing them — not
/// "all tasks", because a user who has filtered to one category and clicks
/// Export means that category, and not a second filter UI either, because one
/// that disagreed with the app's own would be worse than either.
/// </remarks>
/// <param name="IsFiltered">
/// Whether the query narrowed anything, so the button can say which it is
/// rather than leaving the user to discover it by opening the file.
/// </param>
public sealed record ExportSelection(
    IReadOnlyList<MightDoTask> Tasks, bool IsFiltered, string SuggestedName);

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
    private readonly IFilePicker? _filePicker;
    private readonly IFileSaver? _fileSaver;
    private readonly Func<ExportSelection>? _exportSelection;
    private bool _loading;
    private bool _disposed;

    [ObservableProperty] private string _newStatusName = "";
    [ObservableProperty] private StatusType _newStatusType = StatusType.Active;
    [ObservableProperty] private string _newCategoryName = "";
    [ObservableProperty] private CategoryColor _newCategoryColor = Category.Palette[0];
    [ObservableProperty] private string _newTagName = "";
    [ObservableProperty] private string? _error;

    /// <summary>The row awaiting a "where should its tasks go?" answer, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingStatusDelete))]
    private StatusRowViewModel? _statusPendingDelete;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingCategoryDelete))]
    private CategoryRowViewModel? _categoryPendingDelete;

    /// <summary>The plan awaiting a yes or no, if any. Nothing is written until it gets one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewingImport))]
    [NotifyPropertyChangedFor(nameof(ImportSummary))]
    [NotifyPropertyChangedFor(nameof(ImportRemovalWarning))]
    [NotifyPropertyChangedFor(nameof(HasImportRemovals))]
    [NotifyPropertyChangedFor(nameof(ImportNewNames))]
    [NotifyPropertyChangedFor(nameof(HasImportNewNames))]
    [NotifyPropertyChangedFor(nameof(HasImportErrors))]
    private ImportPlan? _pendingImport;

    [ObservableProperty] private string _importFileName = "";

    /// <summary>
    /// Whether unknown categories and tags should be created. On by default.
    /// </summary>
    /// <remarks>
    /// Read when the file is planned, so changing it re-plans rather than
    /// silently applying to a preview the user is no longer looking at.
    /// </remarks>
    [ObservableProperty] private bool _createCategoriesAndTags = true;

    [ObservableProperty] private string? _importResult;

    [ObservableProperty] private StatusOption? _statusReassignTarget;
    [ObservableProperty] private CategoryOption? _categoryReassignTarget;

    /// <param name="filePicker">Null leaves Import unavailable, as the designer's copy of this page is.</param>
    /// <param name="fileSaver">Null leaves Export unavailable, for the same reason.</param>
    /// <param name="exportSelection">
    /// What the list view is showing, asked for at the moment Export is pressed
    /// rather than held here — the filter is view state, and it moves.
    /// </param>
    public SettingsViewModel(
        WorkspaceSession session,
        AppSettings settings,
        IFilePicker? filePicker = null,
        IFileSaver? fileSaver = null,
        Func<ExportSelection>? exportSelection = null)
    {
        _session = session;
        _settings = settings;
        _filePicker = filePicker;
        _fileSaver = fileSaver;
        _exportSelection = exportSelection;
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

    /// <summary>The colours a new category may be given.</summary>
    public IReadOnlyList<CategoryColor> CategoryColors { get; } = Category.Palette;

    public bool IsConfirmingStatusDelete => StatusPendingDelete is not null;
    public bool IsConfirmingCategoryDelete => CategoryPendingDelete is not null;

    public ObservableCollection<string> ImportErrors { get; } = [];

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

    /// <summary>
    /// The design theme — the whole look, as opposed to which of its two
    /// schemes is showing. Machine-local, like the scheme.
    /// </summary>
    public DesignTheme Design => _settings.Design;

    public bool IsCyrkDesign => Design == DesignTheme.Cyrk66;

    public bool IsSageDesign => Design == DesignTheme.SageSlate;

    /// <summary>
    /// Chooses a design theme, wears it and remembers it.
    /// </summary>
    /// <remarks>
    /// The scheme is re-applied afterwards because the incoming theme brings
    /// its own light and dark dictionaries, and swapping the styles is what
    /// tells the application to go and read them again.
    /// </remarks>
    [RelayCommand]
    private void SetDesign(DesignTheme design)
    {
        if (Design == design) return;

        _settings.SetDesign(design);
        MightDo.App.Theme.ApplyDesign(design);
        MightDo.App.Theme.Apply(_settings.Theme);

        OnPropertyChanged(nameof(Design));
        OnPropertyChanged(nameof(IsCyrkDesign));
        OnPropertyChanged(nameof(IsSageDesign));
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

    /// <summary>Retypes a status, or puts the drop-down back if the workspace refuses.</summary>
    /// <remarks>
    /// A refusal is the ordinary case here, not the exotic one — the last
    /// Initial status cannot stop being Initial — and a combo box left showing
    /// the type the user asked for would then be the only thing on screen
    /// claiming the change happened.
    /// </remarks>
    [RelayCommand]
    private async Task SetStatusTypeAsync(StatusRowViewModel? row)
    {
        if (_loading || row is null) return;

        var status = _session.Snapshot.Config.StatusById(row.Id);
        if (status is null || status.Type == row.Type) return;

        await Guarded(() => _session.UpdateStatusAsync(status with { Type = row.Type }));

        // Putting it back re-enters this command, which then finds nothing to
        // do and stops.
        row.Type = _session.Snapshot.Config.StatusById(row.Id)?.Type ?? row.Type;
    }

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

        NewCategoryName = "";
        await _session.AddCategoryAsync(name, NewCategoryColor.Value);
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

    /// <summary>Recolours a category, or puts the swatch back if the write fails.</summary>
    [RelayCommand]
    private async Task SetCategoryColorAsync(CategoryRowViewModel? row)
    {
        if (_loading || row is null) return;

        var category = _session.Snapshot.Config.CategoryById(row.Id);
        if (category is null || category.Color == row.SelectedColor.Value) return;

        await Guarded(() => _session.UpdateCategoryAsync(
            category with { Color = row.SelectedColor.Value }));

        row.ShowColor(_session.Snapshot.Config.CategoryById(row.Id)?.Color);
    }

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

    // ---- import and export -------------------------------------------------

    public bool CanExport => _fileSaver is not null && _exportSelection is not null;

    public bool CanImport => _filePicker is not null;

    /// <summary>
    /// What Export would write, said out loud, so nobody discovers the filter
    /// applied by opening the file.
    /// </summary>
    public string ExportLabel
    {
        get
        {
            if (_exportSelection is null) return "Export tasks to CSV";

            var selection = _exportSelection();
            return selection.IsFiltered
                ? $"Export {Count(selection.Tasks.Count)} (filtered)"
                : $"Export all {Count(selection.Tasks.Count)}";
        }
    }

    public bool IsPreviewingImport => PendingImport is not null;

    public bool HasImportErrors => PendingImport is { Errors.Count: > 0 };

    public bool HasImportNewNames =>
        PendingImport is { } plan && (plan.NewCategories.Count > 0 || plan.NewTags.Count > 0);

    public bool HasImportRemovals =>
        PendingImport is { } plan && (plan.NotesRemoved > 0 || plan.StepsRemoved > 0);

    public string ImportSummary => PendingImport is not { } plan
        ? ""
        : $"Create {plan.CreateCount} · Update {plan.UpdateCount} · "
          + $"Unchanged {plan.UnchangedCount} · Errors {plan.Errors.Count}";

    public string ImportNewNames => PendingImport is not { } plan
        ? ""
        : $"Also creates {Phrase((plan.NewCategories.Count, "category", "categories"), (plan.NewTags.Count, "tag", "tags"))}.";

    /// <summary>
    /// The one irreversible thing an import does that a user could easily not
    /// have meant — a spreadsheet that truncated a multi-line cell shows up here.
    /// </summary>
    public string ImportRemovalWarning => PendingImport is not { } plan
        ? ""
        : $"Removes {Phrase((plan.NotesRemoved, "note", "notes"), (plan.StepsRemoved, "step", "steps"))} from existing tasks.";

    /// <summary>
    /// Writes what the list view is showing to a file the user chooses.
    /// </summary>
    /// <remarks>
    /// Not a backup: the workspace folder is the backup (ADR-0001), and a round
    /// trip through CSV loses attachments, fired reminders and the board
    /// positions of tasks it creates. The hint text beside the button says so.
    /// </remarks>
    [RelayCommand]
    private Task ExportAsync() => Guarded(async () =>
    {
        if (_fileSaver is null || _exportSelection is null) return;

        ImportResult = null;
        var selection = _exportSelection();
        var path = await _fileSaver.PickSaveFileAsync("Export tasks", selection.SuggestedName);

        // Cancelling the picker writes nothing, and says nothing either.
        if (path is null) return;

        await TaskCsv.WriteFileAsync(path, selection.Tasks, _session.Snapshot.Config);
        OnUiThread(() => ImportResult = $"Exported {Count(selection.Tasks.Count)} to {Path.GetFileName(path)}.");
    });

    /// <summary>
    /// Reads a file and works out what it would do. Writes nothing.
    /// </summary>
    [RelayCommand]
    private Task ChooseImportFileAsync() => Guarded(async () =>
    {
        if (_filePicker is null) return;

        var path = await _filePicker.PickFileAsync("Import tasks", "CSV files", "csv");
        if (path is null) return;

        await PreviewAsync(path);
    });

    private async Task PreviewAsync(string path)
    {
        var csv = await TaskCsv.ReadFileAsync(path);
        var plan = await _session.PlanImportAsync(
            csv, new ImportOptions(CreateCategoriesAndTags));

        OnUiThread(() =>
        {
            ImportResult = null;
            ImportFileName = Path.GetFileName(path);
            _importPath = path;
            PendingImport = plan;

            Replace(
                ImportErrors,
                plan.Errors.Select(error => $"line {error.Line} — {error.Column} — {error.Message}"));
        });
    }

    /// <summary>The file being previewed, so the option checkbox can re-plan it.</summary>
    private string? _importPath;

    partial void OnCreateCategoriesAndTagsChanged(bool value)
    {
        if (_importPath is null) return;

        _pending.Add(Guarded(() => PreviewAsync(_importPath)));
    }

    [RelayCommand]
    private Task ApplyImportAsync() => Guarded(async () =>
    {
        if (PendingImport is not { } plan) return;

        var outcome = await _session.ImportAsync(plan);

        OnUiThread(() =>
        {
            CancelImport();
            ImportResult =
                $"Imported: {outcome.Created} created, {outcome.Updated} updated, "
                + $"{outcome.Unchanged} already up to date.";
        });
    });

    [RelayCommand]
    private void CancelImport()
    {
        PendingImport = null;
        _importPath = null;
        ImportFileName = "";
        ImportErrors.Clear();
    }

    private static string Count(int value, string one = "task", string? many = null) =>
        value == 1 ? $"1 {one}" : $"{value} {many ?? one + "s"}";

    /// <summary>"2 categories and 5 tags", leaving out whichever is none.</summary>
    private static string Phrase(params (int Value, string One, string Many)[] parts) =>
        string.Join(
            " and ",
            parts.Where(part => part.Value > 0).Select(part => Count(part.Value, part.One, part.Many)));

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
    private void RefreshTrashInBackground() => _pending.Add(RefreshTrashAsync());

    /// <summary>Work this page started without a caller to await it, for tests to await.</summary>
    public Task PendingWork => _pending.All;

    private readonly PendingWork _pending = new();

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
                blocker: _session.StatusDeletionBlockerFor(status.Id),
                typeChosen: row => SetStatusTypeCommand.Execute(row))));

            Replace(Categories, config.Categories.Select(category =>
                new CategoryRowViewModel(
                    category,
                    snapshot.TasksUsingCategory(category.Id),
                    row => SetCategoryColorCommand.Execute(row))));

            Replace(Tags, config.Tags.Select(tag =>
                new TagRowViewModel(tag, snapshot.TasksUsingTag(tag.Id))));

            // The count on the Export button is a fact about the list view, and
            // adding or trashing a task changes it while this window is open.
            OnPropertyChanged(nameof(ExportLabel));
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

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    /// <summary>
    /// Whether this page has let go of its session.
    /// </summary>
    /// <remarks>
    /// The page is owned by the workspace it was opened on and is disposed with
    /// it, so this is how that ownership can be asserted from outside.
    /// </remarks>
    public bool IsDisposed => _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Changed -= OnWorkspaceChanged;
    }
}

public sealed partial class StatusRowViewModel : ObservableObject
{
    private readonly Action<StatusRowViewModel>? _typeChosen;

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _hiddenFromBoard;

    /// <summary>
    /// Which of the three types this status is, chosen from a drop-down.
    /// </summary>
    /// <remarks>
    /// Saved the moment it changes rather than on losing focus, as the name is:
    /// picking from a closed list of three is the whole gesture.
    /// </remarks>
    [ObservableProperty] private StatusType _type;

    /// <param name="typeChosen">
    /// Called when the user picks a different type. Null in a row nobody can
    /// edit, as the designer's copy of this page is.
    /// </param>
    public StatusRowViewModel(
        Status status,
        bool isDefault,
        int taskCount,
        StatusDeletionBlocker blocker,
        Action<StatusRowViewModel>? typeChosen = null)
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

        // Last, so building the row is not itself a change to save.
        _typeChosen = typeChosen;
    }

    partial void OnTypeChanged(StatusType value)
    {
        _typeChosen?.Invoke(this);
        OnPropertyChanged(nameof(CanMakeDefault));
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
    private readonly Action<CategoryRowViewModel>? _colorChosen;

    [ObservableProperty] private string _name;

    /// <summary>
    /// The colour this category is drawn in, chosen from <see cref="ColorOptions"/>.
    /// </summary>
    /// <remarks>
    /// Saved the moment it changes rather than on losing focus, as the name is:
    /// picking from a list is the whole gesture, and there is nothing further
    /// for the user to finish typing.
    /// </remarks>
    [ObservableProperty] private CategoryColor _selectedColor;

    /// <param name="colorChosen">
    /// Called when the user picks a different colour. Null in a row nobody can
    /// edit, as the designer's copy of this page is.
    /// </param>
    public CategoryRowViewModel(
        Category category, int taskCount, Action<CategoryRowViewModel>? colorChosen = null)
    {
        Id = category.Id;
        _name = category.Name;
        TaskCount = taskCount;

        // A colour the palette has never offered still has to appear in the
        // list, or selecting nothing would silently repaint the category the
        // moment the user touched any other field.
        var current = Category.PaletteEntry(category.Color);
        ColorOptions = current is null
            ? [.. Category.Palette, new CategoryColor("Custom", category.Color)]
            : Category.Palette;
        _selectedColor = current ?? ColorOptions[^1];

        // Last, so building the row is not itself a change to save.
        _colorChosen = colorChosen;
    }

    /// <summary>The colours offered for this category.</summary>
    public IReadOnlyList<CategoryColor> ColorOptions { get; }

    partial void OnSelectedColorChanged(CategoryColor value) => _colorChosen?.Invoke(this);

    /// <summary>
    /// Shows the colour the workspace actually holds, after a change it refused
    /// or could not write. A colour it no longer holds leaves the row alone —
    /// the category has been deleted and the row is on its way out.
    /// </summary>
    public void ShowColor(uint? color)
    {
        if (color is not { } stored || stored == SelectedColor.Value) return;

        SelectedColor = ColorOptions.FirstOrDefault(option => option.Value == stored)
                        ?? SelectedColor;
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

