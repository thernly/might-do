using MightDo.Core.Domain;

namespace MightDo.Core.Storage;

/// <summary>
/// A persisted name that does not identify a file inside the workspace.
/// </summary>
/// <remarks>
/// Task ids and attachment stored names come out of JSON the user (or a sync
/// client, or anything else with write access to the folder) can edit, and they
/// are turned straight back into paths. An absolute path or a <c>..</c> segment
/// would resolve outside <c>tasks/</c> or <c>attachments/</c> and let a crafted
/// workspace overwrite, move, or delete files elsewhere on the machine. Names
/// are therefore checked at the boundary rather than trusted.
/// </remarks>
public sealed class UnsafeWorkspaceNameException(string message) : Exception(message);

/// <summary>
/// A file written in a schema version this build does not understand.
/// </summary>
/// <remarks>
/// Reading a newer file is tolerant by design — unknown keys are skipped — but
/// tolerance on read is only safe while the older writer never writes the file
/// back. It would write schema 1 and every key it knows, deleting whatever the
/// newer version put there. A machine that has not upgraded yet therefore
/// refuses the data outright rather than quietly downgrading it.
/// </remarks>
public sealed class UnsupportedSchemaVersionException(string message) : Exception(message);

/// <summary>
/// <c>config.json</c> is there but cannot be understood.
/// </summary>
/// <remarks>
/// Distinct from a missing config, which is how a fresh workspace starts and is
/// answered by seeding one. A config that exists and will not parse must never
/// be seeded over: it defines the statuses every task refers to, and replacing
/// it with a default would orphan the whole workspace. So the workspace refuses
/// to open, saying which file and why, and leaves the folder exactly as it
/// found it.
/// </remarks>
public sealed class UnreadableConfigException(string message, Exception inner)
    : Exception(message, inner);

/// <summary>
/// The on-disk layout of a workspace folder.
/// </summary>
/// <remarks>
/// The user points this at a folder inside OneDrive (or Dropbox, or iCloud
/// Drive). Everything the app owns lives beneath it:
/// <code>
/// &lt;root&gt;/
///   config.json          statuses, categories, tags
///   tasks/               one JSON file per task, named by ULID
///   attachments/         copied files, prefixed by attachment id
///   .trash/              deleted tasks and their attachments
/// </code>
/// See <c>docs/format/workspace-v1.md</c>.
/// </remarks>
public sealed class Workspace(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public string ConfigFile => Path.Combine(Root, "config.json");
    public string TasksDir => Path.Combine(Root, "tasks");
    public string AttachmentsDir => Path.Combine(Root, "attachments");
    public string TrashDir => Path.Combine(Root, ".trash");
    public string TrashTasksDir => Path.Combine(TrashDir, "tasks");
    public string TrashAttachmentsDir => Path.Combine(TrashDir, "attachments");

    public string TaskFile(string taskId) =>
        Path.Combine(TasksDir, $"{RequireTaskId(taskId)}.json");

    public string TrashedTaskFile(string taskId) =>
        Path.Combine(TrashTasksDir, $"{RequireTaskId(taskId)}.json");

    public string AttachmentFile(string storedName) =>
        Path.Combine(AttachmentsDir, RequireStoredName(storedName));

    public string TrashedAttachmentFile(string storedName) =>
        Path.Combine(TrashAttachmentsDir, RequireStoredName(storedName));

    /// <summary>A task id, or a refusal — never a path.</summary>
    public static string RequireTaskId(string taskId) =>
        Ulid.IsUlid(taskId)
            ? taskId
            : throw new UnsafeWorkspaceNameException(
                $"Task id is not a ULID: '{taskId}'.");

    /// <summary>
    /// An attachment's stored name, which is always
    /// <c>&lt;attachment-ulid&gt;-&lt;original file name&gt;</c> and always a
    /// plain name inside the attachments folder.
    /// </summary>
    public static string RequireStoredName(string storedName)
    {
        var separator = Ulid.Length;
        if (storedName.Length > separator + 1
            && storedName[separator] == '-'
            && Ulid.IsUlid(storedName.AsSpan(0, Ulid.Length))
            && IsPlainName(storedName[(separator + 1)..]))
        {
            return storedName;
        }

        throw new UnsafeWorkspaceNameException(
            $"Attachment stored name is not '<ulid>-<file name>': '{storedName}'.");
    }

    /// <summary>
    /// Whether a name stays put: no separator on any platform, no drive or
    /// stream qualifier, and not a walk up the tree.
    /// </summary>
    /// <remarks>
    /// Both separators are rejected everywhere, not just the local one. A
    /// workspace written on Linux is read on Windows, where a name containing a
    /// backslash stops being one name.
    /// </remarks>
    private static bool IsPlainName(string name) =>
        name is not ("." or "..")
        && name.AsSpan().IndexOfAny('/', '\\') < 0
        && !name.Contains(':')
        && !Path.IsPathRooted(name);

    public void EnsureLayout()
    {
        foreach (var dir in (string[])
                 [Root, TasksDir, AttachmentsDir, TrashTasksDir, TrashAttachmentsDir])
        {
            Directory.CreateDirectory(dir);
        }
    }

    /// <summary>True when the folder already holds a might-do workspace.</summary>
    public bool IsInitialised => File.Exists(ConfigFile);

    /// <summary>
    /// Whether the workspace folder is still there at all.
    /// </summary>
    /// <remarks>
    /// Deleting a watched root produces no filesystem events (measured — see
    /// ADR-0003), so "the workspace has gone" has to be asked rather than waited
    /// for. Without this the app sits showing stale tasks for an unmounted drive
    /// or a moved OneDrive folder.
    /// </remarks>
    public bool Exists => Directory.Exists(Root);
}
