using MightDo.Core.Domain;
using MightDo.Core.Storage;

namespace MightDo.Core.Session;

/// <summary>A reminder that has come due, with the task it belongs to.</summary>
public sealed record DueReminder(MightDoTask Task, Reminder Reminder);

/// <summary>
/// The whole workspace, as of one moment, immutable.
/// </summary>
/// <remarks>
/// Everything lives in memory because the storage model is one small JSON file
/// per task — see ADR-0001. That is comfortable into the low thousands of tasks
/// and is what lets filtering and search stay instant without an index.
/// <para>
/// This is the read side. <see cref="WorkspaceSession"/> is the write side and
/// publishes these. Splitting them means a view can hold a snapshot across a
/// background rescan without tearing, and cannot accidentally mutate the
/// workspace through the same object it renders from.
/// </para>
/// </remarks>
public sealed class WorkspaceSnapshot
{
    private readonly Dictionary<string, MightDoTask> _byId;

    public WorkspaceSnapshot(
        WorkspaceConfig config,
        IReadOnlyList<MightDoTask> tasks,
        IReadOnlyList<TaskLoadFailure> failures,
        IReadOnlyList<ConflictFile> conflicts,
        DateTimeOffset loadedAt)
    {
        Config = config;
        Tasks = tasks;
        Failures = failures;
        Conflicts = conflicts;
        LoadedAt = loadedAt;

        // Derives the key rather than trusting a caller to keep it in step, so
        // "the map key is the task's id" holds by construction.
        _byId = new Dictionary<string, MightDoTask>(tasks.Count, StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            // A case-insensitive volume can hand back two spellings of one
            // filename. Last one wins rather than throwing: a duplicate is a
            // sync artefact, and refusing to open the workspace over it would
            // be a worse failure than showing one of the two.
            _byId[task.Id] = task;
        }
    }

    public static WorkspaceSnapshot From(LoadedWorkspace loaded, DateTimeOffset loadedAt) =>
        new(loaded.Config, loaded.Tasks, loaded.Failures, loaded.Conflicts, loadedAt);

    public WorkspaceConfig Config { get; }

    public IReadOnlyList<MightDoTask> Tasks { get; }

    /// <summary>Task files that couldn't be parsed. Surfaced, never swallowed.</summary>
    public IReadOnlyList<TaskLoadFailure> Failures { get; }

    /// <summary>Files a sync client left in <c>tasks/</c> that we didn't write.</summary>
    public IReadOnlyList<ConflictFile> Conflicts { get; }

    public DateTimeOffset LoadedAt { get; }

    public MightDoTask? TaskById(string? id) =>
        id is not null && _byId.TryGetValue(id, out var task) ? task : null;

    public int TasksUsingStatus(string statusId) =>
        Tasks.Count(task => task.StatusId == statusId);

    public int TasksUsingCategory(string categoryId) =>
        Tasks.Count(task => task.CategoryId == categoryId);

    public int TasksUsingTag(string tagId) =>
        Tasks.Count(task => task.TagIds.Contains(tagId));

    /// <summary>
    /// Reminders that have come due and haven't been acknowledged, newest first.
    /// </summary>
    /// <remarks>
    /// Deliberately ignores whether a reminder already fired an OS notification:
    /// it stays here until dismissed. That is what makes "open the app after two
    /// days away and nothing is missed" work, and per ADR-0004 the in-app
    /// surface is the contract, not the OS banner.
    /// </remarks>
    public IReadOnlyList<DueReminder> OutstandingReminders(DateTime now)
    {
        var moment = now.ToUniversalTime();

        return
        [
            .. Tasks
                .SelectMany(task => task.OutstandingReminders(moment)
                    .Select(reminder => new DueReminder(task, reminder)))
                .OrderByDescending(due => due.Reminder.RemindAt),
        ];
    }

    /// <summary>
    /// Whether this holds the same workspace state as <paramref name="other"/>.
    /// </summary>
    /// <remarks>
    /// Used to decide whether a rescan is worth telling anyone about. ADR-0003
    /// makes reloads frequent and idempotent, so without this a sync client
    /// touching a file would redraw the UI for no reason. Comparing outcomes is
    /// how we avoid that, rather than by suppressing our own writes on a timer —
    /// which would risk swallowing a genuine external change that lands in the
    /// same window.
    /// </remarks>
    public bool HasSameContentAs(WorkspaceSnapshot? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return Config.HasSameContentAs(other.Config)
               && Tasks.Count == other.Tasks.Count
               && Conflicts.SequenceEqual(other.Conflicts)
               && Failures.Count == other.Failures.Count
               && Tasks.All(task => task.HasSameContentAs(other.TaskById(task.Id)));
    }
}
