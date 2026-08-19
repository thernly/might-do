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
/// The workspace folder is not there.
/// </summary>
/// <remarks>
/// A workspace can go away under a running app: a drive is unmounted, a synced
/// folder is moved on another machine, a user deletes it in Finder. Every write
/// therefore asks first, because the alternative is worse than an error — the
/// atomic write and the trash both create what they need, so an ordinary edit
/// or a reminder coming due would build a partial workspace at the old path,
/// which then collides with the real folder when it comes back.
/// </remarks>
public sealed class WorkspaceUnavailableException(string message) : Exception(message);

/// <summary>
/// One of the folders the app owns is a link to somewhere else.
/// </summary>
/// <remarks>
/// <see cref="UnsafeWorkspaceNameException"/> keeps persisted names from
/// naming a file outside the workspace, but a name that stays put is only half
/// the boundary: if <c>attachments/</c> or <c>.trash/</c> is itself a symlink
/// or a junction, the escape happens while the filesystem resolves the path,
/// after the name has passed every check. So the directories the app writes
/// through must be real directories. The root is exempt — whatever it resolves
/// to is the folder the user chose, and that is the boundary rather than a
/// breach of it.
/// </remarks>
public sealed class LinkedWorkspaceDirectoryException(string message) : Exception(message);

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

    /// <summary>The folders the app writes through, all of which it owns.</summary>
    private string[] OwnedDirectories =>
        [TasksDir, AttachmentsDir, TrashDir, TrashTasksDir, TrashAttachmentsDir];

    /// <summary>
    /// Creates the folders the app owns. The root is not one of them.
    /// </summary>
    /// <remarks>
    /// A workspace folder is chosen by the user, never invented by the app: if
    /// it is not there we are looking at a moved, unmounted or deleted
    /// workspace, and creating it is how a shadow workspace appears at the old
    /// path. The folders inside it are ours to replace, so a user who deletes
    /// <c>tasks/</c> gets it back rather than an error.
    /// </remarks>
    public void EnsureLayout()
    {
        RequireWritable();
        foreach (var dir in OwnedDirectories) Directory.CreateDirectory(dir);
    }

    /// <summary>
    /// Checks the workspace is somewhere we may write: still there, and not
    /// wired to somewhere else.
    /// </summary>
    /// <remarks>
    /// Asked again before every mutation rather than once at open, because both
    /// conditions change under a running app — a drive unmounts, and a link can
    /// be swapped in by whatever else has write access to the folder. It costs
    /// a handful of stats against a save that is about to write a file.
    /// </remarks>
    public void RequireWritable()
    {
        if (!Exists)
        {
            throw new WorkspaceUnavailableException(
                $"The workspace folder {Root} is no longer there, so nothing can be "
                + "written to it. Nothing has been created in its place. If it is on a "
                + "drive or in a synced folder, it may come back.");
        }

        foreach (var dir in OwnedDirectories)
        {
            // Asked of the entry rather than of what it points at, so a link
            // whose target is currently missing is refused too rather than
            // being created over.
            if (new DirectoryInfo(dir).LinkTarget is { } target)
            {
                throw new LinkedWorkspaceDirectoryException(
                    $"'{Path.GetFileName(dir)}' in {Root} is a link to '{target}' rather "
                    + "than a folder. MightDo will not write through it: everything it "
                    + "owns has to stay inside the workspace. Replace the link with a "
                    + "real folder to open this workspace.");
            }
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
