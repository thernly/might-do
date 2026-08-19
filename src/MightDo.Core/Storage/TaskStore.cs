using System.Text;
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
    /// <summary>
    /// The longest a single file name may be. 255 bytes is what ext4, APFS and
    /// NTFS all allow, so it is the one that holds wherever a workspace is
    /// synced to.
    /// </summary>
    private const int MaxNameBytes = 255;

    /// <summary>What a copy is called until it is complete.</summary>
    private const string TempSuffix = WorkspaceFiles.TempSuffix;

    /// <summary>
    /// The version of each file as this store last read or wrote it, keyed by
    /// task id, with <c>config.json</c> under <see cref="ConfigKey"/>.
    /// </summary>
    /// <remarks>
    /// What makes a write safe against a sync client. Nothing else tells us what
    /// the other writers of a synced folder have done — the watcher is a
    /// debounced hint that may arrive after our save — so a save compares the
    /// file it is about to replace against the one it was built from, and keeps
    /// anything it doesn't recognise instead of overwriting it. A second copy of
    /// the app on this machine is held off the comparison itself by
    /// <see cref="WorkspaceLock"/>. Reached only through
    /// <c>WorkspaceSession</c>, which serialises everything, so a plain
    /// dictionary is enough.
    /// </remarks>
    private readonly Dictionary<string, string> _versions = new(StringComparer.OrdinalIgnoreCase);

    private const string ConfigKey = "config";

    public Workspace Workspace { get; } = workspace;

    /// <summary>The maximum wait for this store's machine-wide write lock.</summary>
    /// <remarks>
    /// Production stores keep the full safety margin. The internal override
    /// lets contention tests exercise the same fail-closed path without
    /// waiting out a user-facing timeout on every case.
    /// </remarks>
    internal TimeSpan LockTimeout { get; init; } = WorkspaceLock.DefaultTimeout;

    /// <summary>
    /// Makes a workspace here: creates the folder layout and seeds
    /// <c>config.json</c> if this is a fresh workspace. Safe to call on an
    /// existing one.
    /// </summary>
    /// <remarks>
    /// The one call that may create the workspace folder itself, and so the one
    /// the user has to have asked for. Everything else — every save, every
    /// reload — requires the folder to be there, because a workspace that is
    /// missing is a moved, unmounted or deleted one, and creating it back is
    /// how an empty shadow workspace appears at the old path and then collides
    /// with the real folder when it returns.
    /// </remarks>
    public async Task<WorkspaceConfig> InitialiseAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Workspace.Root);
        return await OpenLayoutAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the config of a workspace that must already be there, seeding one
    /// if the folder holds no workspace yet.
    /// </summary>
    private async Task<WorkspaceConfig> OpenLayoutAsync(CancellationToken cancellationToken)
    {
        Workspace.EnsureLayout();

        var (existing, version) = await ReadConfigAsync(cancellationToken);
        if (existing is not null)
        {
            RequireSupportedSchema(
                "config.json", existing.SchemaVersion, WorkspaceConfig.CurrentSchemaVersion);
            _versions[ConfigKey] = version;
            return existing;
        }

        var seed = WorkspaceConfig.Seed();
        await SaveConfigAsync(seed, cancellationToken);
        return seed;
    }

    /// <summary>
    /// Reads <c>config.json</c>, turning anything unparseable into a refusal
    /// that names the file.
    /// </summary>
    /// <remarks>
    /// A raw <see cref="System.Text.Json.JsonException"/> from here escapes as
    /// far as the window event that opened the workspace, which is an
    /// <c>async void</c> and so takes the application down with it. It also says
    /// nothing about which of the workspace's files is at fault. Both are fixed
    /// by refusing here, in the same shape as a config from a newer version.
    /// </remarks>
    private async Task<(WorkspaceConfig? Config, string Version)> ReadConfigAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var read = await WorkspaceFiles
                .ReadJsonVersionedAsync<WorkspaceConfig>(Workspace.ConfigFile, cancellationToken);

            if (read.Value is not null) RequireUsableConfig(read.Value);
            return read;
        }
        catch (Exception error) when (error is not OperationCanceledException
                                          and not IOException
                                          and not UnauthorizedAccessException)
        {
            throw new UnreadableConfigException(
                $"config.json in {Workspace.Root} could not be read: {error.Message} "
                + "Nothing has been changed. Repair or restore that file — or move it "
                + "aside to start the workspace's settings again — and reopen.",
                error);
        }
    }

    public async Task SaveConfigAsync(
        WorkspaceConfig config, CancellationToken cancellationToken = default)
    {
        RequireSupportedSchema(
            "config.json", config.SchemaVersion, WorkspaceConfig.CurrentSchemaVersion);

        Workspace.RequireWritable();

        using var writing = await WorkspaceLock.AcquireAsync(
            Workspace.Root, cancellationToken, LockTimeout);

        await PreserveExternalWriteAsync(ConfigKey, Workspace.ConfigFile, cancellationToken);
        _versions[ConfigKey] = await WorkspaceFiles
            .WriteJsonAtomicAsync(Workspace.ConfigFile, config, cancellationToken);
    }

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
        var config = await OpenLayoutAsync(cancellationToken);

        var tasks = new List<MightDoTask>();
        var failures = new List<TaskLoadFailure>();
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(Workspace.TasksDir))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(path);
            if (!WorkspaceFiles.IsOwnTaskFile(name)) continue;

            try
            {
                var (task, version) = await WorkspaceFiles
                    .ReadJsonVersionedAsync<MightDoTask>(path, cancellationToken);
                if (task is null) continue;

                tasks.Add(RequireSafeNames(name, task));
                versions[task.Id] = version;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failures.Add(new TaskLoadFailure(name, error));
            }
        }

        // A full load is the whole truth about tasks/, so the versions it saw
        // replace the ones we were carrying rather than adding to them.
        versions[ConfigKey] = _versions.GetValueOrDefault(ConfigKey, WorkspaceFiles.NoFile);
        _versions.Clear();
        foreach (var (id, version) in versions) _versions[id] = version;

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
    public async Task<MightDoTask?> LoadTaskAsync(
        string taskId, CancellationToken cancellationToken = default)
    {
        if (!Ulid.IsUlid(taskId)) return null;

        var (task, version) = await WorkspaceFiles.ReadJsonVersionedAsync<MightDoTask>(
            Workspace.TaskFile(taskId), cancellationToken);
        if (task is not null)
        {
            RequireSupportedSchema(
                $"{taskId}.json", task.SchemaVersion, MightDoTask.CurrentSchemaVersion);
            _versions[taskId] = version;
        }

        return task;
    }

    public async Task SaveTaskAsync(
        MightDoTask task, CancellationToken cancellationToken = default)
    {
        RequireSupportedSchema(
            $"{task.Id}.json", task.SchemaVersion, MightDoTask.CurrentSchemaVersion);

        Workspace.RequireWritable();

        // tasks/ is ours to replace if something removed it; the root is not —
        // RequireWritable has already refused a workspace that is not there.
        Directory.CreateDirectory(Workspace.TasksDir);

        var path = Workspace.TaskFile(task.Id);
        using var writing = await WorkspaceLock.AcquireAsync(
            Workspace.Root, cancellationToken, LockTimeout);

        await PreserveExternalWriteAsync(task.Id, path, cancellationToken);
        _versions[task.Id] = await WorkspaceFiles
            .WriteJsonAtomicAsync(path, task, cancellationToken);
    }

    /// <summary>
    /// Moves aside anything written to <paramref name="path"/> since we last
    /// read or wrote it, so the imminent overwrite destroys nothing.
    /// </summary>
    /// <remarks>
    /// The preserved copy lands in the same folder under a conflict name, which
    /// is where <see cref="WorkspaceFiles.FindConflictFiles"/> looks and so
    /// reaches the user as a banner on the next rescan. A file that has gone
    /// missing needs no copy: the save simply puts it back.
    /// <para>
    /// Called with the workspace's <see cref="WorkspaceLock"/> held, so on this
    /// machine no other process can write between the comparison and the
    /// replacement it guards. Another machine writing the same synced folder
    /// still can; that is the sync client's conflict copy to make, and the app
    /// reports it.
    /// </para>
    /// </remarks>
    private async Task PreserveExternalWriteAsync(
        string key, string path, CancellationToken cancellationToken)
    {
        var onDisk = await WorkspaceFiles.VersionOnDiskAsync(path, cancellationToken);
        if (onDisk == WorkspaceFiles.NoFile) return;
        if (onDisk == _versions.GetValueOrDefault(key, WorkspaceFiles.NoFile)) return;

        WorkspaceFiles.PreserveAsConflict(path, DateTime.UtcNow);
    }

    /// <summary>
    /// Moves a task's file into <c>.trash/</c>, along with its attachments.
    /// </summary>
    /// <remarks>
    /// Deliberately not a <c>deleted</c> flag: keeping trashed tasks out of every
    /// query by construction means no filter can ever forget to exclude them.
    /// Nothing purges the trash automatically — silently destroying data on a
    /// timer is worse than a folder that grows.
    /// <para>
    /// Holds the workspace's <see cref="WorkspaceLock"/> for the whole batch.
    /// Moving several files is no more atomic than compare-and-replace is: a
    /// save in another process that lands between the attachment moves and the
    /// task move republishes the task file this has just taken away, leaving an
    /// active task whose attachments are all in <c>.trash/</c>.
    /// <see cref="MoveBatch"/> can undo this process's own failures; it cannot
    /// undo another process's success.
    /// </para>
    /// </remarks>
    public async Task TrashTaskAsync(
        MightDoTask task, CancellationToken cancellationToken = default)
    {
        Workspace.EnsureLayout();
        RequireSafeNames($"{task.Id}.json", task);

        using var writing = await WorkspaceLock.AcquireAsync(
            Workspace.Root, cancellationToken, LockTimeout);

        var moves = new MoveBatch();
        try
        {
            foreach (var attachment in task.Attachments)
            {
                moves.Move(Workspace.AttachmentFile(attachment.StoredName),
                    Workspace.TrashAttachmentsDir);
            }

            moves.Move(Workspace.TaskFile(task.Id), Workspace.TrashTasksDir);
        }
        catch
        {
            moves.Undo();
            throw;
        }

        _versions.Remove(task.Id);
    }

    /// <summary>Brings a trashed task back.</summary>
    public async Task<MightDoTask?> RestoreTaskAsync(
        string taskId, CancellationToken cancellationToken = default)
    {
        if (!Ulid.IsUlid(taskId)) return null;

        Workspace.RequireWritable();

        // Held across the whole restore for the same reason trashing holds it:
        // the canonical file is checked, preserved and then replaced, and a save
        // in another process landing between those steps leaves the task in both
        // tasks/ and .trash/tasks/.
        using var writing = await WorkspaceLock.AcquireAsync(
            Workspace.Root, cancellationToken, LockTimeout);

        var trashed = Workspace.TrashedTaskFile(taskId);
        if (!File.Exists(trashed)) return null;

        // Read before moving: a task whose names would resolve outside the
        // workspace is refused while everything is still in the trash.
        var (task, version) = await WorkspaceFiles
            .ReadJsonVersionedAsync<MightDoTask>(trashed, cancellationToken);
        if (task is not null) RequireSafeNames($"{taskId}.json", task);

        // The canonical file is normally gone — trashing moved it here. If it
        // is back, somebody else put it there while the task sat in the trash,
        // and it is a different version of the same task. Keeping it as a
        // conflict lets the restore land under the task's own name: restoring
        // beside it instead would return a task the canonical file contradicts,
        // and the next rescan would silently undo the restore.
        var canonical = Workspace.TaskFile(taskId);
        if (File.Exists(canonical))
        {
            WorkspaceFiles.PreserveAsConflict(canonical, DateTime.UtcNow);
        }

        var moves = new MoveBatch();
        try
        {
            moves.Move(trashed, Workspace.TasksDir);

            // Trashing took the attachments with the task, and the copies in
            // .trash are the only copies there are. A stored name already taken
            // in attachments/ names the same attachment id, so the bytes there
            // are these bytes: the task binds to them and the trashed duplicate
            // is left where it is rather than overwriting anything.
            foreach (var attachment in task?.Attachments ?? [])
            {
                moves.Move(
                    Workspace.TrashedAttachmentFile(attachment.StoredName),
                    Workspace.AttachmentsDir);
            }
        }
        catch
        {
            moves.Undo();
            throw;
        }

        _versions[taskId] = version;

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
    /// <param name="progress">
    /// Told how many bytes have landed so far, so a long copy can say so.
    /// Reported at most once per megabyte — a caller that redraws on each
    /// report should not be redrawing thousands of times.
    /// </param>
    public async Task<Attachment> CopyAttachmentAsync(
        string sourcePath,
        DateTime addedAt,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Workspace.EnsureLayout();

        var id = Ulid.New();
        var originalName = Path.GetFileName(sourcePath);
        var storedName = $"{id}-{StorableName(originalName)}";
        var destination = Workspace.AttachmentFile(storedName);

        // Temp-and-rename, as every other write in the workspace does: a copy
        // that fails partway (disk full, source pulled, network volume dropped)
        // would otherwise leave a truncated file under a name no task will ever
        // reference, and nothing collects those.
        var temp = destination + TempSuffix;
        try
        {
            await using (var source = File.OpenRead(sourcePath))
            await using (var target = File.Create(temp))
            {
                await CopyAsync(source, target, progress, cancellationToken);
            }

            File.Move(temp, destination, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException
                                                or UnauthorizedAccessException)
            {
                // Nothing more to do: the original failure is the one to report.
            }

            throw;
        }

        return new Attachment(
            id,
            originalName,
            storedName,
            new FileInfo(destination).Length,
            addedAt);
    }

    /// <summary>
    /// The part of a stored name that comes from the user's file: plain, and
    /// short enough to write down.
    /// </summary>
    /// <remarks>
    /// <see cref="Path.GetFileName(string)"/> only strips the separators of the
    /// platform it runs on, so on Linux a file called <c>a\b.txt</c> arrives
    /// here whole and would then be refused by
    /// <see cref="Workspace.RequireStoredName"/> — a correct refusal, worded for
    /// someone reading the code, over a file the user is entitled to attach.
    /// Sanitising here means the boundary check stays a check rather than
    /// becoming the error message.
    /// <para>
    /// The length cap is the 255 bytes filesystems allow a single name; the
    /// extension is kept, because it is what opens the file. The original name
    /// is unaffected — that is what the user sees.
    /// </para>
    /// </remarks>
    private static string StorableName(string originalName)
    {
        var plain = new string([
            .. originalName.Select(c => c is '/' or '\\' or ':' ? '-' : c),
        ]);

        if (plain is "" or "." or "..") plain = "file";

        // The id, its separator and the ".tmp" the copy is written under first
        // are all ASCII, so each costs one byte. The temp name has to fit too:
        // it is the name the file is actually created with.
        return Shorten(plain, MaxNameBytes - Ulid.Length - 1 - TempSuffix.Length);
    }

    /// <summary>Trims a name to a byte budget, keeping its extension.</summary>
    private static string Shorten(string name, int budget)
    {
        if (Encoding.UTF8.GetByteCount(name) <= budget) return name;

        // An extension long enough to crowd out the name it belongs to is not
        // doing its job, so past half the budget it goes.
        var extension = Path.GetExtension(name);
        if (Encoding.UTF8.GetByteCount(extension) > budget / 2) extension = "";

        var stem = name[..^extension.Length];
        var room = budget - Encoding.UTF8.GetByteCount(extension);

        while (stem.Length > 0 && Encoding.UTF8.GetByteCount(stem) > room)
        {
            // Never split a surrogate pair: half of one is not a character.
            stem = char.IsLowSurrogate(stem[^1]) && stem.Length > 1
                ? stem[..^2]
                : stem[..^1];
        }

        return stem + extension;
    }

    /// <summary>
    /// Streams source to destination, saying how far it has got.
    /// </summary>
    /// <remarks>
    /// Written out rather than <see cref="Stream.CopyToAsync(Stream)"/> only
    /// because that cannot report progress. The buffer is a megabyte rather
    /// than the framework's 80KB: nothing here calls
    /// <c>ConfigureAwait(false)</c>, so on the UI thread every chunk is a hop
    /// back through the dispatcher, and a multi-gigabyte file should not make
    /// tens of thousands of them.
    /// </remarks>
    private static async Task CopyAsync(
        Stream source, Stream destination, IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        const int chunk = 1024 * 1024;

        var buffer = new byte[chunk];
        long copied = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            progress?.Report(copied);
        }
    }

    /// <summary>
    /// Moves an attachment's bytes into <c>.trash/</c>. Absent is not an error.
    /// </summary>
    /// <remarks>
    /// Deleting the bytes outright made removing an attachment unrecoverable
    /// the moment anything else in the operation failed: the task save that
    /// drops the record is a separate write, and a task left referring to bytes
    /// that no longer exist anywhere cannot be repaired. Trashing them gives
    /// the same recovery story a trashed task has.
    /// </remarks>
    public async Task TrashAttachmentAsync(
        string storedName, CancellationToken cancellationToken = default)
    {
        Workspace.RequireWritable();

        using var writing = await WorkspaceLock.AcquireAsync(
            Workspace.Root, cancellationToken, LockTimeout);

        var file = Workspace.AttachmentFile(storedName);
        MoveInto(file, Workspace.TrashAttachmentsDir);
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
        RequireSupportedSchema(fileName, task.SchemaVersion, MightDoTask.CurrentSchemaVersion);

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

        // After the names, not before: a task whose id would write outside the
        // workspace is that failure and should say so, rather than being
        // reported as a blank field.
        return PersistedShape.RequireWellFormed(task);
    }

    /// <summary>
    /// Checks that a file's schema version is one this build can write back.
    /// </summary>
    /// <remarks>
    /// Newer files are refused at the boundary rather than opened and later
    /// normalised: for a task the refusal becomes a <see cref="TaskLoadFailure"/>
    /// and the task never reaches memory, so nothing can save over it; for
    /// config.json it stops the workspace opening, which is the honest answer
    /// when the statuses the tasks refer to are written in a dialect we do not
    /// speak.
    /// </remarks>
    /// <summary>
    /// Checks that a config that parsed also describes a usable workspace.
    /// </summary>
    /// <remarks>
    /// <c>required</c> only guarantees the key is present, not that its value
    /// means anything — <c>"defaultStatusId": null</c> deserialises happily.
    /// Task files are refused at this boundary when they fail their invariants;
    /// a hand-edited or sync-merged config that names a status that isn't there
    /// deserves the same treatment, rather than opening and then quietly
    /// misbehaving: tasks written into a status id nothing resolves, "Unknown
    /// status" in the list, missing board columns.
    /// <para>
    /// Thrown as <see cref="InvalidOperationException"/> so the caller's catch
    /// turns it into the same <see cref="UnreadableConfigException"/> an
    /// unparseable file gets — it names the file, says nothing was changed, and
    /// says how to recover, which is exactly right here too.
    /// </para>
    /// </remarks>
    private static void RequireUsableConfig(WorkspaceConfig config)
    {
        PersistedShape.RequireWellFormed(config);

        if (config.Statuses.Count == 0)
        {
            throw new InvalidOperationException("it defines no statuses.");
        }

        var missing = Enum.GetValues<StatusType>()
            .Where(type => !config.Statuses.Any(status => status.Type == type))
            .ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"it has no status of type {string.Join(", ", missing)}, and a workspace "
                + "needs one of each.");
        }

        var fallback = config.StatusById(config.DefaultStatusId);
        if (fallback is null)
        {
            throw new InvalidOperationException(
                $"its defaultStatusId ('{config.DefaultStatusId}') is not one of its statuses.");
        }

        if (fallback.Type != StatusType.Initial)
        {
            throw new InvalidOperationException(
                $"its defaultStatusId names '{fallback.Name}', which is not an Initial "
                + "status, so new tasks would not start at the beginning.");
        }
    }

    private static void RequireSupportedSchema(string fileName, int version, int supported)
    {
        if (version <= supported) return;

        throw new UnsupportedSchemaVersionException(
            $"'{fileName}' is schema version {version}; this version of MightDo "
            + $"understands up to {supported}. Update MightDo to open it — saving it here "
            + "would discard everything the newer version added.");
    }

    /// <summary>
    /// Moves a file into a folder, or does nothing if it is not there. Returns
    /// where it landed, or null if there was nothing to move.
    /// </summary>
    private static string? MoveInto(string file, string targetDir)
    {
        if (!File.Exists(file)) return null;

        Directory.CreateDirectory(targetDir);
        var destination = Path.Combine(targetDir, Path.GetFileName(file));

        // Never clobber something already in the trash — and a link counts as
        // something. A link to nowhere reads as absent to File.Exists while the
        // copy-then-delete fallback below would happily write straight through
        // it, which is the one way a move can still land outside the workspace.
        if (File.Exists(destination) || new FileInfo(destination).LinkTarget is not null)
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            var extension = Path.GetExtension(file);
            var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            destination = Path.Combine(targetDir, $"{stem}-{stamp}{extension}");
        }

        Move(file, destination);
        return destination;
    }

    private static void Move(string file, string destination)
    {
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

    /// <summary>
    /// A run of moves that puts everything back if one of them fails.
    /// </summary>
    /// <remarks>
    /// Trashing and restoring a task each move several files — its attachments
    /// and its JSON — with no way to do them as one operation. On the storage
    /// the workspace is designed for (removable, cloud-synced, quota-limited)
    /// any one of them can fail, and stopping there would leave an active task
    /// whose attachments are already in <c>.trash</c>, or a half-restored one.
    /// Undoing what already landed makes a failed operation a no-op instead:
    /// enough for an operation whose steps are renames, and far less machinery
    /// than a journal.
    /// <para>
    /// The undo moves are renames of files we just created, in the folder we
    /// just took them from, so the case where they too fail is one where
    /// nothing could have helped.
    /// </para>
    /// </remarks>
    private sealed class MoveBatch
    {
        private readonly List<(string From, string To)> _done = [];

        public void Move(string file, string targetDir)
        {
            if (MoveInto(file, targetDir) is { } destination) _done.Add((file, destination));
        }

        public void Undo()
        {
            for (var i = _done.Count - 1; i >= 0; i--)
            {
                var (from, to) = _done[i];
                try
                {
                    TaskStore.Move(to, from);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Nothing better is available: the original failure is the
                    // one worth reporting, and it is about to be rethrown.
                }
            }
        }
    }
}
