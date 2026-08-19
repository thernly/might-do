using System.Security.Cryptography;
using System.Text;

namespace MightDo.Core.Storage;

/// <summary>
/// A machine-wide lock on one workspace, held for the length of a single save.
/// </summary>
/// <remarks>
/// Comparing the file against the version it was read from keeps a save from
/// destroying somebody else's edit, but the compare and the replace are two
/// filesystem operations: two copies of the app that both read version V can
/// both find V still there and both go on to overwrite it, and the loser's edit
/// is gone without even a conflict copy. Holding this across the whole
/// compare-preserve-replace sequence makes that interleaving impossible between
/// processes on this machine.
/// <para>
/// It does nothing for two machines writing the same synced folder, and cannot:
/// a lock file inside the workspace is itself synced, arriving seconds or
/// minutes after it was taken. That case is left to the sync client's own
/// conflict copies, which the app already surfaces — see
/// <c>docs/format/workspace-v1.md</c>.
/// </para>
/// <para>
/// The lock lives beside the machine's temporary files rather than in the
/// workspace, so nothing the user syncs, backs up or looks at ever contains it.
/// It is held by an open handle, not by the file existing, so a process that
/// crashes releases it: the operating system closes the handle, and the empty
/// file left behind is opened again by whoever comes next.
/// </para>
/// </remarks>
internal sealed class WorkspaceLock : IDisposable
{
    /// <summary>
    /// How long to wait for another process before writing anyway. A save the
    /// user asked for that never happens is worse than one that races.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(15);

    /// <summary>
    /// Set once this machine has proved it will not give us a lock file at all,
    /// so no later save waits out the timeout again.
    /// </summary>
    /// <remarks>
    /// A read-only or sandboxed temporary folder, or a lock file left by another
    /// user in a shared one, fails the same way on every attempt: waiting five
    /// seconds for permission that is never coming, on every save, and five per
    /// task in a cascade. Contention is not remembered this way — that one is
    /// transient, and the next save is entitled to its own wait.
    /// </remarks>
    private static volatile bool _unavailable;

    private readonly FileStream? _held;

    private WorkspaceLock(FileStream? held) => _held = held;

    public static async Task<WorkspaceLock> AcquireAsync(
        string root, CancellationToken cancellationToken = default)
    {
        if (_unavailable) return new WorkspaceLock(null);

        var path = PathFor(root);
        if (path is null)
        {
            _unavailable = true;
            return new WorkspaceLock(null);
        }

        // Real time rather than the session's clock: this waits on another
        // process, which a test's fake clock has no say over.
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return new WorkspaceLock(new FileStream(
                    path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (UnauthorizedAccessException)
            {
                // Not contention: permission does not arrive by waiting. The
                // save still has to happen, and every later one skips the wait.
                _unavailable = true;
                return new WorkspaceLock(null);
            }
            catch (IOException)
            {
                // Somebody else is mid-save. Contention is transient, so this
                // one is waited out rather than remembered — the next save gets
                // the same five seconds.
                if (DateTime.UtcNow >= deadline) return new WorkspaceLock(null);
            }

            await Task.Delay(Retry, cancellationToken);
        }
    }

    /// <summary>
    /// Where this workspace's lock lives, or null if the machine has nowhere to
    /// put one.
    /// </summary>
    /// <remarks>
    /// Named by a hash of the path so that any path — long, unicode, on a drive
    /// letter — becomes one plain file name, and two processes that opened the
    /// same folder agree on it.
    /// </remarks>
    private static string? PathFor(string root)
    {
        try
        {
            var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)));
            var dir = Path.Combine(Path.GetTempPath(), "mightdo-locks");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"{name}.lock");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Releases the lock. The file is left behind on purpose: deleting it is a
    /// race of its own, and an empty file in the temporary folder costs nothing.
    /// </summary>
    public void Dispose() => _held?.Dispose();
}
