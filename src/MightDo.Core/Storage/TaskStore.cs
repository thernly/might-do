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
                if (task is not null) tasks.Add(task);
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

    public Task<MightDoTask?> LoadTaskAsync(
        string taskId, CancellationToken cancellationToken = default) =>
        WorkspaceFiles.ReadJsonAsync<MightDoTask>(Workspace.TaskFile(taskId), cancellationToken);

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
        var trashed = Workspace.TrashedTaskFile(taskId);
        if (!File.Exists(trashed)) return null;

        MoveInto(trashed, Workspace.TasksDir);
        return await LoadTaskAsync(taskId, cancellationToken);
    }

    public async Task<IReadOnlyList<MightDoTask>> LoadTrashAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(Workspace.TrashTasksDir)) return [];

        var tasks = new List<MightDoTask>();
        foreach (var path in Directory.EnumerateFiles(Workspace.TrashTasksDir))
        {
            if (!WorkspaceFiles.IsOwnTaskFile(Path.GetFileName(path))) continue;

            try
            {
                var task = await WorkspaceFiles
                    .ReadJsonAsync<MightDoTask>(path, cancellationToken);
                if (task is not null) tasks.Add(task);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A broken file in the trash isn't worth reporting.
            }
        }

        return tasks;
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
