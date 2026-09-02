using System.Reflection;
using Avalonia.Threading;
using MightDo.Platform;

namespace MightDo.App;

/// <summary>Installs the last-chance managed exception recorders for the app.</summary>
internal static class CrashDiagnostics
{
    private sealed record OperationState(string Name, string? TaskId);

    private static readonly AsyncLocal<OperationState?> Current = new();
    private static ManagedCrashLog _log = new(ManagedCrashLog.DefaultPath);
    private static readonly string Build =
        Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? "unknown";

    private static int _processHandlersInstalled;
    private static int _dispatcherHandlerInstalled;

    public static string LogPath => Volatile.Read(ref _log).Path;

    /// <summary>Redirects diagnostics before a headless test application starts.</summary>
    internal static void ConfigureLog(ManagedCrashLog log) =>
        Volatile.Write(ref _log, log);

    /// <summary>Installs handlers that are safe before Avalonia is initialised.</summary>
    public static void InstallProcessHandlers()
    {
        if (Interlocked.Exchange(ref _processHandlersInstalled, 1) != 0) return;

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var error = args.ExceptionObject as Exception
                        ?? new InvalidOperationException(
                            $"An unhandled non-Exception object escaped: {args.ExceptionObject}");
            Record(error, "AppDomain.UnhandledException");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
            Record(args.Exception, "TaskScheduler.UnobservedTaskException");
    }

    /// <summary>
    /// Records dispatcher exceptions without marking an unknown failure handled.
    /// Avalonia therefore keeps its normal fail-fast behaviour, but the next
    /// crash report has a managed companion with the useful part of the stack.
    /// </summary>
    public static void InstallDispatcherHandler()
    {
        if (Interlocked.Exchange(ref _dispatcherHandlerInstalled, 1) != 0) return;

        Dispatcher.UIThread.UnhandledException += (_, args) =>
            Record(args.Exception, "Avalonia.Dispatcher.UnhandledException");
    }

    public static IDisposable Begin(string operation, string? taskId = null)
    {
        var previous = Current.Value;
        Current.Value = new OperationState(operation, taskId);
        return new Scope(previous);
    }

    public static void Record(Exception exception, string origin)
    {
        var operation = Current.Value;
        Volatile.Read(ref _log).Record(exception, new ManagedCrashContext(
            origin,
            Build,
            operation?.Name,
            operation?.TaskId));
    }

    private sealed class Scope(OperationState? previous) : IDisposable
    {
        private OperationState? _previous = previous;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Current.Value = _previous;
            _previous = null;
        }
    }
}
