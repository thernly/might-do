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
