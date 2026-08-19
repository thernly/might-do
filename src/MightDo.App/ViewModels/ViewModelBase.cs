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
