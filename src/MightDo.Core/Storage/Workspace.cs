namespace MightDo.Core.Storage;

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

    public string TaskFile(string taskId) => Path.Combine(TasksDir, $"{taskId}.json");

    public string TrashedTaskFile(string taskId) =>
        Path.Combine(TrashTasksDir, $"{taskId}.json");

    public string AttachmentFile(string storedName) =>
        Path.Combine(AttachmentsDir, storedName);

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
