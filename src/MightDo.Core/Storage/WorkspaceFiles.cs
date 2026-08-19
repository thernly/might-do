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
    /// </remarks>
    public static async Task<string> WriteJsonAtomicAsync<T>(
        string path, T value, CancellationToken cancellationToken = default)
    {
        var contents = WorkspaceJson.Serialize(value) + "\n";

        var parent = Path.GetDirectoryName(path);
        if (parent is not null) Directory.CreateDirectory(parent);

        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, contents, new UTF8Encoding(false), cancellationToken);

        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException)
        {
            // Some Windows configurations refuse a rename onto an existing file.
            // Falling back leaves a very small window where the target is
            // absent, which is still far better than a partially written file.
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        return VersionOf(contents);
    }

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

        File.Move(path, destination);
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
        fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(
        "([0-9A-HJKMNP-TV-Z]{26})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedUlid();
}
