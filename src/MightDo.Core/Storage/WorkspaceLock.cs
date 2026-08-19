using System.Security.Cryptography;
using System.Text;

namespace MightDo.Core.Storage;

/// <summary>
/// A change was refused because another process on this machine held the
/// workspace for longer than a save should take.
/// </summary>
/// <remarks>
/// Nothing was written. The alternative — writing without the lock — is the
/// interleaving <see cref="WorkspaceLock"/> exists to prevent, and it destroys
/// the other process's edit without so much as a conflict copy.
/// </remarks>
public sealed class WorkspaceBusyException(string root)
    : Exception(
        $"Another copy of MightDo is writing to {root} and did not finish in time. "
        + "Nothing has been changed. Try again in a moment.")
{
    /// <summary>The workspace that was busy.</summary>
    public string Root { get; } = root;
}

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
    /// How long to wait for another process before giving up.
    /// </summary>
    /// <remarks>
    /// This used to write anyway on the grounds that a save the user asked for
    /// that never happens is worse than one that races. It isn't: writing
    /// unlocked is exactly the interleaving this type exists to prevent, and
    /// doing it silently turns the workspace's no-data-loss guarantee into
    /// last-writer-wins at the moment contention proves the guarantee was
    /// needed. A change refused with a reason can be repeated; an edit
    /// overwritten by another process cannot be recovered. Long enough that
    /// ordinary overlapping saves never see it, since a save is a handful of
    /// small files.
    /// </remarks>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(15);

    /// <summary>
    /// Set once this machine has proved it will not give us a lock file at all,
    /// so no later save waits out the timeout again.
    /// </summary>
    /// <remarks>
    /// A read-only or sandboxed temporary folder, or a lock file left by another
    /// user in a shared one, fails the same way on every attempt: waiting out
    /// the timeout for permission that is never coming, on every save, and once
    /// per task in a cascade. The workspace is unprotected either way, so this
    /// is the one case that goes ahead unlocked rather than refusing every
    /// change the user makes. Contention is not remembered this way — that one
    /// is transient, and the next save is entitled to its own wait.
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
                // Not contention: permission does not arrive by waiting, and a
                // machine that cannot lock at all is not a machine where another
                // process is about to. Every later save skips the wait.
                _unavailable = true;
                return new WorkspaceLock(null);
            }
            catch (IOException)
            {
                // Somebody else is mid-save. Contention is transient, so this
                // one is waited out rather than remembered — the next save gets
                // the same wait.
                if (DateTime.UtcNow >= deadline) throw new WorkspaceBusyException(root);
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
