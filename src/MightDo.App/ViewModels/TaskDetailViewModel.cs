using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MightDo.Core.Domain;
using MightDo.Core.Session;

namespace MightDo.App.ViewModels;

/// <summary>Asks the user for a file. Implemented by the view layer.</summary>
public interface IFilePicker
{
    Task<string?> PickFileAsync(string title);
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

    public TaskDetailViewModel(WorkspaceSession session, MightDoTask task, IFilePicker filePicker)
    {
        _session = session;
        _filePicker = filePicker;
        TaskId = task.Id;
        Refresh(task);
    }

    public string TaskId { get; }

    /// <summary>
    /// The most recent scalar save, so a caller that needs the write to have
    /// landed can wait for it. Completes even when the save failed.
    /// </summary>
    public Task PendingSave { get; private set; } = Task.CompletedTask;

    public ObservableCollection<StatusOption> Statuses { get; } = [];
    public ObservableCollection<CategoryOption> Categories { get; } = [];
    public ObservableCollection<StepViewModel> Steps { get; } = [];
    public ObservableCollection<NoteViewModel> Notes { get; } = [];
    public ObservableCollection<ReminderViewModel> Reminders { get; } = [];
    public ObservableCollection<AttachmentViewModel> Attachments { get; } = [];

    public IReadOnlyList<Priority> Priorities { get; } = Enum.GetValues<Priority>();

    /// <summary>Re-reads from the snapshot without raising any writes.</summary>
    public void Refresh(MightDoTask task)
    {
        _loading = true;
        try
        {
            var config = _session.Snapshot.Config;

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

            Replace(Statuses, config.Statuses.Select(s => new StatusOption(s.Id, s.Name)));
            Replace(Categories, new[] { CategoryOption.None }
                .Concat(config.Categories.Select(CategoryOption.For)));
            Replace(Steps, task.Steps.Select(step => new StepViewModel(step)));
            Replace(Notes, task.Notes.Select(note => new NoteViewModel(note)));
            Replace(Reminders, task.Reminders.Select(r => new ReminderViewModel(r)));
            Replace(Attachments, task.Attachments.Select(a => new AttachmentViewModel(a)));
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

    partial void OnSelectedCategoryChanged(CategoryOption? value) =>
        Save(task => task with { CategoryId = value?.Id });

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

        PendingSave = Report(_session.MoveToStatusAsync(task, value.Id));
    }

    [RelayCommand]
    private async Task CommitTagsAsync()
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
    }

    // ---- steps, notes, reminders, attachments ------------------------------

    [RelayCommand]
    private async Task AddStepAsync()
    {
        var text = NewStepText.Trim();
        var task = Current;
        if (text.Length == 0 || task is null) return;

        NewStepText = "";
        await _session.AddStepAsync(task, text);
    }

    [RelayCommand]
    private async Task ToggleStepAsync(StepViewModel? step)
    {
        var task = Current;
        if (step is null || task is null) return;

        await _session.SetStepDoneAsync(task, step.Id, step.Done);
    }

    [RelayCommand]
    private async Task DeleteStepAsync(StepViewModel? step)
    {
        var task = Current;
        if (step is null || task is null) return;

        await _session.DeleteStepAsync(task, step.Id);
    }

    [RelayCommand]
    private async Task AddNoteAsync()
    {
        var body = NewNoteBody.Trim();
        var task = Current;
        if (body.Length == 0 || task is null) return;

        NewNoteBody = "";
        await _session.AddNoteAsync(task, body);
    }

    [RelayCommand]
    private async Task DeleteNoteAsync(NoteViewModel? note)
    {
        var task = Current;
        if (note is null || task is null) return;

        await _session.DeleteNoteAsync(task, note.Id);
    }

    [RelayCommand]
    private async Task AddReminderAsync()
    {
        var task = Current;
        if (task is null || NewReminderDate is not { } date) return;

        var time = NewReminderTime ?? TimeSpan.Zero;
        var local = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Local)
            .Add(time);

        await _session.AddReminderAsync(task, local.ToUniversalTime());
    }

    [RelayCommand]
    private async Task DeleteReminderAsync(ReminderViewModel? reminder)
    {
        var task = Current;
        if (reminder is null || task is null) return;

        await _session.DeleteReminderAsync(task, reminder.Id);
    }

    [RelayCommand]
    private async Task DismissReminderAsync(ReminderViewModel? reminder)
    {
        var task = Current;
        if (reminder is null || task is null) return;

        await _session.DismissRemindersAsync(task, new HashSet<string> { reminder.Id });
    }

    [RelayCommand]
    private async Task AttachFileAsync()
    {
        var task = Current;
        if (task is null) return;

        var path = await _filePicker.PickFileAsync("Attach a file");
        if (path is null) return;

        await _session.AttachFileAsync(task, path);
    }

    [RelayCommand]
    private async Task DeleteAttachmentAsync(AttachmentViewModel? attachment)
    {
        var task = Current;
        if (attachment is null || task is null) return;

        await _session.DeleteAttachmentAsync(task, attachment.Id);
    }

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
    private void Save(Func<MightDoTask, MightDoTask> edit)
    {
        if (_loading) return;

        var task = Current;
        if (task is null) return;

        PendingSave = Report(_session.EditTaskAsync(task, edit));
    }

    private async Task Report(Task write)
    {
        try
        {
            await write;
            SaveError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveError = $"This change could not be saved: {ex.Message}";
        }
    }

    private static int? ParseMinutes(string value) =>
        int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)
        && m >= 0
            ? m
            : null;

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
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

public sealed class AttachmentViewModel(Attachment attachment)
{
    public string Id { get; } = attachment.Id;
    public string OriginalName { get; } = attachment.OriginalName;
    public string Size { get; } = $"{attachment.SizeBytes / 1024.0:F0} KB";
}
