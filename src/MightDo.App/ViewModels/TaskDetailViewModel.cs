using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MightDo.Core.Domain;
using MightDo.Core.Session;

namespace MightDo.App.ViewModels;

/// <summary>Asks the user for a file. Implemented by the view layer.</summary>
public interface IFilePicker
{
    Task<string?> PickFileAsync(string title);

    /// <summary>
    /// The same, offering a file type by name and extension.
    /// </summary>
    /// <remarks>
    /// A default implementation rather than a parameter on the method above, so
    /// the attachment picker — which takes anything — needs no change, and
    /// neither does any of the test doubles that stand in for this.
    /// </remarks>
    Task<string?> PickFileAsync(string title, string typeName, params string[] extensions) =>
        PickFileAsync(title);
}

/// <summary>
/// One task, open for editing.
/// </summary>
/// <remarks>
/// Text fields commit on losing focus rather than on every keystroke — each
/// commit is a file write, and ADR-0001 makes that a synced file.
/// <para>
/// A rescan can replace the task underneath this pane at any moment, so
/// <see cref="Refresh"/> re-reads without raising saves. Everything it sets goes
/// through <see cref="_loading"/> so re-reading cannot echo back as a write.
/// </para>
/// </remarks>
public sealed partial class TaskDetailViewModel : ViewModelBase
{
    private readonly WorkspaceSession _session;
    private readonly IFilePicker _filePicker;
    private bool _loading;

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private StatusOption? _selectedStatus;
    [ObservableProperty] private Priority _selectedPriority;
    [ObservableProperty] private CategoryOption? _selectedCategory;
    [ObservableProperty] private DateTime? _dueDate;
    [ObservableProperty] private string _estimateMinutes = "";
    [ObservableProperty] private string _totalTimeMinutes = "";
    [ObservableProperty] private string _tagNames = "";
    [ObservableProperty] private string _newStepText = "";
    [ObservableProperty] private string _newNoteBody = "";
    [ObservableProperty] private DateTime? _newReminderDate = DateTime.Today;
    [ObservableProperty] private TimeSpan? _newReminderTime = new TimeSpan(9, 0, 0);
    [ObservableProperty] private string? _completedLabel;
    [ObservableProperty] private string _varianceLabel = "";

    /// <summary>Set when a save failed, so the pane can say so.</summary>
    [ObservableProperty] private string? _saveError;

    /// <summary>
    /// A file waiting for the user to agree to its size. See
    /// <see cref="LargeAttachmentBytes"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingAttachment))]
    private string? _attachmentAwaitingConfirmation;

    /// <summary>What the confirmation asks, naming the file and its size.</summary>
    [ObservableProperty] private string? _attachmentConfirmation;

    /// <summary>How far the copy in flight has got, as a fraction of the file.</summary>
    [ObservableProperty] private double _attachmentProgress;

    /// <summary>What the copy in flight is doing, or null when none is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAttaching))]
    private string? _attachmentStatus;

    /// <summary>Cancels the copy in flight. Null when nothing is copying.</summary>
    private CancellationTokenSource? _attaching;

    public TaskDetailViewModel(WorkspaceSession session, MightDoTask task, IFilePicker filePicker)
    {
        _session = session;
        _filePicker = filePicker;
        TaskId = task.Id;
        Refresh(task);
    }

    /// <summary>
    /// The size at which attaching asks first rather than simply doing it.
    /// </summary>
    /// <remarks>
    /// The workspace is a folder the user is deliberately syncing to a cloud
    /// provider (ADR-0001), so attaching is not only a copy — it is an upload,
    /// on their connection, out of their quota. Fifty megabytes is where an
    /// attachment stops being a document and starts being a video, an archive
    /// or a disk image: past it, asking first is worth the interruption, and
    /// below it the question would only ever be in the way.
    /// </remarks>
    public const long LargeAttachmentBytes = 50L * 1024 * 1024;

    /// <summary>Whether a file is waiting for the user to agree to its size.</summary>
    public bool IsConfirmingAttachment => AttachmentAwaitingConfirmation is not null;

    /// <summary>Whether a copy is in flight.</summary>
    public bool IsAttaching => AttachmentStatus is not null;

    /// <summary>The task the pane is showing.</summary>
    /// <remarks>
    /// Not fixed for the pane's lifetime: opening another task re-points this
    /// one rather than replacing it. See <see cref="Refresh"/>.
    /// </remarks>
    public string TaskId { get; private set; }

    /// <summary>
    /// Every save this pane still has in flight, so a caller that needs a write
    /// to have landed can wait for it. Completes even when a save failed.
    /// </summary>
    /// <remarks>
    /// All of them rather than the latest: a control raises its change as the
    /// user makes it, so two quick edits are two writes, and a single slot would
    /// forget the first one and hand a waiter the wrong write to wait on.
    /// </remarks>
    public Task PendingSave => _pending.All;

    private readonly PendingWork _pending = new();

    public ObservableCollection<StatusOption> Statuses { get; } = [];
    public ObservableCollection<CategoryOption> Categories { get; } = [];
    public ObservableCollection<StepViewModel> Steps { get; } = [];
    public ObservableCollection<NoteViewModel> Notes { get; } = [];
    public ObservableCollection<ReminderViewModel> Reminders { get; } = [];
    public ObservableCollection<AttachmentViewModel> Attachments { get; } = [];

    public IReadOnlyList<Priority> Priorities { get; } = Enum.GetValues<Priority>();

    /// <summary>
    /// Points the pane at a task and reads it, without raising any writes.
    /// </summary>
    /// <remarks>
    /// Used both for a rescan of the open task and for opening another one. The
    /// second is why the pane is re-pointed rather than replaced: a control
    /// bound to a brand new view model re-reads its bindings in an order it
    /// chooses, and a ComboBox whose items compare equal across the two keeps
    /// the selection it already had and writes it back — so opening a task set
    /// its status to the previously open task's. Same view model, same
    /// bindings, and everything set here is set under <see cref="_loading"/>.
    /// </remarks>
    public void Refresh(MightDoTask task)
    {
        _loading = true;
        try
        {
            var config = _session.Snapshot.Config;

            if (TaskId != task.Id)
            {
                TaskId = task.Id;

                // Half-typed drafts, an unanswered question and a failure belong
                // to the task they were about, not to the one being opened. A
                // copy already in flight is left alone: it is attaching to the
                // task it was started on, which is still the task it names.
                NewStepText = "";
                NewNoteBody = "";
                SaveError = null;
                AttachmentAwaitingConfirmation = null;
                AttachmentConfirmation = null;
            }

            // The option lists come first, and the selections after. A dropdown
            // whose items are rebuilt reports its selection as null on the way
            // past, so a selection set beforehand is wiped out by the rebuild
            // and the field is left blank — which reads as the edit not having
            // been saved. See the comment on Replace.
            Replace(Statuses, config.Statuses.Select(s => new StatusOption(s.Id, s.Name)));
            Replace(Categories, new[] { CategoryOption.None }
                .Concat(config.Categories.Select(CategoryOption.For)));

            Summary = task.Summary;
            Description = task.Description;
            SelectedStatus = config.StatusById(task.StatusId) is { } status
                ? new StatusOption(status.Id, status.Name)
                : null;
            SelectedPriority = task.Priority;
            SelectedCategory = CategoryOption.For(config.CategoryById(task.CategoryId));
            DueDate = task.DueDate is { } due
                ? new DateTime(due.Year, due.Month, due.Day, 0, 0, 0, DateTimeKind.Unspecified)
                : null;
            EstimateMinutes = task.EstimateMinutes?.ToString(CultureInfo.InvariantCulture) ?? "";
            TotalTimeMinutes = task.TotalTimeMinutes?.ToString(CultureInfo.InvariantCulture) ?? "";
            TagNames = string.Join(", ", config.TagsByIds(task.TagIds).Select(tag => tag.Name));

            // The completion date is the application's, not the user's: shown,
            // never edited. See ADR-0002.
            CompletedLabel = task.CompletedAt is { } completed
                ? $"Completed {completed.ToLocalTime():g}"
                : null;

            VarianceLabel = task.EstimateVariance is { } variance
                ? variance == 0
                    ? "Exactly as estimated"
                    : $"{Math.Abs(variance)} min {(variance > 0 ? "over" : "under")} estimate"
                : "";

            Replace(Steps, task.Steps.Select(step => new StepViewModel(step)));
            Replace(Notes, task.Notes.Select(note => new NoteViewModel(note)));
            Replace(Reminders, task.Reminders.Select(r => new ReminderViewModel(r)));
            Replace(Attachments, task.Attachments.Select(a => new AttachmentViewModel(
                a, File.Exists(_session.Workspace.AttachmentFile(a.StoredName)))));
        }
        finally
        {
            _loading = false;
        }
    }

    private MightDoTask? Current => _session.Snapshot.TaskById(TaskId);

    // ---- scalar edits ------------------------------------------------------

    partial void OnSummaryChanged(string value) => Save(task =>
        value.Trim().Length == 0 ? task : task with { Summary = value.Trim() });

    partial void OnDescriptionChanged(string value) =>
        Save(task => task with { Description = value });

    partial void OnSelectedPriorityChanged(Priority value) =>
        Save(task => task with { Priority = value });

    /// <summary>
    /// A null category is a dropdown with nothing selected, not a user choice:
    /// clearing a task's category is picking <see cref="CategoryOption.None"/>,
    /// which is an option in the list and carries a null id. Only a ComboBox
    /// that has emptied itself reports null, and that must not be written.
    /// </summary>
    partial void OnSelectedCategoryChanged(CategoryOption? value)
    {
        if (value is null) return;

        Save(task => task with { CategoryId = value.Id });
    }

    partial void OnDueDateChanged(DateTime? value) => Save(task => task with
    {
        // A due date is a day. Take the calendar components straight across
        // rather than converting an instant, which would shift it a day in
        // some zones.
        DueDate = value is { } date ? new CalendarDate(date.Year, date.Month, date.Day) : null,
    });

    partial void OnEstimateMinutesChanged(string value) =>
        Save(task => task with { EstimateMinutes = ParseMinutes(value) });

    partial void OnTotalTimeMinutesChanged(string value) =>
        Save(task => task with { TotalTimeMinutes = ParseMinutes(value) });

    /// <summary>
    /// Moving status is not an ordinary field edit — it carries the
    /// completion-date rule, so it goes through the session rather than through
    /// a record update.
    /// </summary>
    partial void OnSelectedStatusChanged(StatusOption? value)
    {
        if (_loading || value is null) return;

        var task = Current;
        if (task is null || task.StatusId == value.Id) return;

        _pending.Add(Report(
            () => _session.MoveToStatusAsync(task, value.Id),
            nameof(OnSelectedStatusChanged),
            task.Id));
    }

    [RelayCommand]
    private Task CommitTagsAsync() => Guarded(async () =>
    {
        if (_loading) return;
        var task = Current;
        if (task is null) return;

        var names = TagNames
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // AddTagAsync returns the existing tag when the name is already taken,
        // so typing a tag someone else made reuses it rather than duplicating.
        var ids = new List<string>(names.Count);
        foreach (var name in names) ids.Add((await _session.AddTagAsync(name)).Id);

        var current = Current;
        if (current is not null) await _session.SetTagsAsync(current, ids);
    });

    // ---- steps, notes, reminders, attachments ------------------------------

    [RelayCommand]
    private Task AddStepAsync() => Guarded(async () =>
    {
        var text = NewStepText.Trim();
        var task = Current;
        if (text.Length == 0 || task is null) return;

        NewStepText = "";
        await _session.AddStepAsync(task, text);
    });

    [RelayCommand]
    private Task ToggleStepAsync(StepViewModel? step) => Guarded(async () =>
    {
        var task = Current;
        if (step is null || task is null) return;

        await _session.SetStepDoneAsync(task, step.Id, step.Done);
    });

    [RelayCommand]
    private Task DeleteStepAsync(StepViewModel? step) => Guarded(async () =>
    {
        var task = Current;
        if (step is null || task is null) return;

        await _session.DeleteStepAsync(task, step.Id);
    });

    [RelayCommand]
    private Task AddNoteAsync() => Guarded(async () =>
    {
        var body = NewNoteBody.Trim();
        var task = Current;
        if (body.Length == 0 || task is null) return;

        NewNoteBody = "";
        await _session.AddNoteAsync(task, body);
    });

    [RelayCommand]
    private Task DeleteNoteAsync(NoteViewModel? note) => Guarded(async () =>
    {
        var task = Current;
        if (note is null || task is null) return;

        await _session.DeleteNoteAsync(task, note.Id);
    });

    [RelayCommand]
    private Task AddReminderAsync() => Guarded(async () =>
    {
        var task = Current;
        if (task is null || NewReminderDate is not { } date) return;

        var time = NewReminderTime ?? TimeSpan.Zero;
        var local = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Local)
            .Add(time);

        await _session.AddReminderAsync(task, local.ToUniversalTime());
    });

    [RelayCommand]
    private Task DeleteReminderAsync(ReminderViewModel? reminder) => Guarded(async () =>
    {
        var task = Current;
        if (reminder is null || task is null) return;

        await _session.DeleteReminderAsync(task, reminder.Id);
    });

    [RelayCommand]
    private Task DismissReminderAsync(ReminderViewModel? reminder) => Guarded(async () =>
    {
        var task = Current;
        if (reminder is null || task is null) return;

        await _session.DismissRemindersAsync(task, new HashSet<string> { reminder.Id });
    });

    [RelayCommand]
    private Task AttachFileAsync() => Guarded(async () =>
    {
        var task = Current;
        if (task is null) return;

        var path = await _filePicker.PickFileAsync("Attach a file");
        if (path is null) return;

        var size = SizeOf(path);
        if (size < LargeAttachmentBytes)
        {
            await CopyAsync(path, size);
            return;
        }

        AttachmentConfirmation =
            $"{Path.GetFileName(path)} is {Format(size)}. A copy that size goes into the "
            + "workspace folder, and from there to wherever it syncs. Attach it anyway?";
        AttachmentAwaitingConfirmation = path;
    });

    [RelayCommand]
    private Task ConfirmAttachmentAsync() => Guarded(async () =>
    {
        if (AttachmentAwaitingConfirmation is not { } path) return;

        AttachmentAwaitingConfirmation = null;
        await CopyAsync(path, SizeOf(path));
    });

    /// <summary>
    /// Backs out of attaching — the question, or the copy already running.
    /// </summary>
    /// <remarks>
    /// One command for both because they are one thing to the user: the button
    /// that says no. Which of the two is showing decides what it cancels, and
    /// the copy leaves nothing behind either way.
    /// </remarks>
    [RelayCommand]
    private void CancelAttachment()
    {
        AttachmentAwaitingConfirmation = null;
        AttachmentConfirmation = null;
        _attaching?.Cancel();
    }

    [RelayCommand]
    private Task DeleteAttachmentAsync(AttachmentViewModel? attachment) => Guarded(async () =>
    {
        var task = Current;
        if (attachment is null || task is null) return;

        await _session.DeleteAttachmentAsync(task, attachment.Id);
    });

    // ---- plumbing ----------------------------------------------------------

    /// <summary>
    /// Hands the change itself to the session rather than a finished record.
    /// </summary>
    /// <remarks>
    /// A control raises its change as the user makes it, so two quick edits are
    /// both built from whatever the pane last read. Passing the edit lets the
    /// session apply it to the task current when the write actually happens, so
    /// the second edit cannot revert the first — including a status move, whose
    /// completion-date rule a whole-record write would undo.
    /// <para>
    /// The write is not awaited here, so <see cref="PendingSave"/> keeps hold of
    /// it: tests await it, and a failure becomes <see cref="SaveError"/> rather
    /// than vanishing.
    /// </para>
    /// </remarks>
    private void Save(
        Func<MightDoTask, MightDoTask> edit,
        [CallerMemberName] string operation = "")
    {
        if (_loading) return;

        var task = Current;
        if (task is null) return;

        _pending.Add(Report(
            () => _session.EditTaskAsync(task, edit), operation, task.Id));
    }

    /// <summary>
    /// Awaits a write and turns whatever it does into <see cref="SaveError"/>.
    /// </summary>
    /// <remarks>
    /// Every failure, not only the two file-system ones this caught first. A
    /// task file can also be refused for a name that no longer addresses
    /// somewhere inside the workspace, or for a schema version this build must
    /// not write over, and neither of those is an <see cref="IOException"/>.
    /// Left uncaught they reached an <c>AsyncRelayCommand</c>, which rethrows
    /// onto the UI thread — so the pane's answer to "this file cannot safely be
    /// written" was to close the application rather than to say so.
    /// <para>
    /// Nothing is marshalled here. Every write starts on the UI thread and its
    /// continuation comes back to it, so this pane stays free of Avalonia and
    /// testable without a dispatcher — unlike the settings page, which is
    /// rebuilt by an event raised on whichever thread finished a rescan.
    /// </para>
    /// </remarks>
    private async Task Report(Func<Task> write, string operation, string taskId)
    {
        using var context = CrashDiagnostics.Begin(operation, taskId);

        try
        {
            await write();
            SaveError = null;
        }
        catch (Exception error) when (IsShutdown(error))
        {
            // The workspace is closing. That is what the user asked for.
        }
        catch (Exception error)
        {
            SaveError = $"This change could not be saved: {error.Message}";
        }
    }

    /// <summary>
    /// Runs a command that writes to the workspace, reporting rather than
    /// throwing. See <see cref="Report"/>.
    /// </summary>
    private Task Guarded(Func<Task> work, [CallerMemberName] string operation = "") =>
        _pending.Add(Report(work, operation, TaskId));

    /// <summary>
    /// Copies a file in, saying how far it has got and staying cancellable.
    /// </summary>
    /// <remarks>
    /// The size is measured rather than taken from the stream so the pane can
    /// show a fraction from the first chunk. A source that has changed size
    /// since it was picked only makes the fraction wrong, which is better than
    /// making the copy wrong.
    /// </remarks>
    private async Task CopyAsync(string path, long size)
    {
        var task = Current;
        if (task is null) return;

        using var cancelling = new CancellationTokenSource();
        _attaching = cancelling;

        var name = Path.GetFileName(path);
        AttachmentProgress = 0;
        AttachmentStatus = $"Copying {name}…";

        // Progress captures the UI thread's context here, which is where this
        // pane does all its work; the report arrives back on it. It is posted
        // rather than delivered, so the last report of a copy can arrive after
        // the copy has finished and put the progress bar back on screen for
        // good — hence the check that this is still the copy in flight.
        var progress = new Progress<long>(copied =>
        {
            if (!ReferenceEquals(_attaching, cancelling)) return;

            AttachmentProgress = size > 0 ? Math.Min(1, (double)copied / size) : 0;
            AttachmentStatus = $"Copying {name}… {AttachmentProgress:P0}";
        });

        try
        {
            await _session.AttachFileAsync(task, path, progress, cancelling.Token);
        }
        catch (OperationCanceledException) when (cancelling.IsCancellationRequested)
        {
            // The user pressed cancel. Nothing was attached, and the bytes that
            // had landed are gone — see TaskStore.CopyAttachmentAsync.
        }
        finally
        {
            _attaching = null;
            AttachmentStatus = null;
            AttachmentConfirmation = null;
            AttachmentProgress = 0;
        }
    }

    private static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Unknown size is not a reason to refuse. The copy itself will fail
            // if the file is genuinely unreachable, and it says so properly.
            return 0;
        }
    }

    private static string Format(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} bytes",
    };

    private static int? ParseMinutes(string value) =>
        int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)
        && m >= 0
            ? m
            : null;

    /// <summary>
    /// Refills a collection in place, leaving it alone when it already holds
    /// exactly these items.
    /// </summary>
    /// <remarks>
    /// The check is not an optimisation. Statuses and Categories rarely change,
    /// but every rescan used to clear and refill them regardless, and a
    /// ComboBox drops its selection the moment its items are cleared. Leaving
    /// an unchanged list untouched keeps the Status and Category dropdowns
    /// showing the task through a rescan.
    /// </remarks>
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var replacement = items as IReadOnlyList<T> ?? [.. items];
        if (target.SequenceEqual(replacement)) return;

        target.Clear();
        foreach (var item in replacement) target.Add(item);
    }
}

/// <summary>A Status as the detail pane's dropdown sees it.</summary>
public sealed record StatusOption(string Id, string Name);

/// <summary>
/// A Category as the dropdown sees it, including the "no category" row —
/// a task has exactly one Category, or none.
/// </summary>
public sealed record CategoryOption(string? Id, string Name)
{
    public static readonly CategoryOption None = new(null, "No category");

    public static CategoryOption For(Category? category) =>
        category is null ? None : new CategoryOption(category.Id, category.Name);
}

public sealed partial class StepViewModel : ObservableObject
{
    [ObservableProperty] private bool _done;

    public StepViewModel(Step step)
    {
        Id = step.Id;
        Text = step.Text;
        _done = step.Done;
    }

    public string Id { get; }
    public string Text { get; }
}

public sealed class NoteViewModel(Note note)
{
    public string Id { get; } = note.Id;
    public string Body { get; } = note.Body;
    public string When { get; } = note.CreatedAt.ToLocalTime().ToString("g");
}

public sealed class ReminderViewModel(Reminder reminder)
{
    public string Id { get; } = reminder.Id;
    public string When { get; } = reminder.RemindAt.ToLocalTime().ToString("g");

    public string State { get; } = reminder switch
    {
        { DismissedAt: not null } => "dismissed",
        { FiredAt: not null } => "fired",
        _ => "pending",
    };

    public bool CanDismiss { get; } = reminder.IsOutstanding;
}

public sealed class AttachmentViewModel(Attachment attachment, bool present)
{
    public string Id { get; } = attachment.Id;
    public string OriginalName { get; } = attachment.OriginalName;

    /// <summary>
    /// The file's size, or why it has none — a record whose bytes are gone
    /// looks exactly like a working attachment until you try to open it, so the
    /// pane says so where the size would be.
    /// </summary>
    public string Size { get; } = present
        ? $"{attachment.SizeBytes / 1024.0:F0} KB"
        : "file missing";

    public bool IsMissing { get; } = !present;
}
