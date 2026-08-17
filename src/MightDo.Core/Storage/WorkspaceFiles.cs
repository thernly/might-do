using System.Text;
using System.Text.RegularExpressions;
using MightDo.Core.Domain;
using MightDo.Core.Serialization;

namespace MightDo.Core.Storage;

/// <summary>
/// A file in <c>tasks/</c> that the app did not write.
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
    /// Writes JSON that a sync client can never catch half-finished.
    /// </summary>
    /// <remarks>
    /// The write goes to a temporary file and is then renamed over the target,
    /// which is atomic on every platform we ship to. Without this, OneDrive will
    /// eventually upload a partially written task and you get a corrupt file
    /// instead of a conflict.
    /// </remarks>
    public static async Task WriteJsonAtomicAsync<T>(
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
        where T : class
    {
        if (!File.Exists(path)) return null;

        var contents = await File.ReadAllTextAsync(path, cancellationToken);
        return string.IsNullOrWhiteSpace(contents)
            ? null
            : WorkspaceJson.Deserialize<T>(contents);
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

    /// <summary>Scans <c>tasks/</c> for files the app didn't write.</summary>
    public static IReadOnlyList<ConflictFile> FindConflictFiles(Workspace workspace)
    {
        if (!Directory.Exists(workspace.TasksDir)) return [];

        var conflicts = new List<ConflictFile>();
        foreach (var path in Directory.EnumerateFiles(workspace.TasksDir))
        {
            var name = Path.GetFileName(path);
            if (IsOwnTaskFile(name)) continue;
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue; // our own in-flight write

            var match = EmbeddedUlid().Match(name);
            conflicts.Add(new ConflictFile(
                path,
                match.Success ? match.Groups[1].Value : null,
                File.GetLastWriteTimeUtc(path)));
        }

        // Newest first: the most recent conflict is the one the user is most
        // likely to be looking for.
        return [.. conflicts.OrderByDescending(c => c.ModifiedAt)];
    }

    [GeneratedRegex(
        "([0-9A-HJKMNP-TV-Z]{26})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedUlid();
}
