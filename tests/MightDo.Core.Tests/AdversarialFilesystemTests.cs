using MightDo.Core.Domain;
using MightDo.Core.Session;
using MightDo.Core.Storage;

namespace MightDo.Core.Tests;

/// <summary>
/// The workspace folder behaving as the storage it is designed for actually
/// behaves: linked, removable, and written by more than one process.
/// </summary>
/// <remarks>
/// These are the cases the ordinary tests assume away. A workspace lives in
/// OneDrive or on a stick, so it can be wired to somewhere else, disappear
/// under a running app, come back, and be written by a second copy of the app
/// while this one is mid-save.
/// </remarks>
public class AdversarialFilesystemTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "mightdo-adversarial-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    private readonly string _elsewhere = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "mightdo-elsewhere-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    private Core.Storage.Workspace Workspace => new(_root);

    public void Dispose()
    {
        foreach (var dir in (string[])[_root, _elsewhere])
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    // ---- linked directories -------------------------------------------------

    [Theory]
    [InlineData("attachments", true)]
    [InlineData("tasks", true)]
    [InlineData(".trash", true)]
    [InlineData("tasks", false)]
    public async Task RefusesAWorkspaceWhoseFoldersPointSomewhereElse(
        string linked, bool targetExists)
    {
        // Every persisted name is checked to be a plain name inside the
        // workspace, but a plain name inside a linked folder still resolves
        // outside it: the escape happens while the path is resolved, after the
        // string has passed. Symlinks on Windows need a privilege the test
        // runner does not have, so this asks the question where it can be asked.
        if (OperatingSystem.IsWindows()) return;

        var store = new TaskStore(Workspace);
        await store.InitialiseAsync();

        var target = Path.Combine(_root, linked);
        Directory.Delete(target, recursive: true);

        // A link to nowhere is refused too: it reads as absent, so the folder
        // would otherwise be created straight through it the moment its target
        // appeared.
        Directory.CreateSymbolicLink(
            target, targetExists ? _elsewhere : Path.Combine(_elsewhere, "not-there-yet"));

        await Assert.ThrowsAsync<LinkedWorkspaceDirectoryException>(
            () => new TaskStore(Workspace).LoadAsync());
    }

    [Fact]
    public async Task ALinkSwappedInWhileTheAppIsOpenStopsTheNextWrite()
    {
        // The check is not a one-off at open: whatever else has write access to
        // a synced folder can wire it up after the app is looking at it.
        if (OperatingSystem.IsWindows()) return;

        using var session = await WorkspaceSession.OpenAsync(new TaskStore(Workspace));
        var task = await session.CreateTaskAsync("Before the swap");

        Directory.Delete(Path.Combine(_root, "tasks"), recursive: true);
        Directory.CreateSymbolicLink(Path.Combine(_root, "tasks"), _elsewhere);

        await Assert.ThrowsAsync<LinkedWorkspaceDirectoryException>(
            () => session.EditTaskAsync(task, current => current with { Summary = "After" }));

        Assert.Empty(Directory.EnumerateFileSystemEntries(_elsewhere));
    }

    // ---- a workspace that is no longer there --------------------------------

    [Fact]
    public async Task AnEditIntoAVanishedWorkspaceIsRefusedRatherThanRebuildingIt()
    {
        // A drive unmounted, or the folder moved on another machine. Writing
        // here would leave a task file and a tasks/ folder at the old path, to
        // be found later by whatever the real folder syncs back over.
        using var session = await WorkspaceSession.OpenAsync(new TaskStore(Workspace));
        var task = await session.CreateTaskAsync("Written while it was there");

        Directory.Delete(_root, recursive: true);

        await Assert.ThrowsAsync<WorkspaceUnavailableException>(
            () => session.EditTaskAsync(task, current => current with { Summary = "Later" }));
        await Assert.ThrowsAsync<WorkspaceUnavailableException>(
            () => session.AddNoteAsync(task, "and a note"));
        await Assert.ThrowsAsync<WorkspaceUnavailableException>(
            () => session.AddStatusAsync("Settings change", StatusType.Active));
        await Assert.ThrowsAsync<WorkspaceUnavailableException>(
            () => session.RefreshAsync());

        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task AReminderComingDueDoesNotRebuildAVanishedWorkspaceEither()
    {
        // The one write with no user behind it: a reminder marking itself fired
        // would otherwise recreate the folder while nobody was even looking.
        using var session = await WorkspaceSession.OpenAsync(new TaskStore(Workspace));
        var task = await session.CreateTaskAsync("Ring the dentist");
        await session.AddReminderAsync(
            session.Snapshot.TaskById(task.Id)!, DateTime.UtcNow.AddMinutes(-1));

        var due = session.Snapshot.TaskById(task.Id)!;
        Directory.Delete(_root, recursive: true);

        await Assert.ThrowsAsync<WorkspaceUnavailableException>(
            () => session.MarkRemindersFiredAsync(
                due, due.Reminders.Select(r => r.Id).ToHashSet()));

        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task AWorkspaceThatComesBackIsReadAgainRatherThanOverwritten()
    {
        using var session = await WorkspaceSession.OpenAsync(new TaskStore(Workspace));
        var task = await session.CreateTaskAsync("Ours");

        // Away, and back with an edit made on the machine that had it.
        var contents = await File.ReadAllTextAsync(Workspace.TaskFile(task.Id));
        Directory.Delete(_root, recursive: true);
        await Assert.ThrowsAsync<WorkspaceUnavailableException>(
            () => session.EditTaskAsync(task, current => current with { Summary = "Lost" }));

        Directory.CreateDirectory(Path.Combine(_root, "tasks"));
        await File.WriteAllTextAsync(
            Workspace.TaskFile(task.Id),
            contents.Replace("Ours", "Edited on the other machine"));

        await session.RefreshAsync();

        Assert.Equal(
            "Edited on the other machine", session.Snapshot.TaskById(task.Id)!.Summary);
    }

    // ---- two writers at once ------------------------------------------------

    [Fact]
    public async Task TwoStoresSavingTheSameTaskAtOnceLoseNothing()
    {
        // The check-then-replace sequence is three filesystem operations, and
        // two copies of the app that both read version V can both find V still
        // there. Whoever gets there second must still find the other's bytes and
        // keep them, rather than the two overwriting each other.
        var seed = new TaskStore(Workspace);
        await seed.InitialiseAsync();

        // Ten pairs rather than one: an unserialised check and write would get
        // away with it now and then, and a test that only sometimes notices is
        // one that will not notice at all.
        foreach (var round in Enumerable.Range(0, 10))
        {
            var task = MightDoTask.Create($"Round {round}", "s", Rank.First);
            await seed.SaveTaskAsync(task);

            var stores = new[] { new TaskStore(Workspace), new TaskStore(Workspace) };
            foreach (var store in stores) await store.LoadAsync();

            await Task.WhenAll(stores.Select((store, i) => Task.Run(
                () => store.SaveTaskAsync(task with { Summary = $"Round {round} by {i}" }))));

            var written = await WrittenInTasksFolder();
            Assert.Contains(written, text => text.Contains($"\"summary\": \"Round {round} by 0\""));
            Assert.Contains(written, text => text.Contains($"\"summary\": \"Round {round} by 1\""));
        }
    }

    [Fact]
    public async Task TwoStoresSavingTheConfigAtOnceLoseNothingEither()
    {
        // The config is one file for the whole workspace, so two writers collide
        // on it far more readily than on any one task.
        var seed = new TaskStore(Workspace);
        var config = await seed.InitialiseAsync();

        var stores = new[] { new TaskStore(Workspace), new TaskStore(Workspace) };
        foreach (var store in stores) await store.LoadAsync();

        await Task.WhenAll(stores.Select((store, i) => Task.Run(() => store.SaveConfigAsync(
            config with { Categories = [new Category(Ulid.New(), $"Category {i}", 0xFF2196F3)] }))));

        var written = new List<string>();
        foreach (var path in Directory.EnumerateFiles(_root, "config*.json"))
        {
            written.Add(await File.ReadAllTextAsync(path));
        }

        Assert.Contains(written, text => text.Contains("\"name\": \"Category 0\""));
        Assert.Contains(written, text => text.Contains("\"name\": \"Category 1\""));
    }

    private async Task<List<string>> WrittenInTasksFolder()
    {
        var written = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(_root, "tasks")))
        {
            written.Add(await File.ReadAllTextAsync(path));
        }

        return written;
    }

    [Fact]
    public async Task OverlappingWritesLeaveNoTemporaryFilesBehind()
    {
        // One shared temporary name means each writer is writing the file the
        // other is about to rename: a failed save, or the wrong writer's bytes
        // under the right name.
        var store = new TaskStore(Workspace);
        await store.InitialiseAsync();

        var path = Path.Combine(_root, "tasks", "overlap.json");
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
            WorkspaceFiles.WriteJsonAtomicAsync(
                path, MightDoTask.Create($"Writer {i}", "s", Rank.First)))));

        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "tasks"), "*.tmp"));
        Assert.NotNull(await WorkspaceFiles.ReadJsonAsync<MightDoTask>(path));
    }

    // ---- restoring onto a task that has come back ---------------------------

    [Fact]
    public async Task RestoringOverATaskThatCameBackKeepsBothAndReturnsTheCanonicalOne()
    {
        // The sync client puts the file back after the local user trashes it.
        // Restoring beside it would return a task the canonical file
        // contradicts, and the next rescan would silently undo the restore.
        using var session = await WorkspaceSession.OpenAsync(new TaskStore(Workspace));
        var task = await session.CreateTaskAsync("Trashed here");
        await session.TrashTaskAsync(task);

        await WorkspaceFiles.WriteJsonAtomicAsync(
            Workspace.TaskFile(task.Id),
            task with { Summary = "Restored by the sync client" });

        var restored = await session.RestoreTaskAsync(task.Id);

        Assert.NotNull(restored);
        Assert.Equal("Trashed here", restored.Summary);

        // What the session says it restored is what the task's own file holds.
        var canonical = await WorkspaceFiles.ReadJsonAsync<MightDoTask>(
            Workspace.TaskFile(task.Id));
        Assert.Equal("Trashed here", canonical!.Summary);

        // And the version that came back is kept, not overwritten.
        await session.RefreshAsync();
        var conflict = Assert.Single(session.Snapshot.Conflicts);
        Assert.Equal(task.Id, conflict.TaskId);
        Assert.Contains(
            "Restored by the sync client",
            await File.ReadAllTextAsync(conflict.FullPath));
    }
}
