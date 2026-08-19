using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MightDo.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread, immediately when it is
    /// already the one calling.
    /// </summary>
    /// <remarks>
    /// <see cref="Core.Session.WorkspaceSession.Changed"/> is raised on whichever
    /// thread finished the work, which for a rescan is a background one, and
    /// every view model that subscribes goes on to rebuild a bound collection.
    /// Doing that off the UI thread is a cross-thread access, so this lives on
    /// the base type rather than in one subscriber: a second subscriber that has
    /// to reinvent the rule is a second subscriber that can forget it.
    /// </remarks>
    protected static void OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    /// <summary>
    /// Whether an exception is the application closing rather than something
    /// that went wrong.
    /// </summary>
    /// <remarks>
    /// A cancelled or disposed session is a workspace being closed or switched,
    /// which the user asked for. Reporting it would put an error in front of
    /// somebody for doing exactly what they meant to.
    /// </remarks>
    protected static bool IsShutdown(Exception error) =>
        error is OperationCanceledException or ObjectDisposedException;
}

/// <summary>
/// Every guarded task a view model has in flight, as one task to wait on.
/// </summary>
/// <remarks>
/// A single <c>Pending = DoAsync()</c> slot is last-writer-wins: two quick
/// edits leave the first task untracked, and a caller waiting on the slot is
/// waiting on the wrong write. Waiting on all of them is what "the pane has
/// finished saving" actually means, and it is what tests are asking for when
/// they await it.
/// <para>
/// The work is not serialised, only tracked — each task still starts when it is
/// added, and the session's own gate decides what order the writes land in.
/// Every task added here has already been guarded, so none of them faults and
/// the combined task cannot either.
/// </para>
/// </remarks>
public sealed class PendingWork
{
    private Task _all = Task.CompletedTask;

    /// <summary>Everything added so far, completing when the last of it does.</summary>
    public Task All => Volatile.Read(ref _all);

    /// <summary>Tracks <paramref name="work"/> and hands it straight back.</summary>
    public Task Add(Task work)
    {
        Volatile.Write(ref _all, Both(Volatile.Read(ref _all), work));
        return work;
    }

    private static async Task Both(Task earlier, Task later) =>
        await Task.WhenAll(earlier, later);
}

