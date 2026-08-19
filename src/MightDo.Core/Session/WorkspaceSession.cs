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
/// A change that had to write several files failed partway through.
/// </summary>
/// <remarks>
/// The writes that landed stayed there — there is no way to take them back that
/// isn't itself a write that can fail — so the session re-reads the workspace
/// before throwing this. Memory therefore matches disk; what neither matches is
/// what the user asked for, which is what this says.
/// </remarks>
public sealed class PartiallyAppliedException(string message, Exception inner)
    : Exception(message, inner);

/// <summary>
/// Holds the whole workspace in memory and is the only thing that writes to it.
/// </summary>
/// <remarks>
/// The read side is <see cref="WorkspaceSnapshot"/>, published by
/// <see cref="Changed"/>. Views render from a snapshot and call methods here to
/// change things, which keeps a view from mutating the workspace through the
/// object it draws from.
/// <para>
/// Every mutation and every reload is serialised. Nothing in .NET gives that
/// for free, and a rescan landing halfway through a cascade would otherwise
/// clobber it.
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

    /// <summary>
    /// Cancelled when the session closes, so nothing queued behind the gate
    /// writes to a workspace the user has already left.
    /// </summary>
    private readonly CancellationTokenSource _closing = new();

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
        ObjectDisposedException.ThrowIf(_disposed, this);

        await EnterAsync(cancellationToken);
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

    /// <summary>
    /// Applies a field-level edit to the session's own copy of the task.
    /// </summary>
    /// <remarks>
    /// The caller supplies the change, not a finished record, so an edit built
    /// from a stale snapshot alters only the field it means to and cannot
    /// revert another edit that landed first. A change that leaves the task's
    /// content as it was writes nothing.
    /// </remarks>
    public Task<MightDoTask> EditTaskAsync(
        MightDoTask task,
        Func<MightDoTask, MightDoTask> edit,
        CancellationToken cancellationToken = default) =>
        MutateAsync(async snapshot =>
        {
            var current = Current(snapshot, task);
            var edited = edit(current);
            if (edited.HasSameContentAs(current)) return (snapshot, current);

            var updated = edited.Touch();
            await SaveAsync(updated, cancellationToken);
            return (WithTask(snapshot, updated), updated);
        }, cancellationToken);

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
    /// Takes a set of ids rather than one, because marking them one at a time
    /// works from a stale copy of the task: with two reminders due at once, the
    /// second write is built from the pre-first-write task and silently drops
    /// the first's <c>firedAt</c>, so that reminder re-fires on every tick
    /// forever. Applying them together makes that unrepresentable.
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
    /// <remarks>
    /// The bytes are copied before the gate is taken, and only the record that
    /// points at them is written under it. An attachment is the one write whose
    /// size the user chooses, and a multi-gigabyte file inside the gate would
    /// hold up every other write in the workspace — every keystroke's save, the
    /// reminder that comes due, the rescan behind a sync — for as long as the
    /// copy takes.
    /// <para>
    /// <paramref name="progress"/> is told how many bytes have landed, and
    /// cancelling <paramref name="cancellationToken"/> abandons the copy and
    /// leaves nothing behind.
    /// </para>
    /// </remarks>
    public async Task<MightDoTask> AttachFileAsync(
        MightDoTask task,
        string sourcePath,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var attachment = await _store.CopyAttachmentAsync(
            sourcePath, _time.GetUtcNow().UtcDateTime, progress, cancellationToken);

        try
        {
            return await MutateAsync(async snapshot =>
            {
                var current = Current(snapshot, task);
                var updated = current with { Attachments = [.. current.Attachments, attachment] };

                await SaveAsync(updated, cancellationToken);
                return (WithTask(snapshot, updated), updated);
            }, cancellationToken);
        }
        catch
        {
            // The bytes have to be in place before the record that points at
            // them, so a failed save leaves a copy nothing refers to. Since the
            // operation never happened, neither should the copy. This covers the
            // session closing mid-copy too, which is a workspace the user has
            // already left.
            _store.TrashAttachment(attachment.StoredName);
            throw;
        }
    }

    /// <summary>
    /// Unbinds an attachment from the task and moves its bytes to the trash.
    /// </summary>
    /// <remarks>
    /// The record goes first. Losing the bytes while the task still points at
    /// them is a task that can't be repaired; losing the record while the bytes
    /// sit in <c>attachments/</c> is a stray file, which is the failure worth
    /// having.
    /// </remarks>
    public Task<MightDoTask> DeleteAttachmentAsync(
        MightDoTask task, string attachmentId, CancellationToken cancellationToken = default) =>
        MutateAsync(async snapshot =>
        {
            var current = Current(snapshot, task);
            var attachment = current.Attachments.FirstOrDefault(a => a.Id == attachmentId);

            var updated = current with
            {
                Attachments = [.. current.Attachments.Where(a => a.Id != attachmentId)],
            };
            await SaveAsync(updated, cancellationToken);

            if (attachment is not null) _store.TrashAttachment(attachment.StoredName);

            return (WithTask(snapshot, updated), updated);
        }, cancellationToken);

    public Task TrashTaskAsync(MightDoTask task, CancellationToken cancellationToken = default) =>
        MutateAsync(snapshot =>
        {
            _store.TrashTask(task);
            return Task.FromResult(
                (Rebuild(snapshot, snapshot.Tasks.Where(t => t.Id != task.Id).ToList()), true));
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
        CascadeAsync(snapshot =>
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
        },
        async snapshot =>
        {
            // One batch: every affected task is written, then the config, then a
            // single change is published, so nothing sees the migration halfway
            // through. A write that fails halfway is not undone — see
            // CascadeAsync.
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

            return Rebuild(snapshot, tasks, config);
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
        CascadeAsync(async snapshot =>
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

            return Rebuild(snapshot, tasks, config);
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
        CascadeAsync(async snapshot =>
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

            return Rebuild(snapshot, tasks, config);
        }, cancellationToken);

    // ----------------------------------------------------------------- plumbing

    /// <summary>
    /// Takes the gate, giving up if the session closes while waiting for it.
    /// </summary>
    /// <remarks>
    /// The wait is what a queued operation does for however long the one ahead
    /// of it takes, so it is where closing has to be noticed: a save the user
    /// started before switching workspaces should not land in the folder they
    /// have left. Work that already holds the gate is left to finish — a write
    /// abandoned halfway is worse than a late one.
    /// </remarks>
    private async Task EnterAsync(CancellationToken cancellationToken)
    {
        using var closing = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _closing.Token);

        await _gate.WaitAsync(closing.Token);

        if (!_closing.IsCancellationRequested) return;

        _gate.Release();
        _closing.Token.ThrowIfCancellationRequested();
    }

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
        await EnterAsync(cancellationToken);
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

    /// <summary>
    /// Runs a change that writes several files, re-reading the workspace if one
    /// of those writes fails.
    /// </summary>
    /// <remarks>
    /// Deleting a status, category or tag rewrites every task that used it and
    /// then the config. If a write fails partway, the ones before it are already
    /// on disk while the snapshot describes none of them, so memory and disk
    /// disagree until something happens to rescan. Reloading here makes that
    /// deterministic, and <see cref="PartiallyAppliedException"/> tells the user
    /// what state they are in rather than surfacing a bare I/O error for a
    /// change that half-happened.
    /// <para>
    /// <paramref name="validate"/> runs before anything is written, so a domain
    /// refusal stays the exception the caller expects instead of being dressed
    /// up as a partial application.
    /// </para>
    /// </remarks>
    private async Task CascadeAsync(
        Action<WorkspaceSnapshot>? validate,
        Func<WorkspaceSnapshot, Task<WorkspaceSnapshot>> apply,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;

        await MutateAsync(async snapshot =>
        {
            validate?.Invoke(snapshot);

            try
            {
                return (await apply(snapshot), true);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failure = error;
                var loaded = await _store.LoadAsync(cancellationToken);
                return (WorkspaceSnapshot.From(loaded, _time.GetUtcNow()), true);
            }
        }, cancellationToken);

        if (failure is null) return;

        throw new PartiallyAppliedException(
            "That change was only partly applied: some tasks were updated before the "
            + $"workspace stopped accepting writes ({failure.Message}) It has been re-read "
            + "from disk, so what you see is what is there. Try again once the problem is "
            + "fixed.",
            failure);
    }

    private Task CascadeAsync(
        Func<WorkspaceSnapshot, Task<WorkspaceSnapshot>> apply,
        CancellationToken cancellationToken) =>
        CascadeAsync(validate: null, apply, cancellationToken);

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

    /// <summary>
    /// Closes the session: nothing waiting to write does so, and nothing new
    /// starts.
    /// </summary>
    /// <remarks>
    /// The gate is deliberately not disposed, and neither is
    /// <see cref="_closing"/>. A <see cref="SemaphoreSlim"/> only needs
    /// disposing once its wait handle has been used, and taking it away from an
    /// operation that is still holding it turns an ordinary shutdown — closing
    /// the window, or switching workspaces mid-save — into an
    /// <see cref="ObjectDisposedException"/> on a background thread, thrown by
    /// the release of a gate that was fine a moment earlier.
    /// <para>
    /// The same argument covers the token source, for a different reason of the
    /// same shape: a save that is still in flight links against
    /// <c>_closing.Token</c>, and
    /// <see cref="CancellationTokenSource.CreateLinkedTokenSource(CancellationToken, CancellationToken)"/>
    /// throws <see cref="ObjectDisposedException"/> off a disposed source — so
    /// disposing here would replace the <see cref="OperationCanceledException"/>
    /// shutdown is supposed to produce with a bug. Cancelled, it holds no timer
    /// and no registrations, and its wait handle is never asked for, so there is
    /// nothing for <c>Dispose</c> to release that the collector will not take.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _closing.Cancel();
    }
}
