namespace MightDo.Core.Session;

/// <summary>
/// Turns filesystem noise into a request to reload.
/// </summary>
/// <remarks>
/// Per ADR-0003, a watch event is a hint and nothing more: the event's kind and
/// path are ignored entirely, because on macOS an in-place rewrite is reported
/// as <c>Created</c> and a file moved out of a directory is reported as
/// <c>Created</c> at its old path. One save also produces three to five events —
/// five inside OneDrive, where the sync client touches the file after we write
/// it — so debouncing is mandatory rather than an optimisation.
/// <para>
/// This type holds no <see cref="Storage.TaskStore"/> and no
/// <see cref="WorkspaceSession"/>, so it structurally cannot write to the
/// workspace in response to an event. That is ADR-0003's load-bearing rule made
/// impossible to break rather than merely documented.
/// </para>
/// </remarks>
public sealed class WorkspaceWatcher : IDisposable
{
    /// <summary>Long enough to swallow a sync client's burst, short enough to feel live.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// How often to check the workspace folder still exists. Deleting a watched
    /// root produces no events at all (measured — see ADR-0003), so an unmounted
    /// drive or a moved folder has to be asked about rather than waited for.
    /// </summary>
    public static readonly TimeSpan DefaultExistencePoll = TimeSpan.FromSeconds(15);

    private readonly Storage.Workspace _workspace;
    private readonly TimeProvider _time;
    private readonly TimeSpan _debounce;
    private readonly Lock _gate = new();

    private FileSystemWatcher? _watcher;
    private ITimer? _debounceTimer;
    private ITimer? _pollTimer;
    private bool _rootWasPresent = true;
    private bool _disposed;

    public WorkspaceWatcher(
        Storage.Workspace workspace,
        TimeProvider? time = null,
        TimeSpan? debounce = null,
        TimeSpan? existencePoll = null)
    {
        _workspace = workspace;
        _time = time ?? TimeProvider.System;
        _debounce = debounce ?? DefaultDebounce;
        ExistencePoll = existencePoll ?? DefaultExistencePoll;
    }

    public TimeSpan ExistencePoll { get; }

    /// <summary>
    /// Something under the workspace changed. Already coalesced, and says
    /// nothing about what changed — the handler should reload everything.
    /// </summary>
    public event EventHandler? RescanRequested;

    /// <summary>The workspace folder is no longer there.</summary>
    public event EventHandler? RootVanished;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            _pollTimer ??= _time.CreateTimer(
                _ => CheckRootExists(), null, ExistencePoll, ExistencePoll);

            if (_watcher is not null || !Directory.Exists(_workspace.Root)) return;

            // The whole root, not just tasks/: the Flutter implementation
            // watches only the tasks folder and so never notices an external
            // edit to config.json, even though it reloads it.
            _watcher = new FileSystemWatcher(_workspace.Root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                               | NotifyFilters.DirectoryName
                               | NotifyFilters.LastWrite
                               | NotifyFilters.Size,
            };

            _watcher.Created += OnChanged;
            _watcher.Changed += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Renamed += OnChanged;

            // Watching is a convenience; losing it shouldn't take the app down,
            // and the manual refresh and the existence poll both still work.
            _watcher.Error += (_, _) => OnWatcherError();

            _watcher.EnableRaisingEvents = true;
        }
    }

    /// <summary>Requests a rescan as though the filesystem had reported one.</summary>
    public void Poke() => OnChanged(this, EventArgs.Empty);

    /// <summary>
    /// Recovers from a watcher error by taking a fresh handle and rescanning.
    /// </summary>
    /// <remarks>
    /// The usual cause is the watcher's internal buffer overflowing, which is
    /// what a sync client landing a few hundred files at once produces. After
    /// that the watcher never raises another change event, so merely declining
    /// to crash would leave live reload dead for the rest of the session in the
    /// exact case it matters most. The rescan covers whatever was missed.
    /// </remarks>
    private void OnWatcherError()
    {
        lock (_gate)
        {
            if (_disposed) return;

            // The root may be gone, in which case Start() takes no handle and
            // the existence poll picks it up when it comes back.
            Restart();
        }

        Poke();
    }

    private void OnChanged(object? sender, EventArgs args)
    {
        if (args is FileSystemEventArgs file
            && file.Name?.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) == true)
        {
            return; // our own in-flight write
        }

        lock (_gate)
        {
            if (_disposed) return;

            // Restart the window on every event, so a burst collapses into one.
            _debounceTimer?.Dispose();
            _debounceTimer = _time.CreateTimer(
                _ => Fire(), null, _debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        RescanRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CheckRootExists()
    {
        bool vanished;
        lock (_gate)
        {
            if (_disposed) return;

            var present = _workspace.Exists;
            vanished = _rootWasPresent && !present;
            _rootWasPresent = present;

            // The folder came back — an unmounted drive remounted, say. The
            // watcher's handle is stale, so take a fresh one.
            if (present && _watcher is null) Restart();
        }

        if (vanished) RootVanished?.Invoke(this, EventArgs.Empty);
    }

    private void Restart()
    {
        _watcher?.Dispose();
        _watcher = null;
        Start();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            _debounceTimer?.Dispose();
            _pollTimer?.Dispose();
            _watcher?.Dispose();
            _debounceTimer = null;
            _pollTimer = null;
            _watcher = null;
        }
    }
}
