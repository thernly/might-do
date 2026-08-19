using System.Text.Json.Nodes;
using MightDo.Core.Domain;
using MightDo.Core.Session;
using MightDo.Core.Storage;

namespace MightDo.Core.Tests;

/// <summary>
/// What happens when a change is asked for on something that is no longer
/// there, and when two processes want the workspace at once.
/// </summary>
/// <remarks>
/// Both are the same shape of bug: an operation that reads the world, takes its
/// time, and then writes as though nothing moved. The first resurrects a task
/// the user deleted; the second overwrites an edit another copy of the app
/// made. Neither is visible in a test that does one thing at a time.
/// </remarks>
public class DeletionAndLockingTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "mightdo-deletion-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    private WorkspaceSession _session = null!;

    private Core.Storage.Workspace Workspace => new(_root);

    public async Task InitializeAsync() =>
        _session = await WorkspaceSession.OpenAsync(new TaskStore(Workspace));

    public Task DisposeAsync()
    {
        _session.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    // ---- a deleted task stays deleted ---------------------------------------

    [Fact]
    public async Task AnEditQueuedBehindATrashDoesNotBringTheTaskBack()
    {
        var task = await _session.CreateTaskAsync("Cancel the order");

        await _session.TrashTaskAsync(task);

        // The pane still holds the task it was opened with, and the user is
        // still typing in it.
        await Assert.ThrowsAsync<TaskNoLongerExistsException>(
            () => _session.EditTaskAsync(task, current => current with { Summary = "Renamed" }));

        Assert.Null(_session.Snapshot.TaskById(task.Id));
        Assert.False(File.Exists(Workspace.TaskFile(task.Id)));
        Assert.True(File.Exists(Workspace.TrashedTaskFile(task.Id)));
    }

    [Fact]
    public async Task MovingADeletedTaskToAnotherStatusIsRefusedToo()
    {
        var task = await _session.CreateTaskAsync("Drag me");
        var elsewhere = _session.Snapshot.Config.Statuses.First(s => s.Type == StatusType.Active);

        await _session.TrashTaskAsync(task);

        await Assert.ThrowsAsync<TaskNoLongerExistsException>(
            () => _session.MoveToStatusAsync(task, elsewhere.Id));
        Assert.Empty(_session.Snapshot.Tasks);
    }

    [Fact]
    public async Task ADeletionThatLandsDuringALongCopyLeavesNoResurrectedTask()
    {
        var task = await _session.CreateTaskAsync("Attach the contract");

        var source = Path.Combine(_root, "contract.pdf");
        await File.WriteAllBytesAsync(source, new byte[4 * 1024 * 1024]);

        // The bytes are copied outside the gate on purpose, so the trash can and
        // does land first — which is the race the fallback used to lose.
        var trashed = false;
        var attaching = _session.AttachFileAsync(task, source, new Progress<long>(async _ =>
        {
            if (Interlocked.Exchange(ref trashed, true)) return;
            await _session.TrashTaskAsync(task);
        }));

        await Assert.ThrowsAsync<TaskNoLongerExistsException>(() => attaching);

        Assert.Null(_session.Snapshot.TaskById(task.Id));
        Assert.False(File.Exists(Workspace.TaskFile(task.Id)));

        // The copy is undone as well: bytes nothing points at do not belong in
        // the active attachments folder.
        Assert.Empty(Directory.GetFiles(Workspace.AttachmentsDir));
    }

    [Fact]
    public async Task AnExternalDeletionSeenOnRefreshRefusesTheNextStalePaneCommand()
    {
        var task = await _session.CreateTaskAsync("Deleted on the other machine");

        File.Delete(Workspace.TaskFile(task.Id));
        await _session.RefreshAsync();

        await Assert.ThrowsAsync<TaskNoLongerExistsException>(
            () => _session.AddNoteAsync(task, "Still typing over here"));
        Assert.False(File.Exists(Workspace.TaskFile(task.Id)));
    }

    [Fact]
    public async Task TrashingATaskTwiceIsNotAnError()
    {
        // Unlike an edit, a second deletion asks for the state the workspace is
        // already in.
        var task = await _session.CreateTaskAsync("Delete me once");

        await _session.TrashTaskAsync(task);
        await _session.TrashTaskAsync(task);

        Assert.Empty(_session.Snapshot.Tasks);
    }

    [Fact]
    public async Task TrashingTakesAnAttachmentAddedSinceTheCallerReadTheTask()
    {
        var stale = await _session.CreateTaskAsync("Trash me");

        var source = Path.Combine(_root, "note.txt");
        await File.WriteAllTextAsync(source, "bytes");
        await _session.AttachFileAsync(stale, source);

        // The caller's copy predates the attachment; the session's does not, and
        // it is the session's that is trashed.
        await _session.TrashTaskAsync(stale);

        Assert.Empty(Directory.GetFiles(Workspace.AttachmentsDir));
        Assert.Single(Directory.GetFiles(Workspace.TrashAttachmentsDir));
    }

    // ---- the lock covers every mutation, and does not fail open -------------

    [Fact]
    public async Task ASaveThatCannotTakeTheLockChangesNothing()
    {
        var store = new TaskStore(Workspace);
        await store.InitialiseAsync();

        var task = MightDoTask.Create("Written by the other process", "s", Rank.First);
        await store.SaveTaskAsync(task);

        using (HoldTheLock())
        {
            await Assert.ThrowsAsync<WorkspaceBusyException>(
                () => store.SaveTaskAsync(task with { Summary = "Written by this one" }));
        }

        var written = await File.ReadAllTextAsync(Workspace.TaskFile(task.Id));
        Assert.Contains("Written by the other process", written);
    }

    [Theory]
    [InlineData("trash")]
    [InlineData("restore")]
    [InlineData("attachment")]
    public async Task EveryMutationWaitsForTheLockRatherThanWritingWithoutIt(string operation)
    {
        var store = new TaskStore(Workspace);
        await store.InitialiseAsync();

        var task = MightDoTask.Create("Contended", "s", Rank.First);
        await store.SaveTaskAsync(task);
        if (operation == "restore") await store.TrashTaskAsync(task);

        var source = Path.Combine(_root, "bytes.bin");
        await File.WriteAllTextAsync(source, "bytes");
        var attachment = await store.CopyAttachmentAsync(source, DateTime.UtcNow);

        using (HoldTheLock())
        {
            await Assert.ThrowsAsync<WorkspaceBusyException>(() => operation switch
            {
                "trash" => store.TrashTaskAsync(task),
                "restore" => store.RestoreTaskAsync(task.Id),
                _ => store.TrashAttachmentAsync(attachment.StoredName),
            });
        }
    }

    // ---- a malformed file is one bad file, not a broken workspace -----------

    [Theory]
    [InlineData("\"steps\": [null]")]
    [InlineData("\"notes\": [null]")]
    [InlineData("\"reminders\": null")]
    [InlineData("\"summary\": null")]
    [InlineData("\"statusId\": null")]
    public async Task ATaskThatParsesIntoHolesIsUnreadableRatherThanPoisonous(string crafted)
    {
        var task = await _session.CreateTaskAsync("Will be mangled");
        var healthy = await _session.CreateTaskAsync("Must survive");

        var path = Workspace.TaskFile(task.Id);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var mangled = JsonNode.Parse($"{{{crafted}}}")!.AsObject();
        foreach (var (key, value) in mangled)
        {
            json[key] = value?.DeepClone();
        }

        await File.WriteAllTextAsync(path, json.ToJsonString());

        var loaded = await new TaskStore(Workspace).LoadAsync();

        // The whole point: the bad file is reported, and everything else opens.
        Assert.Equal(healthy.Id, Assert.Single(loaded.Tasks).Id);
        Assert.Equal($"{task.Id}.json", Assert.Single(loaded.Failures).FileName);
    }

    [Fact]
    public async Task AConfigThatParsesIntoHolesRefusesTheWorkspaceByName()
    {
        var config = Workspace.ConfigFile;
        var json = JsonNode.Parse(await File.ReadAllTextAsync(config))!.AsObject();
        json["categories"] = JsonNode.Parse("[null]");
        await File.WriteAllTextAsync(config, json.ToJsonString());

        var error = await Assert.ThrowsAsync<UnreadableConfigException>(
            () => new TaskStore(Workspace).LoadAsync());
        Assert.Contains("config.json", error.Message);
    }

    [Fact]
    public async Task AWorkspaceFileTooBigToBeOneOfOursIsRefusedBeforeItIsRead()
    {
        var task = await _session.CreateTaskAsync("Grew in the night");

        await using (var file = File.Create(Workspace.TaskFile(task.Id)))
        {
            file.SetLength(PersistedShapeLimit + 1);
        }

        var loaded = await new TaskStore(Workspace).LoadAsync();
        Assert.Empty(loaded.Tasks);
        Assert.Single(loaded.Failures);
    }

    /// <summary>The cap in <c>PersistedShape</c>, which is internal to Core.</summary>
    private const long PersistedShapeLimit = 16L * 1024 * 1024;

    /// <summary>
    /// Takes the workspace's lock the way another process would: by name, from
    /// outside <see cref="TaskStore"/>, so the store has to wait for it.
    /// </summary>
    private FileStream HoldTheLock()
    {
        var name = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Workspace.Root)));
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "mightdo-locks")).FullName;

        return new FileStream(
            Path.Combine(dir, $"{name}.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
    }
}
