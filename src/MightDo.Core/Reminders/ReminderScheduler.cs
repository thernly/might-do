using MightDo.Core.Domain;
using MightDo.Core.Session;

namespace MightDo.Core.Reminders;

/// <summary>
/// Shows a reminder to the user, if the platform can.
/// </summary>
/// <remarks>
/// Per ADR-0004 the in-app surface is the contract and an OS notification is
/// best-effort, so a failure here is not a failure of the feature.
/// </remarks>
public interface IReminderNotifier
{
    Task NotifyAsync(MightDoTask task, Reminder reminder, CancellationToken cancellationToken);
}

/// <summary>Does nothing, successfully. The fallback where no platform notifier exists.</summary>
public sealed class NullReminderNotifier : IReminderNotifier
{
    public static readonly NullReminderNotifier Instance = new();

    public Task NotifyAsync(
        MightDoTask task, Reminder reminder, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>
/// Fires reminders that have come due, on a clock.
/// </summary>
/// <remarks>
/// Clock-driven, never event-driven: it writes to the workspace, and ADR-0003
/// forbids writing in response to a watch event. Nothing is registered with the
/// operating system, so nothing needs unregistering when a reminder is edited or
/// its task is trashed — and nothing fires while the app is closed, which is the
/// documented limit of this version.
/// </remarks>
public sealed class ReminderScheduler : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(20);

    private readonly WorkspaceSession _session;
    private readonly IReminderNotifier _notifier;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Cancelled on shutdown, so a tick stops rather than writing on.</summary>
    private readonly CancellationTokenSource _stopping = new();

    private ITimer? _timer;
    private bool _disposed;

    public ReminderScheduler(
        WorkspaceSession session,
        IReminderNotifier? notifier = null,
        TimeProvider? time = null)
    {
        _session = session;
        _notifier = notifier ?? NullReminderNotifier.Instance;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised after reminders have fired, for anything that wants to react.</summary>
    public event EventHandler<IReadOnlyList<DueReminder>>? Fired;

    /// <summary>
    /// A tick failed. Raised so the failure has somewhere to go other than a
    /// task nobody is holding.
    /// </summary>
    /// <remarks>
    /// Ticks run from a timer, so there is no caller to hand the exception to.
    /// Without this, a workspace that has become unwritable — a drive
    /// unmounted, permissions changed — silently stops marking reminders and
    /// re-shows the same ones forever, with nothing said.
    /// </remarks>
    public event EventHandler<Exception>? Failed;

    public void Start(TimeSpan? interval = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var period = interval ?? DefaultInterval;
        _timer ??= _time.CreateTimer(
            _ => _ = TickInBackgroundAsync(), null, TimeSpan.Zero, period);
    }

    /// <summary>
    /// A timer tick: the same work, with nowhere to throw.
    /// </summary>
    private async Task TickInBackgroundAsync()
    {
        try
        {
            await TickAsync(_stopping.Token);
        }
        catch (OperationCanceledException)
        {
            // Shutting down mid-tick is not a failure.
        }
        catch (Exception error)
        {
            Failed?.Invoke(this, error);
        }
    }

    /// <summary>
    /// Fires every reminder that is due and hasn't fired yet.
    /// </summary>
    /// <remarks>
    /// Reminders are grouped by task and applied in a single edit per task. The
    /// Flutter implementation marks them one at a time from a task captured
    /// before the loop, so with two reminders due at once the second write
    /// discards the first's <c>firedAt</c> and that reminder re-fires on every
    /// tick, forever — the exact loop marking-before-showing was meant to stop.
    /// <para>
    /// Marking happens before notifying, so a notifier that throws cannot cause
    /// the same reminder to fire again on the next tick.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<DueReminder>> TickAsync(
        CancellationToken cancellationToken = default)
    {
        if (_disposed) return [];

        // One tick at a time: a slow notifier must not overlap with the next.
        if (!await _gate.WaitAsync(0, cancellationToken)) return [];

        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var due = _session.Snapshot.Tasks
                .SelectMany(task => task.Reminders
                    .Where(reminder => reminder.IsPending && reminder.RemindAt <= now)
                    .Select(reminder => new DueReminder(task, reminder)))
                .ToList();

            if (due.Count == 0) return [];

            foreach (var group in due.GroupBy(d => d.Task.Id, StringComparer.Ordinal))
            {
                var task = _session.Snapshot.TaskById(group.Key);
                if (task is null) continue; // trashed since the snapshot was taken

                await _session.MarkRemindersFiredAsync(
                    task,
                    group.Select(d => d.Reminder.Id).ToHashSet(StringComparer.Ordinal),
                    cancellationToken);
            }

            foreach (var item in due)
            {
                try
                {
                    await _notifier.NotifyAsync(item.Task, item.Reminder, cancellationToken);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // ADR-0004: the OS banner is best-effort. The reminder stays
                    // in the in-app panel until dismissed, which is the promise.
                }
            }

            Fired?.Invoke(this, due);
            return due;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops the clock and tells any tick in flight to give up.
    /// </summary>
    /// <remarks>
    /// The gate is not disposed, for the reason given on
    /// <see cref="WorkspaceSession.Dispose"/>: a tick holding it when the app
    /// closes would fail on release, throwing from a background thread during
    /// an otherwise orderly shutdown.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        _stopping.Cancel();
    }
}
