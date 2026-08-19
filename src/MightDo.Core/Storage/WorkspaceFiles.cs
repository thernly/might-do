using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MightDo.Core.Domain;
using MightDo.Core.Serialization;

namespace MightDo.Core.Storage;

/// <summary>
/// A file beside one of ours that the app did not write.
/// </summary>
/// <remarks>
/// Because task filenames are strictly ULIDs, anything else in that folder came
/// from somewhere else — and in practice that means a sync client dropped a
/// conflict copy there: OneDrive's <c>01j….-LAPTOP.json</c>, Dropbox's
/// <c>01j… (conflicted copy 2026-08-16).json</c>, iCloud's <c>01j… 2.json</c>.
/// <para>
/// These are surfaced in the app rather than ignored. Silently skipping them is
/// how you discover months later that an edit was lost.
/// </para>
/// </remarks>
/// <param name="TaskId">The task this appears to be a copy of, when recoverable.</param>
public sealed record ConflictFile(string FullPath, string? TaskId, DateTime ModifiedAt)
{
    public string FileName => Path.GetFileName(FullPath);
}

public static partial class WorkspaceFiles
{
    /// <summary>
    /// The contents of a file as we last saw them, or <see cref="NoFile"/>.
    /// </summary>
    /// <remarks>
    /// A short hash rather than the text itself: it is compared before every
    /// write, and holding a copy of every task file in memory to do that would
    /// double the workspace's footprint for no extra certainty.
    /// </remarks>
    public static string VersionOf(string contents) =>
        string.IsNullOrWhiteSpace(contents)
            ? NoFile
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contents)));

    /// <summary>
    /// The version of a file that is not there — or is there but still empty,
    /// which <see cref="ReadJsonAsync{T}"/> already reads as absent.
    /// </summary>
    public const string NoFile = "";

    /// <summary>The version of what is on disk right now.</summary>
    public static async Task<string> VersionOnDiskAsync(
        string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return NoFile;
        return VersionOf(await File.ReadAllTextAsync(path, cancellationToken));
    }

    /// <summary>
    /// Writes JSON that a sync client can never catch half-finished, returning
    /// the version of what was written.
    /// </summary>
    /// <remarks>
    /// The write goes to a temporary file and is then renamed over the target,
    /// which is atomic on every platform we ship to. Without this, OneDrive will
    /// eventually upload a partially written task and you get a corrupt file
    /// instead of a conflict.
    /// <para>
    /// The temporary name is unique per write. A shared one — <c>&lt;target&gt;.tmp</c>
    /// — is fine until two writers overlap, and then each is writing the file
    /// the other is about to rename: the losing save fails, or the winning name
    /// carries the wrong bytes. Uniqueness costs nothing and makes the overlap
    /// unrepresentable.
    /// </para>
    /// <para>
    /// The folder is expected to exist. Creating it here is how a save into a
    /// workspace that has been unmounted or moved rebuilds a fragment of it at
    /// the old path — see <see cref="WorkspaceUnavailableException"/>.
    /// </para>
    /// </remarks>
    public static async Task<string> WriteJsonAtomicAsync<T>(
        string path, T value, CancellationToken cancellationToken = default)
    {
        var contents = WorkspaceJson.Serialize(value) + "\n";

        var temp = TempSibling(path);
        try
        {
            await File.WriteAllTextAsync(
                temp, contents, new UTF8Encoding(false), cancellationToken);

            try
            {
                File.Move(temp, path, overwrite: true);
            }
            catch (IOException)
            {
                // Some Windows configurations refuse a rename onto an existing
                // file. The one being replaced is moved aside rather than
                // deleted, so a failure here still leaves a complete file at the
                // target: deleting first means a transient share, quota or
                // network error takes the last good copy with it.
                ReplaceThroughSideStep(temp, path);
            }
        }
        catch
        {
            // A write that failed partway — a full disk, a volume pulled — has
            // left a half-file under a name nothing will ever look at again, in
            // a folder a sync client is watching. Nothing else collects those.
            Discard(temp);
            throw;
        }

        return VersionOf(contents);
    }

    private static void ReplaceThroughSideStep(string temp, string path)
    {
        if (!File.Exists(path))
        {
            File.Move(temp, path);
            return;
        }

        var displaced = TempSibling(path);
        File.Move(path, displaced);
        try
        {
            File.Move(temp, path);
        }
        catch
        {
            PutBack(displaced, path);
            throw;
        }

        // The new contents are live: whatever happens to the copy we set aside,
        // this write succeeded. Failing here instead would report a save that
        // landed as a failure, and the next save would find a file it did not
        // remember writing and preserve the user's own bytes as a conflict.
        Discard(displaced);
    }

    /// <summary>
    /// Puts back the file that was moved aside, or failing that gives it a name
    /// the user will be told about.
    /// </summary>
    /// <remarks>
    /// A temporary name is skipped by every scan on purpose, so leaving the last
    /// good copy under one is leaving it where nobody will ever find it. The
    /// conflict name is the one shape this app already reports.
    /// </remarks>
    private static void PutBack(string displaced, string path)
    {
        try
        {
            File.Move(displaced, path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Move(displaced, ConflictNameFor(path, DateTime.UtcNow));
            }
            catch (Exception renaming) when (renaming is IOException
                                                 or UnauthorizedAccessException)
            {
                // Nothing else is available, and the failure on the way out is
                // the one the user needs to hear about.
            }
        }
    }

    /// <summary>Removes a temporary file, if it is still there to remove.</summary>
    private static void Discard(string temp)
    {
        try
        {
            File.Delete(temp);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The failure that brought us here is the one worth reporting.
        }
    }

    /// <summary>
    /// A name beside <paramref name="path"/> that no other writer will pick,
    /// and that everything scanning the folder already knows to skip.
    /// </summary>
    private static string TempSibling(string path) =>
        $"{path}.{Guid.NewGuid():N}{TempSuffix}";

    /// <summary>What a file is called while it is being written or replaced.</summary>
    public const string TempSuffix = ".tmp";

    /// <summary>
    /// Renames a file we are about to overwrite out of the way, as the sync
    /// clients themselves do, and returns where it went.
    /// </summary>
    /// <remarks>
    /// Used when the file on disk is not the one we loaded: somebody else — a
    /// sync client, a second copy of the app, a text editor — wrote it after we
    /// read it. Overwriting is still what the user asked for, but their edit is
    /// kept beside it under a name <see cref="FindConflictFiles"/> reports,
    /// rather than being replaced by a save they never connected to it.
    /// </remarks>
    public static string PreserveAsConflict(string path, DateTime detectedAt)
    {
        var destination = ConflictNameFor(path, detectedAt);
        File.Move(path, destination);
        return destination;
    }

    /// <summary>
    /// A free name beside <paramref name="path"/>, in the shape the sync clients
    /// use and <see cref="FindConflictFiles"/> reports.
    /// </summary>
    private static string ConflictNameFor(string path, DateTime detectedAt)
    {
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var stamp = detectedAt.ToString("yyyy-MM-dd HHmmss");

        var destination = Path.Combine(dir, $"{stem} (conflicted copy {stamp}){extension}");
        for (var attempt = 2; File.Exists(destination); attempt++)
        {
            destination = Path.Combine(
                dir, $"{stem} (conflicted copy {stamp} {attempt}){extension}");
        }

        return destination;
    }

    /// <summary>
    /// Reads and deserializes a file, or null if it is absent or empty.
    /// </summary>
    /// <remarks>
    /// An empty file reads as absent rather than as an error: a sync client that
    /// has created the file but not yet filled it is a transient state, not
    /// corruption.
    /// </remarks>
    public static async Task<T?> ReadJsonAsync<T>(
        string path, CancellationToken cancellationToken = default)
        where T : class =>
        (await ReadJsonVersionedAsync<T>(path, cancellationToken)).Value;

    /// <summary>
    /// Reads a file along with the version of the exact contents it was parsed
    /// from, so a later write can tell whether that is still what is on disk.
    /// </summary>
    public static async Task<(T? Value, string Version)> ReadJsonVersionedAsync<T>(
        string path, CancellationToken cancellationToken = default)
        where T : class
    {
        if (!File.Exists(path)) return (null, NoFile);

        PersistedShape.RequireReadableSize(path);

        var contents = await File.ReadAllTextAsync(path, cancellationToken);
        return string.IsNullOrWhiteSpace(contents)
            ? (null, NoFile)
            : (WorkspaceJson.Deserialize<T>(contents), VersionOf(contents));
    }

    /// <summary>
    /// Whether <paramref name="fileName"/> is a task file this app wrote: a
    /// 26-character Crockford base32 ULID plus <c>.json</c>.
    /// </summary>
    /// <remarks>
    /// Matched case-insensitively — we write lowercase, but a sync client or a
    /// case-insensitive filesystem may hand the name back in another case, and a
    /// task must never be mistaken for a foreign file over casing.
    /// </remarks>
    public static bool IsOwnTaskFile(string fileName) =>
        Path.GetExtension(fileName).Equals(".json", StringComparison.OrdinalIgnoreCase)
        && Ulid.IsUlid(Path.GetFileNameWithoutExtension(fileName.AsSpan()));

    /// <summary>
    /// Scans <c>tasks/</c> and the workspace root for files the app didn't write.
    /// </summary>
    public static IReadOnlyList<ConflictFile> FindConflictFiles(Workspace workspace)
    {
        var conflicts = new List<ConflictFile>();

        foreach (var path in Files(workspace.TasksDir))
        {
            var name = Path.GetFileName(path);
            if (IsOwnTaskFile(name) || IsInFlightWrite(name)) continue;

            var match = EmbeddedUlid().Match(name);
            conflicts.Add(new ConflictFile(
                path,
                match.Success ? match.Groups[1].Value : null,
                File.GetLastWriteTimeUtc(path)));
        }

        // The config is one file rather than one per task, so a sync client has
        // only ever the one place to drop its copy — and a lost status or
        // category is as much a lost edit as a task is.
        foreach (var path in Files(workspace.Root))
        {
            var name = Path.GetFileName(path);
            if (!name.StartsWith("config", StringComparison.OrdinalIgnoreCase)) continue;
            if (path == workspace.ConfigFile || IsInFlightWrite(name)) continue;

            conflicts.Add(new ConflictFile(path, null, File.GetLastWriteTimeUtc(path)));
        }

        // Newest first: the most recent conflict is the one the user is most
        // likely to be looking for.
        return [.. conflicts.OrderByDescending(c => c.ModifiedAt)];
    }

    private static IEnumerable<string> Files(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir) : [];

    /// <summary>Our own temporary file, mid-rename.</summary>
    private static bool IsInFlightWrite(string fileName) =>
        fileName.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(
        "([0-9A-HJKMNP-TV-Z]{26})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedUlid();
}
