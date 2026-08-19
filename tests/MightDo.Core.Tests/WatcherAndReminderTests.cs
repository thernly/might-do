using Microsoft.Extensions.Time.Testing;
using MightDo.Core.Domain;
using MightDo.Core.Reminders;
using MightDo.Core.Session;
using MightDo.Core.Storage;

namespace MightDo.Core.Tests;

public class WorkspaceWatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mightdo-watch-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly FakeTimeProvider _time = new();
    private readonly Core.Storage.Workspace _workspace;

    public WorkspaceWatcherTests()
    {
        _workspace = new Core.Storage.Workspace(_root);
        _workspace.EnsureLayout();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private WorkspaceWatcher Watcher(TimeSpan? debounce = null) =>
        new(_workspace, _time, debounce ?? TimeSpan.FromMilliseconds(400));

    [Fact]
    public void CollapsesABurstIntoOneRescan()
    {
        // ADR-0003 measured three to five events per save, and more inside
        // OneDrive. Without this the app rescans the whole workspace five times
        // for one keystroke's worth of work.
        using var watcher = Watcher();
        var rescans = 0;
        watcher.RescanRequested += (_, _) => rescans++;
        watcher.Start();

        for (var i = 0; i < 5; i++) watcher.Poke();
        _time.Advance(TimeSpan.FromMilliseconds(400));

        Assert.Equal(1, rescans);
    }

    [Fact]
    public void StartsANewWindowForAnEventAfterTheLastOneClosed()
    {
        using var watcher = Watcher();
        var rescans = 0;
        watcher.RescanRequested += (_, _) => rescans++;
        watcher.Start();

        watcher.Poke();
        _time.Advance(TimeSpan.FromMilliseconds(400));
        watcher.Poke();
        _time.Advance(TimeSpan.FromMilliseconds(400));

        Assert.Equal(2, rescans);
    }

    [Fact]
    public void DoesNotFireBeforeTheWindowElapses()
    {
        using var watcher = Watcher();
        var rescans = 0;
        watcher.RescanRequested += (_, _) => rescans++;
        watcher.Start();

        watcher.Poke();
        _time.Advance(TimeSpan.FromMilliseconds(399));

        Assert.Equal(0, rescans);
    }

    [Fact]
    public void NoticesTheWorkspaceFolderVanishing()
    {
        // Deleting a watched root produces no filesystem events at all, so this
        // can only come from polling.
        using var watcher = Watcher();
        var vanished = 0;
        watcher.RootVanished += (_, _) => vanished++;
        watcher.Start();

        Directory.Delete(_root, recursive: true);
        _time.Advance(watcher.ExistencePoll);

        Assert.Equal(1, vanished);
    }

    [Fact]
    public void ReportsTheFolderVanishingOnceNotOnEveryPoll()
    {
        using var watcher = Watcher();
        var vanished = 0;
        watcher.RootVanished += (_, _) => vanished++;
        watcher.Start();

        Directory.Delete(_root, recursive: true);
        _time.Advance(watcher.ExistencePoll);
        _time.Advance(watcher.ExistencePoll);
        _time.Advance(watcher.ExistencePoll);

        Assert.Equal(1, vanished);
    }

    [Fact]
    public void SaysNothingWhileTheFolderIsPresent()
    {
        using var watcher = Watcher();
        var vanished = 0;
        watcher.RootVanished += (_, _) => vanished++;
        watcher.Start();

        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(0, vanished);
    }

    [Fact]
    public void StopsFiringOnceDisposed()
    {
        var watcher = Watcher();
        var rescans = 0;
        watcher.RescanRequested += (_, _) => rescans++;
        watcher.Start();

        watcher.Poke();
        watcher.Dispose();
        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(0, rescans);
    }

    [Fact]
    public async Task NeverWritesToTheWorkspace()
    {
        // ADR-0003's load-bearing rule. The watcher holds no store and no
        // session, so this is structural — but it is cheap to prove, and the
        // consequence of getting it wrong is a write loop with a sync client.
        var store = new TaskStore(_workspace);
        using var session = await WorkspaceSession.OpenAsync(store, _time);
        await session.CreateTaskAsync("Untouched");

        var before = Snapshot();
        using var watcher = Watcher();
        watcher.RescanRequested += (_, _) => { };
        watcher.Start();

        watcher.Poke();
        _time.Advance(TimeSpan.FromSeconds(1));
        _time.Advance(watcher.ExistencePoll);

        Assert.Equal(before, Snapshot());

        Dictionary<string, DateTime> Snapshot() =>
            Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
                .ToDictionary(f => f, File.GetLastWriteTimeUtc);
    }
}

public class ReminderSchedulerTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mightdo-remind-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly FakeTimeProvider _time =
        new(new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.Zero));

    private WorkspaceSession _session = null!;

    public async Task InitializeAsync() =>
        _session = await WorkspaceSession.OpenAsync(
            new TaskStore(new Core.Storage.Workspace(_root)), _time);

    public Task DisposeAsync()
    {
        _session.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    [Fact]
    public async Task FiresAReminderThatHasComeDue()
    {
        var task = await _session.CreateTaskAsync("Remind me");
        await _session.AddReminderAsync(_session.Snapshot.TaskById(task.Id)!, Now.AddMinutes(-1));

        using var scheduler = new ReminderScheduler(_session, time: _time);
        var fired = await scheduler.TickAsync();

        Assert.Single(fired);
        Assert.NotNull(_session.Snapshot.TaskById(task.Id)!.Reminders.Single().FiredAt);
    }

    [Fact]
    public async Task DoesNotFireAReminderThatIsNotDueYet()
    {
        var task = await _session.CreateTaskAsync("Later");
        await _session.AddReminderAsync(_session.Snapshot.TaskById(task.Id)!, Now.AddHours(1));

        using var scheduler = new ReminderScheduler(_session, time: _time);

        Assert.Empty(await scheduler.TickAsync());
    }

    [Fact]
    public async Task TwoRemindersDueOnOneTaskBothFireAndNeitherRefires()
    {
        // Applying each firing to a task captured before the loop would have
        // the second write discard the first's firedAt, and that reminder would
        // re-fire on every tick forever.
        var task = await _session.CreateTaskAsync("Two at once");
        await _session.AddReminderAsync(_session.Snapshot.TaskById(task.Id)!, Now.AddMinutes(-2));
        await _session.AddReminderAsync(_session.Snapshot.TaskById(task.Id)!, Now.AddMinutes(-1));

        using var scheduler = new ReminderScheduler(_session, time: _time);
        var first = await scheduler.TickAsync();
        var second = await scheduler.TickAsync();

        Assert.Equal(2, first.Count);
        Assert.Empty(second);
        Assert.All(_session.Snapshot.TaskById(task.Id)!.Reminders,
            reminder => Assert.NotNull(reminder.FiredAt));
    }

    [Fact]
    public async Task AFiredReminderStaysInTheInAppPanelUntilDismissed()
    {
        // ADR-0004: the in-app surface is the contract. This is what makes
        // "open the app after two days away and nothing is missed" work.
        var task = await _session.CreateTaskAsync("Nagging");
        await _session.AddReminderAsync(_session.Snapshot.TaskById(task.Id)!, Now.AddMinutes(-1));

        using var scheduler = new ReminderScheduler(_session, time: _time);
        await scheduler.TickAsync();

        Assert.Single(_session.Snapshot.OutstandingReminders(Now));
    }

    [Fact]
    public async Task ANotifierThatThrowsStillLeavesTheReminderFired()
    {
        var task = await _session.CreateTaskAsync("Broken notifier");
        await _session.AddReminderAsync(_session.Snapshot.TaskById(task.Id)!, Now.AddMinutes(-1));

        using var scheduler = new ReminderScheduler(_session, new ThrowingNotifier(), _time);
        var fired = await scheduler.TickAsync();

        Assert.Single(fired);
        Assert.NotNull(_session.Snapshot.TaskById(task.Id)!.Reminders.Single().FiredAt);

        // And it does not fire again on the next tick.
        Assert.Empty(await scheduler.TickAsync());
    }

    [Fact]
    public async Task NotifiesOncePerDueReminder()
    {
        var task = await _session.CreateTaskAsync("Counted");
        await _session.AddReminderAsync(_session.Snapshot.TaskById(task.Id)!, Now.AddMinutes(-2));
        await _session.AddReminderAsync(_session.Snapshot.TaskById(task.Id)!, Now.AddMinutes(-1));
        var notifier = new CountingNotifier();

        using var scheduler = new ReminderScheduler(_session, notifier, _time);
        await scheduler.TickAsync();
        await scheduler.TickAsync();

        Assert.Equal(2, notifier.Count);
    }

    [Fact]
    public async Task OutstandingRemindersComeBackNewestFirst()
    {
        var task = await _session.CreateTaskAsync("Several");
        var current = _session.Snapshot.TaskById(task.Id)!;
        await _session.AddReminderAsync(current, Now.AddHours(-3));
        await _session.AddReminderAsync(_session.Snapshot.TaskById(task.Id)!, Now.AddHours(-1));
        await _session.AddReminderAsync(_session.Snapshot.TaskById(task.Id)!, Now.AddHours(-2));

        var outstanding = _session.Snapshot.OutstandingReminders(Now);

        Assert.Equal(
            [Now.AddHours(-1), Now.AddHours(-2), Now.AddHours(-3)],
            outstanding.Select(due => due.Reminder.RemindAt));
    }

    [Fact]
    public async Task ADismissedReminderIsNeitherFiredNorOutstanding()
    {
        var task = await _session.CreateTaskAsync("Dismissed early");
        await _session.AddReminderAsync(_session.Snapshot.TaskById(task.Id)!, Now.AddMinutes(-1));
        var reminder = _session.Snapshot.TaskById(task.Id)!.Reminders.Single();
        await _session.DismissRemindersAsync(
            _session.Snapshot.TaskById(task.Id)!, new HashSet<string> { reminder.Id });

        using var scheduler = new ReminderScheduler(_session, time: _time);
        var fired = await scheduler.TickAsync();

        Assert.Empty(fired);
        Assert.Empty(_session.Snapshot.OutstandingReminders(Now));
    }

    private sealed class ThrowingNotifier : IReminderNotifier
    {
        public Task NotifyAsync(
            MightDoTask task, Reminder reminder, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no notification service here");
    }

    private sealed class CountingNotifier : IReminderNotifier
    {
        public int Count { get; private set; }

        public Task NotifyAsync(
            MightDoTask task, Reminder reminder, CancellationToken cancellationToken)
        {
            Count++;
            return Task.CompletedTask;
        }
    }
}
