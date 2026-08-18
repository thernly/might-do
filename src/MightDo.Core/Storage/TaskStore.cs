using MightDo.Core.Domain;

namespace MightDo.Core.Storage;

/// <summary>A task file that couldn't be parsed.</summary>
/// <remarks>
/// Reported rather than swallowed — a task that silently vanishes is worse than
/// one that shows up as broken.
/// </remarks>
public sealed record TaskLoadFailure(string FileName, Exception Error);

/// <summary>Result of loading a workspace from disk, including anything that failed.</summary>
public sealed record LoadedWorkspace(
    WorkspaceConfig Config,
    IReadOnlyList<MightDoTask> Tasks,
    IReadOnlyList<TaskLoadFailure> Failures,
    IReadOnlyList<ConflictFile> Conflicts);

/// <summary>
/// Reads and writes the workspace. One JSON file per task; see
/// <c>docs/adr/0001-file-per-task-json-storage.md</c>.
/// </summary>
public sealed class TaskStore(Workspace workspace)
{
    public Workspace Workspace { get; } = workspace;

    /// <summary>
    /// Creates the folder layout and seeds <c>config.json</c> if this is a fresh
    /// workspace. Safe to call on an existing one.
    /// </summary>
    public async Task<WorkspaceConfig> InitialiseAsync(CancellationToken cancellationToken = default)
    {
        Workspace.EnsureLayout();

        var existing = await WorkspaceFiles
            .ReadJsonAsync<WorkspaceConfig>(Workspace.ConfigFile, cancellationToken);
        if (existing is not null) return existing;

        var seed = WorkspaceConfig.Seed();
        await SaveConfigAsync(seed, cancellationToken);
        return seed;
    }

    public Task SaveConfigAsync(
        WorkspaceConfig config, CancellationToken cancellationToken = default) =>
        WorkspaceFiles.WriteJsonAtomicAsync(Workspace.ConfigFile, config, cancellationToken);

    /// <summary>
    /// Reads the whole workspace: config, every task, everything that failed to
    /// parse, and every conflict artefact.
    /// </summary>
    /// <remarks>
    /// Reading everything is the normal mode, not a fallback — ADR-0001 makes
    /// queries our code over an in-memory collection, and ADR-0003 makes live
    /// reload a debounced rescan rather than an interpretation of watch events.
    /// </remarks>
    public async Task<LoadedWorkspace> LoadAsync(CancellationToken cancellationToken = default)
    {
        var config = await InitialiseAsync(cancellationToken);

        var tasks = new List<MightDoTask>();
        var failures = new List<TaskLoadFailure>();

        foreach (var path in Directory.EnumerateFiles(Workspace.TasksDir))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(path);
            if (!WorkspaceFiles.IsOwnTaskFile(name)) continue;

            try
            {
                var task = await WorkspaceFiles
                    .ReadJsonAsync<MightDoTask>(path, cancellationToken);
                if (task is not null) tasks.Add(RequireSafeNames(name, task));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failures.Add(new TaskLoadFailure(name, error));
            }
        }

        return new LoadedWorkspace(
            config,
            tasks,
            failures,
            WorkspaceFiles.FindConflictFiles(Workspace));
    }

    /// <summary>Reads one task, or null if there is no such task.</summary>
    /// <remarks>
    /// An id that isn't a ULID names no file we could have written, so it reads
    /// as absent rather than as an error — looking something up is allowed to
    /// come back empty. Writing under such an id is not: that throws.
    /// </remarks>
    public Task<MightDoTask?> LoadTaskAsync(
        string taskId, CancellationToken cancellationToken = default) =>
        Ulid.IsUlid(taskId)
            ? WorkspaceFiles.ReadJsonAsync<MightDoTask>(
                Workspace.TaskFile(taskId), cancellationToken)
            : Task.FromResult<MightDoTask?>(null);

    public Task SaveTaskAsync(MightDoTask task, CancellationToken cancellationToken = default) =>
        WorkspaceFiles.WriteJsonAtomicAsync(Workspace.TaskFile(task.Id), task, cancellationToken);

    /// <summary>
    /// Moves a task's file into <c>.trash/</c>, along with its attachments.
    /// </summary>
    /// <remarks>
    /// Deliberately not a <c>deleted</c> flag: keeping trashed tasks out of every
    /// query by construction means no filter can ever forget to exclude them.
    /// Nothing purges the trash automatically — silently destroying data on a
    /// timer is worse than a folder that grows.
    /// </remarks>
    public Task TrashTaskAsync(MightDoTask task, CancellationToken cancellationToken = default)
    {
        Workspace.EnsureLayout();
        RequireSafeNames($"{task.Id}.json", task);

        foreach (var attachment in task.Attachments)
        {
            var file = Workspace.AttachmentFile(attachment.StoredName);
            if (File.Exists(file)) MoveInto(file, Workspace.TrashAttachmentsDir);
        }

        var taskFile = Workspace.TaskFile(task.Id);
        if (File.Exists(taskFile)) MoveInto(taskFile, Workspace.TrashTasksDir);

        return Task.CompletedTask;
    }

    /// <summary>Brings a trashed task back.</summary>
    public async Task<MightDoTask?> RestoreTaskAsync(
        string taskId, CancellationToken cancellationToken = default)
    {
        if (!Ulid.IsUlid(taskId)) return null;

        var trashed = Workspace.TrashedTaskFile(taskId);
        if (!File.Exists(trashed)) return null;

        // Read before moving: a task whose names would resolve outside the
        // workspace is refused while everything is still in the trash.
        var task = await WorkspaceFiles.ReadJsonAsync<MightDoTask>(trashed, cancellationToken);
        if (task is not null) RequireSafeNames($"{taskId}.json", task);

        MoveInto(trashed, Workspace.TasksDir);

        // Trashing took the attachments with the task, and the copies in
        // .trash are the only copies there are.
        foreach (var attachment in task?.Attachments ?? [])
        {
            var file = Workspace.TrashedAttachmentFile(attachment.StoredName);
            if (File.Exists(file)) MoveInto(file, Workspace.AttachmentsDir);
        }

        return task;
    }

    public async Task<IReadOnlyList<MightDoTask>> LoadTrashAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(Workspace.TrashTasksDir)) return [];

        var tasks = new List<MightDoTask>();
        foreach (var path in Directory.EnumerateFiles(Workspace.TrashTasksDir))
        {
            var name = Path.GetFileName(path);
            if (!WorkspaceFiles.IsOwnTaskFile(name)) continue;

            try
            {
                var task = await WorkspaceFiles
                    .ReadJsonAsync<MightDoTask>(path, cancellationToken);
                if (task is not null) tasks.Add(RequireSafeNames(name, task));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A broken file in the trash isn't worth reporting.
            }
        }

        return tasks;
    }

    /// <summary>
    /// Copies a file into the workspace's attachments folder.
    /// </summary>
    /// <remarks>
    /// The copy is authoritative — moving or deleting the user's original has no
    /// effect on it. The stored name is prefixed with the attachment's id so two
    /// files called <c>contract.pdf</c> cannot collide.
    /// </remarks>
    public async Task<Attachment> CopyAttachmentAsync(
        string sourcePath, DateTime addedAt, CancellationToken cancellationToken = default)
    {
        Workspace.EnsureLayout();

        var id = Ulid.New();
        var originalName = Path.GetFileName(sourcePath);
        var storedName = $"{id}-{originalName}";
        var destination = Workspace.AttachmentFile(storedName);

        await using (var source = File.OpenRead(sourcePath))
        await using (var target = File.Create(destination))
        {
            await source.CopyToAsync(target, cancellationToken);
        }

        return new Attachment(
            id,
            originalName,
            storedName,
            new FileInfo(destination).Length,
            addedAt);
    }

    /// <summary>Removes an attachment's bytes. Absent is not an error.</summary>
    public void DeleteAttachment(string storedName)
    {
        var file = Workspace.AttachmentFile(storedName);
        if (File.Exists(file)) File.Delete(file);
    }

    /// <summary>
    /// Checks that a loaded task's persisted names still address files inside
    /// the workspace, and that it is the task its own file is named after.
    /// </summary>
    /// <remarks>
    /// Parse failures are already treated as untrusted input; the strings inside
    /// a file that parses are no more trustworthy. An id that disagrees with its
    /// filename means the next save would write somewhere else entirely, so it
    /// is a broken file rather than a task.
    /// </remarks>
    private static MightDoTask RequireSafeNames(string fileName, MightDoTask task)
    {
        var expected = Path.GetFileNameWithoutExtension(fileName);
        if (!Workspace.RequireTaskId(task.Id).Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsafeWorkspaceNameException(
                $"Task id '{task.Id}' does not match its file name '{fileName}'.");
        }

        foreach (var attachment in task.Attachments)
        {
            Workspace.RequireStoredName(attachment.StoredName);
        }

        return task;
    }

    private static void MoveInto(string file, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        var destination = Path.Combine(targetDir, Path.GetFileName(file));

        // Never clobber something already in the trash.
        if (File.Exists(destination))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            var extension = Path.GetExtension(file);
            var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            destination = Path.Combine(targetDir, $"{stem}-{stamp}{extension}");
        }

        try
        {
            File.Move(file, destination);
        }
        catch (IOException)
        {
            // Renames can fail across volumes; fall back to copy-then-delete.
            File.Copy(file, destination, overwrite: true);
            File.Delete(file);
        }
    }
}
