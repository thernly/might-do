using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Time.Testing;
using MightDo.App.ViewModels;
using MightDo.Core.Domain;
using MightDo.Core.Reminders;
using MightDo.Core.Session;
using MightDo.Core.Storage;
using MightDo.Platform;

namespace MightDo.App.Tests;

/// <summary>
/// What the app layer is wired out of, rather than what it shows.
/// </summary>
/// <remarks>
/// The view model used to build a real <c>FileSystemWatcher</c> and a notifier
/// that shells out to the desktop in its own constructor, so the one thing most
/// likely to break — what this projection does when a rescan and a reminder tick
/// land on it — could only be tested by sleeping and hoping.
/// </remarks>
public class WorkspaceServicesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mightdo-wiring-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>
    /// Started at the wall clock, because a reminder due "a minute ago" has to
    /// be a minute ago on the clock the scheduler reads — which is this one.
    /// </summary>
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly RecordingNotifier _notifier = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact]
    public async Task AReminderFiresOnAClockTheTestOwns()
    {
        var folder = Path.Combine(_root, "ws");
        var store = new TaskStore(new Core.Storage.Workspace(folder));

        // Seeded through its own session, which is closed again before the view
        // model opens one of its own on the same folder.
        using (var seed = await WorkspaceSession.OpenAsync(
                   store, cancellationToken: TestContext.Current.CancellationToken))
        {
            var task = await seed.CreateTaskAsync(
                "Ring the dentist", cancellationToken: TestContext.Current.CancellationToken);
            await seed.AddReminderAsync(
                task,
                _time.GetUtcNow().UtcDateTime.AddMinutes(-1),
                TestContext.Current.CancellationToken);
        }

        var settings = AppSettings.Load(Path.Combine(_root, "settings.json"));
        using var workspace = await WorkspaceViewModel.OpenAsync(
            new TaskStore(new Core.Storage.Workspace(folder)),
            settings,
            new NoPicker(),
            Services());

        // The scheduler's first tick is the one the timer is created with, and
        // the fake clock is what releases it.
        _time.Advance(TimeSpan.FromSeconds(1));

        var notified = await Task.WhenAny(_notifier.Notified, Task.Delay(10_000));
        Dispatcher.UIThread.RunJobs();

        Assert.Same(_notifier.Notified, notified);
        Assert.Equal("Ring the dentist", Assert.Single(_notifier.Summaries));

        // And the tick reached the projection, which is the wiring this is about.
        Assert.Single(workspace.OutstandingReminders);
    }

    [AvaloniaFact]
    public async Task TheWatcherTheShellSuppliesIsTheOneTheWorkspaceUses()
    {
        var folder = Path.Combine(_root, "ws");
        var store = new TaskStore(new Core.Storage.Workspace(folder));
        await store.InitialiseAsync(TestContext.Current.CancellationToken);

        var settings = AppSettings.Load(Path.Combine(_root, "settings.json"));
        settings.AddWorkspace(folder);

        var main = new MainViewModel(settings, new NoPicker(), new NoPicker(), Services());
        await main.InitialiseAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(main.Workspace);

        // A task written by something else — another machine, a sync client —
        // reaches the list only because the watcher asks for a rescan, and here
        // that watcher is running on the test's clock.
        using (var other = await WorkspaceSession.OpenAsync(
                   new TaskStore(new Core.Storage.Workspace(folder)),
                   cancellationToken: TestContext.Current.CancellationToken))
        {
            await other.CreateTaskAsync(
                "Arrived from elsewhere",
                cancellationToken: TestContext.Current.CancellationToken);
        }

        main.Workspace!.RefreshInBackground();
        await main.Workspace.PendingBackgroundWork;
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(main.Workspace.Tasks, row => row.Summary == "Arrived from elsewhere");

        main.Workspace.Dispose();
    }

    private WorkspaceServices Services() => new(
        session => new WorkspaceWatcher(session.Workspace, _time),
        session => new ReminderScheduler(session, _notifier, _time))
    {
        ReminderInterval = TimeSpan.FromSeconds(1),
    };

    /// <summary>A notifier that records rather than talking to the desktop.</summary>
    private sealed class RecordingNotifier : IReminderNotifier
    {
        private readonly TaskCompletionSource _notified =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Summaries { get; } = [];

        /// <summary>Completes when the first reminder has been notified.</summary>
        public Task Notified => _notified.Task;

        public Task NotifyAsync(
            MightDoTask task, Reminder reminder, CancellationToken cancellationToken)
        {
            lock (Summaries) Summaries.Add(task.Summary);
            _notified.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class NoPicker : IFolderPicker, IFilePicker
    {
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title) => Task.FromResult<string?>(null);
    }
}

/// <summary>
/// Waiting on a view model's in-flight work.
/// </summary>
/// <remarks>
/// A single <c>Pending = DoAsync()</c> slot forgot every task but the last, so
/// a failure in the first vanished and a test awaiting the slot could be
/// awaiting the wrong write.
/// </remarks>
public class PendingWorkTests
{
    [Fact]
    public async Task WaitsForTheEarlierTaskAsWellAsTheLater()
    {
        var pending = new PendingWork();
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();

        _ = pending.Add(first.Task);
        _ = pending.Add(second.Task);

        second.SetResult();
        await Task.Yield();

        Assert.False(pending.All.IsCompleted);

        first.SetResult();
        await pending.All;
    }

    [Fact]
    public void HandsBackTheTaskItWasGiven()
    {
        var pending = new PendingWork();
        var work = Task.CompletedTask;

        Assert.Same(work, pending.Add(work));
    }
}

/// <summary>
/// Whether a remembered folder is there, asked without stopping the UI.
/// </summary>
/// <remarks>
/// The switcher asks once per row every time it is rebuilt, and it is rebuilt
/// during window open. On an unmounted share a single <c>Directory.Exists</c>
/// blocks for seconds, so the answers arrive after the rows do.
/// </remarks>
public class WorkspaceAvailabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mightdo-availability-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact]
    public async Task MarksAFolderThatIsNotThereOnceTheProbeComesBack()
    {
        Directory.CreateDirectory(_root);
        var settings = AppSettings.Load(Path.Combine(_root, "settings.json"));
        settings.AddWorkspace(Path.Combine(_root, "gone"));

        var main = new MainViewModel(settings, new NoPicker(), new NoPicker());
        await main.InitialiseAsync();

        await main.PendingAvailability;
        Dispatcher.UIThread.RunJobs();

        Assert.True(Assert.Single(main.Workspaces).IsMissing);
    }

    [AvaloniaFact]
    public async Task LeavesAFolderThatIsThereUnmarked()
    {
        var folder = Path.Combine(_root, "ws");
        var store = new TaskStore(new Core.Storage.Workspace(folder));
        await store.InitialiseAsync(TestContext.Current.CancellationToken);

        var settings = AppSettings.Load(Path.Combine(_root, "settings.json"));
        settings.AddWorkspace(folder);

        var main = new MainViewModel(settings, new NoPicker(), new NoPicker());
        main.CloseWorkspace();

        await main.PendingAvailability;
        Dispatcher.UIThread.RunJobs();

        Assert.False(Assert.Single(main.Workspaces).IsMissing);
    }

    private sealed class NoPicker : IFolderPicker, IFilePicker
    {
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title) => Task.FromResult<string?>(null);
    }
}
