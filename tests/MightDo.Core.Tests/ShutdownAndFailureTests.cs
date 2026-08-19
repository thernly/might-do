using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using MightDo.Core.Domain;
using MightDo.Core.Reminders;
using MightDo.Core.Session;
using MightDo.Core.Storage;

namespace MightDo.Core.Tests;

/// <summary>
/// What happens when a workspace closes while it is still working, and where a
/// failure with no caller goes.
/// </summary>
/// <remarks>
/// Closing the window, quitting, and switching workspaces all tear a session
/// down at an arbitrary moment — including one where a save is in flight or
/// queued behind it. None of those may produce a half-written workspace, a
/// write into a folder the user has left, or an exception on a background
/// thread.
/// </remarks>
public class ShutdownTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mightdo-shutdown-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private Task<WorkspaceSession> OpenAsync() =>
        WorkspaceSession.OpenAsync(new TaskStore(new Core.Storage.Workspace(_root)));

    [Fact]
    public async Task AWriteInFlightWhenTheSessionClosesStillFinishes()
    {
        // The edit function runs inside the gate, which is what lets this block
        // a write at exactly the moment the session is disposed. Before the fix
        // the gate was disposed underneath it and the write failed on release,
        // throwing from a thread with nobody to catch it.
        var session = await OpenAsync();
        var task = await session.CreateTaskAsync("In flight");

        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var writing = Task.Run(() => session.EditTaskAsync(task, current =>
        {
            holding.Set();
            release.Wait();
            return current with { Summary = "Finished" };
        }));

        holding.Wait();
        session.Dispose();
        release.Set();

        var written = await writing;
        Assert.Equal("Finished", written.Summary);

        var store = new TaskStore(new Core.Storage.Workspace(_root));
        var reloaded = await store.LoadTaskAsync(task.Id);
        Assert.Equal("Finished", reloaded!.Summary);
    }

    [Fact]
    public async Task AnEditWaitingWhenTheSessionClosesNeverLands()
    {
        // The user pressed something, then switched workspaces before it got
        // its turn. Writing it afterwards would put an edit in a folder they
        // have left.
        var session = await OpenAsync();
        var task = await session.CreateTaskAsync("Queued behind");

        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var first = Task.Run(() => session.EditTaskAsync(task, current =>
        {
            holding.Set();
            release.Wait();
            return current with { Summary = "First" };
        }));

        holding.Wait();
        var queued = session.EditTaskAsync(task, current => current with { Summary = "Too late" });

        session.Dispose();
        release.Set();
        await first;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        var store = new TaskStore(new Core.Storage.Workspace(_root));
        var reloaded = await store.LoadTaskAsync(task.Id);
        Assert.Equal("First", reloaded!.Summary);
    }

    [Fact]
    public async Task ARefreshStartedAfterTheSessionClosesDoesNothing()
    {
        var session = await OpenAsync();
        session.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => session.RefreshAsync());
    }

    [Fact]
    public async Task ATickHoldingTheGateWhenTheSchedulerClosesFinishesCleanly()
    {
        // Quitting during a reminder write. The notifier is held open so the
        // tick is provably still inside the scheduler's own gate when Dispose
        // runs.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 18, 9, 0, 0, TimeSpan.Zero));
        using var session = await WorkspaceSession.OpenAsync(
            new TaskStore(new Core.Storage.Workspace(_root)), time);

        var task = await session.CreateTaskAsync("Remind me");
        await session.AddReminderAsync(
            session.Snapshot.TaskById(task.Id)!, time.GetUtcNow().UtcDateTime.AddMinutes(-1));

        var notifier = new BlockingNotifier();
        var scheduler = new ReminderScheduler(session, notifier, time);

        var tick = scheduler.TickAsync();
        await notifier.Notifying.Task;

        scheduler.Dispose();
        notifier.Continue.SetResult();

        Assert.Single(await tick);
    }

    [Fact]
    public async Task AFailedTickIsReportedRatherThanLost()
    {
        // A tick runs from a timer, so a write that fails has no caller to fail
        // to. Without the event it stops marking reminders in silence and the
        // same ones re-show forever.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 18, 9, 0, 0, TimeSpan.Zero));
        using var session = await WorkspaceSession.OpenAsync(
            new TaskStore(new Core.Storage.Workspace(_root)), time);

        var task = await session.CreateTaskAsync("Unwritable");
        await session.AddReminderAsync(
            session.Snapshot.TaskById(task.Id)!, time.GetUtcNow().UtcDateTime.AddMinutes(-1));

        // A directory where the write's temporary file goes: nothing can be
        // written there, which is what an unmounted drive looks like from here.
        Directory.CreateDirectory(session.Workspace.TaskFile(task.Id) + ".tmp");

        using var scheduler = new ReminderScheduler(session, time: time);
        var reported = new TaskCompletionSource<Exception>();
        scheduler.Failed += (_, error) => reported.TrySetResult(error);

        scheduler.Start(TimeSpan.FromSeconds(20));
        time.Advance(TimeSpan.FromSeconds(20));

        var failure = await reported.Task.WaitAsync(
            TimeSpan.FromSeconds(10));
        Assert.NotNull(failure);
    }

    /// <summary>A notifier that stops mid-tick until it is let go.</summary>
    private sealed class BlockingNotifier : IReminderNotifier
    {
        public TaskCompletionSource Notifying { get; } = new();

        public TaskCompletionSource Continue { get; } = new();

        public async Task NotifyAsync(
            MightDoTask task, Reminder reminder, CancellationToken cancellationToken)
        {
            Notifying.TrySetResult();
            await Continue.Task;
        }
    }
}

/// <summary>What the store does with a <c>config.json</c> it cannot read.</summary>
/// <remarks>
/// Malformed task files have always been reported rather than swallowed; the
/// config was the one file whose corruption threw a raw
/// <see cref="System.Text.Json.JsonException"/> out through the window event
/// that opened the workspace.
/// </remarks>
public class UnreadableConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mightdo-badconfig-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RefusesAConfigThatWillNotParseAndLeavesItAlone()
    {
        var workspace = new Core.Storage.Workspace(_root);
        workspace.EnsureLayout();
        await File.WriteAllTextAsync(
            workspace.ConfigFile, "{ this is not json",
            CancellationToken.None);

        var store = new TaskStore(workspace);

        var error = await Assert.ThrowsAsync<UnreadableConfigException>(
            () => store.LoadAsync());

        Assert.Contains("config.json", error.Message);

        // Seeding over it would replace the statuses every task refers to.
        Assert.Equal(
            "{ this is not json",
            await File.ReadAllTextAsync(
                workspace.ConfigFile));
    }

    [Fact]
    public async Task StillSeedsAWorkspaceThatSimplyHasNoConfigYet()
    {
        var store = new TaskStore(new Core.Storage.Workspace(_root));

        var config = await store.InitialiseAsync();

        Assert.NotEmpty(config.Statuses);
    }
}

/// <summary>
/// What is left behind when a change that touches several files fails partway.
/// </summary>
/// <remarks>
/// The storage layer is built for folders that stop accepting writes mid-change
/// — a full disk, an unmounted drive, a sync client holding a file. A cascade
/// that gives up halfway may not leave memory describing a workspace that is no
/// longer on disk, and a copy that gives up halfway may not leave bytes nothing
/// refers to.
/// </remarks>
public class PartialFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mightdo-partial-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AStatusDeletionThatFailsHalfwayResyncsAndSaysSo()
    {
        var workspace = new Core.Storage.Workspace(_root);
        using var session = await WorkspaceSession.OpenAsync(new TaskStore(workspace));

        var doomed = await session.AddStatusAsync("Doomed", StatusType.Active);
        var active = session.Snapshot.Config.Statuses.First(
            s => s.Type == StatusType.Active && s.Id != doomed.Id);

        foreach (var i in Enumerable.Range(0, 3))
        {
            var created = await session.CreateTaskAsync($"Task {i}");
            await session.MoveToStatusAsync(created, doomed.Id);
        }

        // A directory where the atomic write wants to put its temp file, so the
        // last task in the batch cannot be saved and the ones before it already
        // have been.
        var tasks = session.Snapshot.Tasks.Where(t => t.StatusId == doomed.Id).ToList();
        var blocked = tasks[^1];
        Directory.CreateDirectory(workspace.TaskFile(blocked.Id) + ".tmp");

        await Assert.ThrowsAsync<PartiallyAppliedException>(
            () => session.DeleteStatusAsync(doomed.Id, active.Id));

        // Memory matches disk: the tasks that were written stayed written, the
        // one that wasn't didn't, and the status is still there because the
        // config was never reached.
        var onDisk = await new TaskStore(new Core.Storage.Workspace(_root)).LoadAsync();
        Assert.NotNull(session.Snapshot.Config.StatusById(doomed.Id));
        Assert.NotNull(onDisk.Config.StatusById(doomed.Id));

        foreach (var task in onDisk.Tasks)
        {
            Assert.Equal(task.StatusId, session.Snapshot.TaskById(task.Id)!.StatusId);
        }

        Assert.Equal(doomed.Id, session.Snapshot.TaskById(blocked.Id)!.StatusId);
        Assert.Contains(onDisk.Tasks, t => t.StatusId == active.Id);
    }

    [Fact]
    public async Task ADomainRefusalIsStillADomainRefusalNotAPartialApplication()
    {
        using var session = await WorkspaceSession.OpenAsync(
            new TaskStore(new Core.Storage.Workspace(_root)));

        var doomed = await session.AddStatusAsync("Doomed", StatusType.Active);

        await Assert.ThrowsAsync<ArgumentException>(
            () => session.DeleteStatusAsync(doomed.Id, "01m07z0000000000000000gone"));
    }

    [Fact]
    public async Task ALargeAttachmentDoesNotHoldUpEveryOtherWrite()
    {
        // The copy is the one write whose size the user chooses. Inside the
        // session's gate it would stall every save, reminder and rescan behind
        // it for as long as it took.
        var workspace = new Core.Storage.Workspace(_root);
        using var session = await WorkspaceSession.OpenAsync(new TaskStore(workspace));
        var task = await session.CreateTaskAsync("Has an attachment");

        var source = Path.Combine(_root, "big.bin");
        await File.WriteAllBytesAsync(source, new byte[2 * 1024 * 1024], CancellationToken.None);

        // The first progress report holds the copy open, which stands in for a
        // file large enough to take a while. Reported on the copying thread, so
        // blocking there really does stop the copy where it is.
        var started = new TaskCompletionSource();
        var mayFinish = new TaskCompletionSource();
        var holding = false;
        var progress = new SynchronousProgress(_ =>
        {
            if (holding) return;

            holding = true;
            started.TrySetResult();
            mayFinish.Task.Wait(TimeSpan.FromSeconds(10));
        });

        var attaching = Task.Run(() => session.AttachFileAsync(task, source, progress));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Mid-copy, an ordinary edit still lands rather than queueing behind it.
        var edited = await session.EditTaskAsync(
            task, current => current with { Summary = "Edited mid-copy" })
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Edited mid-copy", edited.Summary);

        mayFinish.SetResult();
        var attached = await attaching;
        Assert.Single(attached.Attachments);
        Assert.Equal("Edited mid-copy", session.Snapshot.TaskById(task.Id)!.Summary);
    }

    [Fact]
    public async Task ACopyReportsItsProgressAndFinishesOnTheFileSize()
    {
        var workspace = new Core.Storage.Workspace(_root);
        workspace.EnsureLayout();

        var source = Path.Combine(_root, "source.bin");
        var size = (3 * 1024 * 1024) + 17;
        await File.WriteAllBytesAsync(source, new byte[size], CancellationToken.None);

        var reports = new List<long>();
        var attachment = await new TaskStore(workspace).CopyAttachmentAsync(
            source, DateTime.UtcNow, new SynchronousProgress(reports.Add));

        Assert.NotEmpty(reports);
        Assert.Equal(reports.OrderBy(bytes => bytes), reports);
        Assert.Equal(size, reports[^1]);
        Assert.Equal(size, attachment.SizeBytes);
    }

    /// <summary>
    /// Reports on the thread that copies, so a test can read what it collected
    /// without waiting for another one to post it.
    /// </summary>
    private sealed class SynchronousProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    [Fact]
    public async Task AnAttachmentCopyThatFailsLeavesNothingBehind()
    {
        var workspace = new Core.Storage.Workspace(_root);
        workspace.EnsureLayout();

        var source = Path.Combine(_root, "source.bin");
        await File.WriteAllBytesAsync(source, new byte[64 * 1024], CancellationToken.None);

        var store = new TaskStore(workspace);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.CopyAttachmentAsync(
                source, DateTime.UtcNow, cancellationToken: cancelled.Token));

        // The bytes were opened and the destination created before the copy gave
        // up; nothing collects a file no task refers to, so it has to go now.
        Assert.Empty(Directory.GetFiles(workspace.AttachmentsDir));
    }
}

/// <summary>
/// A config that parses but doesn't describe a workspace anything can run on.
/// </summary>
/// <remarks>
/// Task files are refused at this boundary when they break their invariants.
/// <c>config.json</c> is the file a hand-edit or a sync merge is most likely to
/// break, and the damage is quiet: tasks written into a status id nothing
/// resolves, and a board missing columns.
/// </remarks>
public class InvalidConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mightdo-invalidconfig-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private async Task<UnreadableConfigException> RefusalAfterAsync(
        Func<JsonNode, JsonNode> edit)
    {
        var workspace = new Core.Storage.Workspace(_root);
        await new TaskStore(workspace).InitialiseAsync();

        var config = JsonNode.Parse(await File.ReadAllTextAsync(
            workspace.ConfigFile, CancellationToken.None))!;
        await File.WriteAllTextAsync(
            workspace.ConfigFile, edit(config).ToJsonString(), CancellationToken.None);

        return await Assert.ThrowsAsync<UnreadableConfigException>(
            () => new TaskStore(new Core.Storage.Workspace(_root)).LoadAsync());
    }

    [Fact]
    public async Task RefusesAConfigWhoseDefaultStatusDoesNotExist()
    {
        var error = await RefusalAfterAsync(config =>
        {
            config["defaultStatusId"] = "01m07z0000000000000000gone";
            return config;
        });

        Assert.Contains("config.json", error.Message);
        Assert.Contains("defaultStatusId", error.Message);
    }

    [Fact]
    public async Task RefusesANullDefaultStatusEvenThoughTheKeyIsPresent() =>
        // `required` only means the key is there; null deserialises happily.
        Assert.Contains("defaultStatusId", (await RefusalAfterAsync(config =>
        {
            config["defaultStatusId"] = null;
            return config;
        })).Message);

    [Fact]
    public async Task RefusesADefaultStatusThatIsNotAnInitialStatus() =>
        Assert.Contains("Initial", (await RefusalAfterAsync(config =>
        {
            var active = config["statuses"]!.AsArray()
                .First(s => s!["type"]!.GetValue<string>() == "active");
            config["defaultStatusId"] = active!["id"]!.GetValue<string>();
            return config;
        })).Message);

    [Fact]
    public async Task RefusesAConfigWithNoStatusOfSomeType() =>
        Assert.Contains("Final", (await RefusalAfterAsync(config =>
        {
            config["statuses"] = new JsonArray(
                [.. config["statuses"]!.AsArray()
                    .Where(s => s!["type"]!.GetValue<string>() != "final")
                    .Select(s => JsonNode.Parse(s!.ToJsonString()))]);
            return config;
        })).Message);

    [Fact]
    public async Task StillOpensAWorkspaceWhoseConfigIsFine()
    {
        var store = new TaskStore(new Core.Storage.Workspace(_root));
        await store.InitialiseAsync();

        var reopened = await new TaskStore(new Core.Storage.Workspace(_root)).LoadAsync();

        Assert.NotEmpty(reopened.Config.Statuses);
    }
}

/// <summary>
/// What a rescan counts as a change, which is what decides whether the user is
/// shown one.
/// </summary>
public class SnapshotComparisonTests
{
    private static readonly WorkspaceConfig Config = WorkspaceConfig.Seed();

    private static WorkspaceSnapshot SnapshotWithBrokenFile(string fileName) =>
        new(Config,
            [],
            [new TaskLoadFailure(fileName, new IOException("could not be read"))],
            [],
            DateTimeOffset.UnixEpoch);

    /// <summary>
    /// One file being repaired while another breaks inside the same debounce
    /// window is a change, even though the tally is unmoved.
    /// </summary>
    /// <remarks>
    /// Counting the failures made those two rescans identical, so the reload
    /// returned early and the "Unreadable" list kept naming the file that was
    /// now fine — until something unrelated happened to change.
    /// </remarks>
    [Fact]
    public void ASwappedUnreadableFileIsAChange() =>
        Assert.False(SnapshotWithBrokenFile("a.json")
            .HasSameContentAs(SnapshotWithBrokenFile("b.json")));

    [Fact]
    public void TheSameUnreadableFileIsNot() =>
        Assert.True(SnapshotWithBrokenFile("a.json")
            .HasSameContentAs(SnapshotWithBrokenFile("a.json")));

    /// <summary>A config read as a different format version is a different config.</summary>
    [Fact]
    public void ASchemaVersionChangeIsAChange() =>
        Assert.False(Config.HasSameContentAs(Config with { SchemaVersion = 2 }));
}
