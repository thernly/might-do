using MightDo.Core.Domain;
using MightDo.Core.Query;
using MightDo.Core.Storage;

namespace MightDo.Core.Session;

public sealed class WorkspaceChangedEventArgs(WorkspaceSnapshot snapshot) : EventArgs
{
    public WorkspaceSnapshot Snapshot { get; } = snapshot;
}

/// <summary>Why a status can't be deleted.</summary>
public enum StatusDeletionBlocker
{
    None,

    /// <summary>No such status.</summary>
    Unknown,

    /// <summary>It is the status new tasks start in.</summary>
    IsDefault,

    /// <summary>It is the last status of its type, and every workspace needs one of each.</summary>
    LastOfItsType,
}

/// <summary>
/// Holds the whole workspace in memory and is the only thing that writes to it.
/// </summary>
/// <remarks>
/// The read side is <see cref="WorkspaceSnapshot"/>, published by
/// <see cref="Changed"/>. Views render from a snapshot and call methods here to
/// change things, which keeps a view from mutating the workspace through the
/// object it draws from.
/// <para>
/// Every mutation and every reload is serialised. The Flutter implementation got
/// that free from Dart's single isolate; .NET does not, and a rescan landing
/// halfway through a cascade would otherwise clobber it.
/// </para>
/// <para>
/// This type knows nothing about <see cref="TaskQuery"/>, watching, or
/// reminders. Wiring those together is the composition root's job.
/// </para>
/// </remarks>
public sealed class WorkspaceSession : IDisposable
{
    private readonly TaskStore _store;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile WorkspaceSnapshot _snapshot;
    private bool _disposed;

    private WorkspaceSession(TaskStore store, TimeProvider time, WorkspaceSnapshot snapshot)
    {
        _store = store;
        _time = time;
        _snapshot = snapshot;
    }

    /// <summary>
    /// Opens a workspace, reading it fully before returning.
    /// </summary>
    /// <remarks>
    /// There is no "loading" state and no placeholder config: a session exists
    /// only once its workspace is loaded. Whether a workspace has been chosen
    /// yet is a question for the application shell, not for this object.
    /// </remarks>
    public static async Task<WorkspaceSession> OpenAsync(
        TaskStore store,
        TimeProvider? time = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        time ??= TimeProvider.System;

        var loaded = await store.LoadAsync(cancellationToken);
        return new WorkspaceSession(
            store, time, WorkspaceSnapshot.From(loaded, time.GetUtcNow()));
    }

    /// <summary>The workspace as of the last load or write.</summary>
    public WorkspaceSnapshot Snapshot => _snapshot;

    public Storage.Workspace Workspace => _store.Workspace;

    /// <summary>
    /// Raised when the workspace has changed, carrying the new snapshot.
    /// </summary>
    /// <remarks>
    /// Raised on whichever thread completed the work, which for a rescan is a
    /// background one. A UI must marshal to its own thread.
    /// <para>
    /// Not raised when a reload finds nothing has changed, so a sync client
    /// touching a file does not redraw anything.
    /// </para>
    /// </remarks>
    public event EventHandler<WorkspaceChangedEventArgs>? Changed;

    // ------------------------------------------------------------------ loading

    /// <summary>
    /// Re-reads the whole workspace from disk.
    /// </summary>
    /// <remarks>
    /// The one reload path, used by both the manual refresh and the watcher.
    /// ADR-0003 requires a manual refresh regardless, so having two would mean
    /// one of them was less tested than the user's escape hatch deserves.
    /// </remarks>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var loaded = await _store.LoadAsync(cancellationToken);
            var next = WorkspaceSnapshot.From(loaded, _time.GetUtcNow());
            if (next.HasSameContentAs(_snapshot)) return;

            _snapshot = next;
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    // ------------------------------------------------------------- task writes

    public async Task<MightDoTask> CreateTaskAsync(
        string summary,
        string? statusId = null,
        string description = "",
        string? categoryId = null,
        IReadOnlyList<string>? tagIds = null,
        Priority priority = Priority.Medium,
        CalendarDate? dueDate = null,
        int? estimateMinutes = null,
        CancellationToken cancellationToken = default) =>
        await MutateAsync(async snapshot =>
        {
            var targetStatus = statusId ?? snapshot.Config.DefaultStatusId;
            var task = MightDoTask.Create(
                summary: summary,
                statusId: targetStatus,
                boardRank: BoardProjection.RankForBottomOf(
                    BoardProjection.Column(snapshot.Tasks, targetStatus)),
                description: description,
                categoryId: categoryId,
                tagIds: tagIds,
                priority: priority,
                dueDate: dueDate,
                estimateMinutes: estimateMinutes);

            await SaveAsync(task, cancellationToken);
            return (WithTask(snapshot, task), task);
        }, cancellationToken);

    public Task<MightDoTask> UpdateTaskAsync(
        MightDoTask task, CancellationToken cancellationToken = default) =>
        PersistAsync(task, cancellationToken);

    /// <summary>Moves a task to another status, applying the completion-date rule.</summary>
    public Task<MightDoTask> MoveToStatusAsync(
        MightDoTask task,
        string statusId,
        string? boardRank = null,
        CancellationToken cancellationToken = default) =>
        MutateAsync(async snapshot =>
        {
            var moved = Current(snapshot, task).WithStatus(statusId, snapshot.Config, boardRank);
            await SaveAsync(moved, cancellationToken);
            return (WithTask(snapshot, moved), moved);
        }, cancellationToken);

    /// <summary>
    /// Places a task in a column between two neighbours. Pass null for either to
    /// drop at the top or bottom.
    /// </summary>
    public Task<MightDoTask> ReorderOnBoardAsync(
        MightDoTask task,
        string statusId,
        MightDoTask? above = null,
        MightDoTask? below = null,
        CancellationToken cancellationToken = default) =>
        MoveToStatusAsync(
            task, statusId, BoardProjection.RankBetween(above, below), cancellationToken);

    public Task<MightDoTask> AddNoteAsync(
        MightDoTask task, string body, CancellationToken cancellationToken = default) =>
        EditAsync(task, current => current with { Notes = [.. current.Notes, Note.Create(body)] },
            cancellationToken);

    public Task<MightDoTask> DeleteNoteAsync(
        MightDoTask task, string noteId, CancellationToken cancellationToken = default) =>
        EditAsync(task, current => current with
        {
            Notes = [.. current.Notes.Where(note => note.Id != noteId)],
        }, cancellationToken);

    public Task<MightDoTask> AddStepAsync(
        MightDoTask task, string text, CancellationToken cancellationToken = default) =>
        EditAsync(task, current => current with { Steps = [.. current.Steps, Step.Create(text)] },
            cancellationToken);

    public Task<MightDoTask> SetStepDoneAsync(
        MightDoTask task, string stepId, bool done,
        CancellationToken cancellationToken = default) =>
        EditAsync(task, current => current with
        {
            Steps = [.. current.Steps.Select(s => s.Id == stepId ? s with { Done = done } : s)],
        }, cancellationToken);

    public Task<MightDoTask> DeleteStepAsync(
        MightDoTask task, string stepId, CancellationToken cancellationToken = default) =>
        EditAsync(task, current => current with
        {
            Steps = [.. current.Steps.Where(step => step.Id != stepId)],
        }, cancellationToken);

    public Task<MightDoTask> SetTagsAsync(
        MightDoTask task, IEnumerable<string> tagIds,
        CancellationToken cancellationToken = default) =>
        EditAsync(task, current => current.WithTags(tagIds), cancellationToken);

    public Task<MightDoTask> AddReminderAsync(
        MightDoTask task, DateTime remindAt, CancellationToken cancellationToken = default) =>
        EditAsync(task, current => current with
        {
            Reminders = [.. current.Reminders, Reminder.Create(remindAt)],
        }, cancellationToken);

    public Task<MightDoTask> DeleteReminderAsync(
        MightDoTask task, string reminderId, CancellationToken cancellationToken = default) =>
        EditAsync(task, current => current with
        {
            Reminders = [.. current.Reminders.Where(r => r.Id != reminderId)],
        }, cancellationToken);

    /// <summary>
    /// Marks reminders as having fired, all in one write.
    /// </summary>
    /// <remarks>
    /// Takes a set of ids rather than one, because the Flutter implementation
    /// marks them one at a time from a stale copy of the task: with two
    /// reminders due at once, the second write is built from the pre-first-write
    /// task and silently drops the first's <c>firedAt</c>, so that reminder
    /// re-fires on every tick forever. Applying them together makes that
    /// unrepresentable.
    /// </remarks>
    public Task<MightDoTask> MarkRemindersFiredAsync(
        MightDoTask task, IReadOnlySet<string> reminderIds,
        CancellationToken cancellationToken = default)
    {
        var firedAt = _time.GetUtcNow().UtcDateTime;
        return EditAsync(task, current => current with
        {
            Reminders =
            [
                .. current.Reminders.Select(r =>
                    reminderIds.Contains(r.Id) && r.FiredAt is null
                        ? r with { FiredAt = firedAt }
                        : r),
            ],
        }, cancellationToken);
    }

    /// <summary>Dismisses reminders, all in one write. See <see cref="MarkRemindersFiredAsync"/>.</summary>
    public Task<MightDoTask> DismissRemindersAsync(
        MightDoTask task, IReadOnlySet<string> reminderIds,
        CancellationToken cancellationToken = default)
    {
        var dismissedAt = _time.GetUtcNow().UtcDateTime;
        return EditAsync(task, current => current with
        {
            Reminders =
            [
                .. current.Reminders.Select(r =>
                    reminderIds.Contains(r.Id) && r.DismissedAt is null
                        ? r with { DismissedAt = dismissedAt }
                        : r),
            ],
        }, cancellationToken);
    }

    /// <summary>
    /// Copies a file into the workspace and binds it to the task. The copy is
    /// authoritative — the user's original can move or vanish afterwards.
    /// </summary>
    public Task<MightDoTask> AttachFileAsync(
        MightDoTask task, string sourcePath, CancellationToken cancellationToken = default) =>
        MutateAsync(async snapshot =>
        {
            var attachment = await _store.CopyAttachmentAsync(
                sourcePath, _time.GetUtcNow().UtcDateTime, cancellationToken);

            var updated = Current(snapshot, task) with
            {
                Attachments = [.. Current(snapshot, task).Attachments, attachment],
            };
            await SaveAsync(updated, cancellationToken);
            return (WithTask(snapshot, updated), updated);
        }, cancellationToken);

    public Task<MightDoTask> DeleteAttachmentAsync(
        MightDoTask task, string attachmentId, CancellationToken cancellationToken = default) =>
        MutateAsync(async snapshot =>
        {
            var current = Current(snapshot, task);
            var attachment = current.Attachments.FirstOrDefault(a => a.Id == attachmentId);
            if (attachment is not null) _store.DeleteAttachment(attachment.StoredName);

            var updated = current with
            {
                Attachments = [.. current.Attachments.Where(a => a.Id != attachmentId)],
            };
            await SaveAsync(updated, cancellationToken);
            return (WithTask(snapshot, updated), updated);
        }, cancellationToken);

    public Task TrashTaskAsync(MightDoTask task, CancellationToken cancellationToken = default) =>
        MutateAsync(async snapshot =>
        {
            await _store.TrashTaskAsync(task, cancellationToken);
            return (Rebuild(snapshot, snapshot.Tasks.Where(t => t.Id != task.Id).ToList()), true);
        }, cancellationToken);

    /// <summary>
    /// The tasks sitting in the trash. Read-only: the snapshot never holds
    /// them, so this is a fresh look at the folder each time.
    /// </summary>
    public Task<IReadOnlyList<MightDoTask>> LoadTrashAsync(
        CancellationToken cancellationToken = default) =>
        _store.LoadTrashAsync(cancellationToken);

    /// <summary>
    /// Brings a trashed task back into the workspace, or returns null if
    /// nothing in the trash has that id.
    /// </summary>
    public Task<MightDoTask?> RestoreTaskAsync(
        string taskId, CancellationToken cancellationToken = default) =>
        MutateAsync<MightDoTask?>(async snapshot =>
        {
            var restored = await _store.RestoreTaskAsync(taskId, cancellationToken);
            return restored is null
                ? (snapshot, null)
                : (WithTask(snapshot, restored), restored);
        }, cancellationToken);

    // ----------------------------------------------------------- config writes

    public Task<Status> AddStatusAsync(
        string name, StatusType type, CancellationToken cancellationToken = default) =>
        MutateAsync(async snapshot =>
        {
            var status = new Status(Ulid.New(), name, type, snapshot.Config.Statuses.Count);
            var config = snapshot.Config with
            {
                Statuses = [.. snapshot.Config.Statuses, status],
            };
            await SaveConfigAsync(config, cancellationToken);
            return (WithConfig(snapshot, config), status);
        }, cancellationToken);

    public Task UpdateStatusAsync(Status status, CancellationToken cancellationToken = default) =>
        ConfigAsync(config => config with
        {
            Statuses = [.. config.Statuses.Select(s => s.Id == status.Id ? status : s)],
        }, cancellationToken);

    /// <summary>Reorders statuses, which is also the board's column order.</summary>
    public Task ReorderStatusesAsync(
        IReadOnlyList<Status> ordered, CancellationToken cancellationToken = default) =>
        ConfigAsync(config => config with
        {
            Statuses = [.. ordered.Select((status, index) => status with { Order = index })],
        }, cancellationToken);

    /// <summary>
    /// Why a status can't be deleted, or <see cref="StatusDeletionBlocker.None"/>.
    /// </summary>
    /// <remarks>
    /// Deleting a status in use is blocked rather than cascading — tasks are
    /// never orphaned or destroyed as a side effect of a settings change. The
    /// reason is returned as a value rather than a sentence so the wording stays
    /// a UI concern.
    /// </remarks>
    public StatusDeletionBlocker StatusDeletionBlockerFor(string statusId)
    {
        var config = _snapshot.Config;
        var status = config.StatusById(statusId);

        if (status is null) return StatusDeletionBlocker.Unknown;
        if (statusId == config.DefaultStatusId) return StatusDeletionBlocker.IsDefault;
        if (!config.Statuses.Any(s => s.Type == status.Type && s.Id != statusId))
        {
            return StatusDeletionBlocker.LastOfItsType;
        }

        return StatusDeletionBlocker.None;
    }

    /// <summary>Deletes a status, moving any tasks using it to <paramref name="reassignTo"/>.</summary>
    public Task DeleteStatusAsync(
        string statusId, string reassignTo, CancellationToken cancellationToken = default) =>
        MutateAsync(async snapshot =>
        {
            var blocker = StatusDeletionBlockerFor(statusId);
            if (blocker != StatusDeletionBlocker.None)
            {
                throw new InvalidOperationException(
                    $"Cannot delete this status: {blocker}.");
            }

            if (snapshot.Config.StatusById(reassignTo) is null)
            {
                throw new ArgumentException($"Unknown status: '{reassignTo}'", nameof(reassignTo));
            }

            // One batch: every affected task is written, then the config, then a
            // single change is published. The Flutter implementation writes and
            // notifies once per task, so a failure halfway leaves the workspace
            // half-migrated with no record of it.
            var tasks = new List<MightDoTask>(snapshot.Tasks.Count);
            foreach (var task in snapshot.Tasks)
            {
                if (task.StatusId != statusId)
                {
                    tasks.Add(task);
                    continue;
                }

                var moved = task.WithStatus(reassignTo, snapshot.Config);
                await SaveAsync(moved, cancellationToken);
                tasks.Add(moved);
            }

            var remaining = snapshot.Config.Statuses.Where(s => s.Id != statusId).ToList();
            var config = snapshot.Config with
            {
                Statuses = [.. remaining.Select((status, index) => status with { Order = index })],
            };
            await SaveConfigAsync(config, cancellationToken);

            return (Rebuild(snapshot, tasks, config), true);
        }, cancellationToken);

    public Task SetDefaultStatusAsync(
        string statusId, CancellationToken cancellationToken = default) =>
        ConfigAsync(config =>
        {
            var status = config.StatusById(statusId);
            if (status is null || status.Type != StatusType.Initial)
            {
                throw new ArgumentException(
                    "New tasks must start in an Initial status", nameof(statusId));
            }

            return config with { DefaultStatusId = statusId };
        }, cancellationToken);

    public Task<Category> AddCategoryAsync(
        string name, uint color, CancellationToken cancellationToken = default) =>
        MutateAsync(async snapshot =>
        {
            var category = new Category(Ulid.New(), name, color);
            var config = snapshot.Config with
            {
                Categories = [.. snapshot.Config.Categories, category],
            };
            await SaveConfigAsync(config, cancellationToken);
            return (WithConfig(snapshot, config), category);
        }, cancellationToken);

    public Task UpdateCategoryAsync(
        Category category, CancellationToken cancellationToken = default) =>
        ConfigAsync(config => config with
        {
            Categories = [.. config.Categories.Select(c => c.Id == category.Id ? category : c)],
        }, cancellationToken);

    /// <summary>
    /// Deletes a category. Tasks using it move to <paramref name="reassignTo"/>,
    /// or lose their category entirely when that is null.
    /// </summary>
    public Task DeleteCategoryAsync(
        string categoryId, string? reassignTo = null,
        CancellationToken cancellationToken = default) =>
        MutateAsync(async snapshot =>
        {
            var tasks = new List<MightDoTask>(snapshot.Tasks.Count);
            foreach (var task in snapshot.Tasks)
            {
                if (task.CategoryId != categoryId)
                {
                    tasks.Add(task);
                    continue;
                }

                var updated = task with { CategoryId = reassignTo };
                await SaveAsync(updated, cancellationToken);
                tasks.Add(updated);
            }

            var config = snapshot.Config with
            {
                Categories = [.. snapshot.Config.Categories.Where(c => c.Id != categoryId)],
            };
            await SaveConfigAsync(config, cancellationToken);

            return (Rebuild(snapshot, tasks, config), true);
        }, cancellationToken);

    /// <summary>Adds a tag, or returns the existing one with that name.</summary>
    public Task<Tag> AddTagAsync(string name, CancellationToken cancellationToken = default) =>
        MutateAsync(async snapshot =>
        {
            var existing = snapshot.Config.Tags.FirstOrDefault(
                tag => string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return (snapshot, existing);

            var created = new Tag(Ulid.New(), name);
            var config = snapshot.Config with { Tags = [.. snapshot.Config.Tags, created] };
            await SaveConfigAsync(config, cancellationToken);
            return (WithConfig(snapshot, config), created);
        }, cancellationToken);

    public Task UpdateTagAsync(Tag tag, CancellationToken cancellationToken = default) =>
        ConfigAsync(config => config with
        {
            Tags = [.. config.Tags.Select(t => t.Id == tag.Id ? tag : t)],
        }, cancellationToken);

    /// <summary>
    /// Deletes a tag, detaching it from every task. Unlike statuses and
    /// categories this needs no prompt — tags are deliberately lightweight.
    /// </summary>
    public Task DeleteTagAsync(string tagId, CancellationToken cancellationToken = default) =>
        MutateAsync(async snapshot =>
        {
            var tasks = new List<MightDoTask>(snapshot.Tasks.Count);
            foreach (var task in snapshot.Tasks)
            {
                if (!task.TagIds.Contains(tagId))
                {
                    tasks.Add(task);
                    continue;
                }

                var updated = task.WithTags(task.TagIds.Where(id => id != tagId));
                await SaveAsync(updated, cancellationToken);
                tasks.Add(updated);
            }

            var config = snapshot.Config with
            {
                Tags = [.. snapshot.Config.Tags.Where(t => t.Id != tagId)],
            };
            await SaveConfigAsync(config, cancellationToken);

            return (Rebuild(snapshot, tasks, config), true);
        }, cancellationToken);

    // ----------------------------------------------------------------- plumbing

    /// <summary>
    /// Runs <paramref name="mutate"/> with exclusive access, then publishes the
    /// snapshot it produced.
    /// </summary>
    /// <remarks>
    /// The event is raised after the gate is released, so a handler that calls
    /// back into the session cannot deadlock.
    /// </remarks>
    private async Task<T> MutateAsync<T>(
        Func<WorkspaceSnapshot, Task<(WorkspaceSnapshot Snapshot, T Result)>> mutate,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        T result;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (snapshot, value) = await mutate(_snapshot);
            var changed = !ReferenceEquals(snapshot, _snapshot);
            _snapshot = snapshot;
            result = value;

            if (!changed) return result;
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
        return result;
    }

    private Task<MightDoTask> EditAsync(
        MightDoTask task,
        Func<MightDoTask, MightDoTask> edit,
        CancellationToken cancellationToken) =>
        MutateAsync(async snapshot =>
        {
            var updated = edit(Current(snapshot, task));
            await SaveAsync(updated, cancellationToken);
            return (WithTask(snapshot, updated), updated);
        }, cancellationToken);

    private Task<MightDoTask> PersistAsync(
        MightDoTask task, CancellationToken cancellationToken) =>
        MutateAsync(async snapshot =>
        {
            await SaveAsync(task, cancellationToken);
            return (WithTask(snapshot, task), task);
        }, cancellationToken);

    private Task ConfigAsync(
        Func<WorkspaceConfig, WorkspaceConfig> edit, CancellationToken cancellationToken) =>
        MutateAsync(async snapshot =>
        {
            var config = edit(snapshot.Config);
            await SaveConfigAsync(config, cancellationToken);
            return (WithConfig(snapshot, config), true);
        }, cancellationToken);

    /// <summary>
    /// The session's copy of a task, so an edit built from a stale snapshot
    /// still applies to current state rather than reverting it.
    /// </summary>
    private static MightDoTask Current(WorkspaceSnapshot snapshot, MightDoTask task) =>
        snapshot.TaskById(task.Id) ?? task;

    private Task SaveAsync(MightDoTask task, CancellationToken cancellationToken) =>
        _store.SaveTaskAsync(task, cancellationToken);

    private Task SaveConfigAsync(WorkspaceConfig config, CancellationToken cancellationToken) =>
        _store.SaveConfigAsync(config, cancellationToken);

    private WorkspaceSnapshot WithTask(WorkspaceSnapshot snapshot, MightDoTask task)
    {
        var tasks = new List<MightDoTask>(snapshot.Tasks.Count + 1);
        var replaced = false;
        foreach (var existing in snapshot.Tasks)
        {
            if (existing.Id == task.Id)
            {
                tasks.Add(task);
                replaced = true;
            }
            else
            {
                tasks.Add(existing);
            }
        }

        if (!replaced) tasks.Add(task);
        return Rebuild(snapshot, tasks);
    }

    private WorkspaceSnapshot WithConfig(WorkspaceSnapshot snapshot, WorkspaceConfig config) =>
        Rebuild(snapshot, snapshot.Tasks, config);

    private WorkspaceSnapshot Rebuild(
        WorkspaceSnapshot snapshot,
        IReadOnlyList<MightDoTask> tasks,
        WorkspaceConfig? config = null) =>
        new(config ?? snapshot.Config,
            tasks,
            snapshot.Failures,
            snapshot.Conflicts,
            _time.GetUtcNow());

    private void RaiseChanged() =>
        Changed?.Invoke(this, new WorkspaceChangedEventArgs(_snapshot));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}
