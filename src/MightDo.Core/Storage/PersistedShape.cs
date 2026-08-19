using MightDo.Core.Domain;

namespace MightDo.Core.Storage;

/// <summary>
/// Checks that a file which parsed also deserialised into an object the rest of
/// the app can actually use.
/// </summary>
/// <remarks>
/// Parsing and deserialising are not the same guarantee. System.Text.Json
/// enforces that a <c>required</c> key is present; it says nothing about its
/// value, so <c>"summary": null</c>, <c>"steps": [null]</c> and
/// <c>"reminders": null</c> all produce a well-typed object with holes in it
/// that no C# signature in this codebase admits to. The holes surface far from
/// here — <see cref="MightDoTask.StepsDone"/> counting steps, search reading
/// every note — and by then a single hand-edited or sync-merged file has taken
/// out the projection that builds the whole workspace, rather than showing up
/// as one unreadable task beside the others.
/// <para>
/// So the storage boundary is where a file stops being untrusted input, and
/// that means the whole object graph, not just the names that get turned into
/// paths. Everything checked here is checked inside the caller's own
/// <c>try</c>, which is what turns a malformed task into a
/// <see cref="TaskLoadFailure"/> and a malformed config into an
/// <see cref="UnreadableConfigException"/>.
/// </para>
/// </remarks>
internal static class PersistedShape
{
    /// <summary>
    /// The largest workspace file we will read into memory.
    /// </summary>
    /// <remarks>
    /// A task is prose and a config is a few dozen names; neither reaches a
    /// megabyte by any honest route. Checking the length before reading means a
    /// file that is enormous by accident — a truncated download, a sync client's
    /// mistake — or on purpose fails as one unreadable file instead of taking
    /// the process down with it.
    /// </remarks>
    public const long MaxFileBytes = 16L * 1024 * 1024;

    /// <summary>Checks that a task deserialised whole.</summary>
    public static MightDoTask RequireWellFormed(MightDoTask task)
    {
        RequireText(task.Id, "id");
        RequireText(task.StatusId, "statusId");
        RequireText(task.BoardRank, "boardRank");
        RequirePresent(task.Summary, "summary");
        RequirePresent(task.Description, "description");
        RequireEnum(task.Priority, "priority");
        RequireNotNegative(task.EstimateMinutes, "estimateMinutes");
        RequireNotNegative(task.TotalTimeMinutes, "totalTimeMinutes");

        foreach (var tagId in RequireElements(task.TagIds, "tagIds"))
        {
            RequireText(tagId, "tagIds entry");
        }

        foreach (var step in RequireElements(task.Steps, "steps"))
        {
            RequireText(step.Id, "step id");
            RequirePresent(step.Text, "step text");
        }

        foreach (var note in RequireElements(task.Notes, "notes"))
        {
            RequireText(note.Id, "note id");
            RequirePresent(note.Body, "note body");
        }

        foreach (var attachment in RequireElements(task.Attachments, "attachments"))
        {
            RequireText(attachment.Id, "attachment id");
            RequirePresent(attachment.OriginalName, "attachment originalName");
            RequireText(attachment.StoredName, "attachment storedName");
            RequireNotNegative(attachment.SizeBytes, "attachment sizeBytes");
        }

        foreach (var reminder in RequireElements(task.Reminders, "reminders"))
        {
            RequireText(reminder.Id, "reminder id");
        }

        return task;
    }

    /// <summary>Checks that a config deserialised whole.</summary>
    /// <remarks>
    /// Thrown as <see cref="InvalidOperationException"/> for the same reason
    /// <c>RequireUsableConfig</c> is: the caller turns it into the
    /// <see cref="UnreadableConfigException"/> that names the file and says how
    /// to recover.
    /// </remarks>
    public static WorkspaceConfig RequireWellFormed(WorkspaceConfig config)
    {
        RequireText(config.DefaultStatusId, "defaultStatusId");

        foreach (var status in RequireElements(config.Statuses, "statuses"))
        {
            RequireText(status.Id, "status id");
            RequirePresent(status.Name, "status name");
            RequireEnum(status.Type, "status type");
        }

        foreach (var category in RequireElements(config.Categories, "categories"))
        {
            RequireText(category.Id, "category id");
            RequirePresent(category.Name, "category name");
        }

        foreach (var tag in RequireElements(config.Tags, "tags"))
        {
            RequireText(tag.Id, "tag id");
            RequirePresent(tag.Name, "tag name");
        }

        RequireDistinct(config.Statuses.Select(s => s.Id), "statuses");
        RequireDistinct(config.Categories.Select(c => c.Id), "categories");
        RequireDistinct(config.Tags.Select(t => t.Id), "tags");

        return config;
    }

    /// <summary>Refuses a file too big to be one of ours before it is read.</summary>
    public static void RequireReadableSize(string path)
    {
        var length = new FileInfo(path).Length;
        if (length <= MaxFileBytes) return;

        throw new InvalidOperationException(
            $"it is {length / (1024 * 1024)} MB, and no workspace file is that big.");
    }

    /// <summary>A value the format requires, which may legitimately be empty.</summary>
    private static void RequirePresent(string value, string name)
    {
        if (value is null) throw Malformed(name, "is null");
    }

    /// <summary>A value the format requires to say something.</summary>
    private static void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw Malformed(name, "is missing or blank");
    }

    private static void RequireEnum<T>(T value, string name) where T : struct, Enum
    {
        if (!Enum.IsDefined(value)) throw Malformed(name, $"is not a known value ('{value}')");
    }

    private static void RequireNotNegative(long? value, string name)
    {
        if (value < 0) throw Malformed(name, $"is negative ({value})");
    }

    private static IReadOnlyList<T> RequireElements<T>(IReadOnlyList<T> values, string name)
    {
        if (values is null) throw Malformed(name, "is null");
        if (values.Any(value => value is null)) throw Malformed(name, "contains a null entry");

        return values;
    }

    private static void RequireDistinct(IEnumerable<string> ids, string name)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicate = ids.FirstOrDefault(id => !seen.Add(id));
        if (duplicate is not null) throw Malformed(name, $"names '{duplicate}' twice");
    }

    private static InvalidOperationException Malformed(string name, string problem) =>
        new($"its {name} {problem}.");
}
