using MightDo.Core.Domain;

namespace MightDo.Core.Interchange;

/// <summary>How an import should treat things the file mentions and the workspace has not got.</summary>
/// <param name="CreateCategoriesAndTags">
/// On by default. Categories and tags are names with no semantics the app
/// reasons about, so creating them is safe in a way creating a status is not —
/// see ADR-0005. Off, a row naming an unknown one simply leaves that field
/// unset rather than failing.
/// </param>
public sealed record ImportOptions(bool CreateCategoriesAndTags = true)
{
    public static ImportOptions Default { get; } = new();
}

/// <summary>What an import would do to one task.</summary>
public enum ImportRowKind
{
    /// <summary>The file has no id for this row, or an id the workspace has never seen.</summary>
    Create,

    /// <summary>The id names a task, and at least one field the file speaks about differs.</summary>
    Update,

    /// <summary>The id names a task, and every field the file speaks about already matches.</summary>
    Unchanged,
}

/// <summary>One row, resolved against the workspace.</summary>
/// <param name="Task">
/// The task as it would be written. For a create the board rank is a
/// placeholder; the session puts it at the bottom of its column.
/// </param>
public sealed record ImportChange(int Line, ImportRowKind Kind, MightDoTask Task);

/// <summary>
/// Everything an import would do, worked out before anything is written.
/// </summary>
/// <remarks>
/// Import is the half of this feature that can destroy data, so it is
/// parse, plan, show, then apply: this is the "plan", and it exists as a value
/// the preview can render and the session can apply without doing the work
/// twice.
/// <para>
/// A task in the workspace with no row in the file is left alone. Import is
/// never a deletion — the file is a set of changes, not a mirror — because a
/// mis-exported filter would otherwise be able to empty a workspace, and the app
/// has no undo.
/// </para>
/// </remarks>
public sealed record ImportPlan
{
    public required IReadOnlyList<ImportChange> Changes { get; init; }

    public required IReadOnlyList<CsvRowError> Errors { get; init; }

    /// <summary>Categories the file mentions that the workspace has not got, ready to be added.</summary>
    public IReadOnlyList<Category> NewCategories { get; init; } = [];

    public IReadOnlyList<Tag> NewTags { get; init; } = [];

    /// <summary>
    /// Notes that exist on a task now and would not afterwards.
    /// </summary>
    /// <remarks>
    /// Counted separately, and shown prominently, because it is the one
    /// irreversible thing an import does that a user could easily not have
    /// meant — a spreadsheet that truncated a multi-line cell shows up here.
    /// </remarks>
    public int NotesRemoved { get; init; }

    /// <inheritdoc cref="NotesRemoved"/>
    public int StepsRemoved { get; init; }

    public int CreateCount => Changes.Count(change => change.Kind is ImportRowKind.Create);

    public int UpdateCount => Changes.Count(change => change.Kind is ImportRowKind.Update);

    public int UnchangedCount => Changes.Count(change => change.Kind is ImportRowKind.Unchanged);

    /// <summary>Whether applying this would write anything at all.</summary>
    public bool WritesAnything =>
        CreateCount > 0 || UpdateCount > 0 || NewCategories.Count > 0 || NewTags.Count > 0;

    /// <summary>
    /// Works out what <paramref name="read"/> would do to the workspace.
    /// </summary>
    /// <remarks>
    /// Pure: no session, no I/O, and no clock beyond <paramref name="time"/>.
    /// The trashed ids are passed in rather than looked up because the trash
    /// lives on disk and this layer does not read disk.
    /// </remarks>
    /// <param name="trashedIds">
    /// Ids in <c>.trash/</c>. A row naming one is refused: quietly recreating a
    /// task beside its own trashed copy is how a user ends up with two.
    /// </param>
    public static ImportPlan Build(
        CsvReadResult read,
        IReadOnlyList<MightDoTask> tasks,
        WorkspaceConfig config,
        IReadOnlySet<string>? trashedIds = null,
        ImportOptions? options = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(config);

        options ??= ImportOptions.Default;
        var now = Instants.Now(time ?? TimeProvider.System);
        var trashed = trashedIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var byId = tasks.ToDictionary(task => task.Id, StringComparer.OrdinalIgnoreCase);
        var errors = new List<CsvRowError>(read.Errors);
        var changes = new List<ImportChange>();
        var names = new NameResolver(config, options.CreateCategoriesAndTags);
        var notesRemoved = 0;
        var stepsRemoved = 0;

        foreach (var row in read.Rows)
        {
            if (row.Id is { } id && trashed.Contains(id))
            {
                errors.Add(new CsvRowError(
                    row.Line, "id", "That task is in the trash. Restore it before importing."));
                continue;
            }

            var existing = row.Id is null ? null : byId.GetValueOrDefault(row.Id);
            var built = existing is null
                ? Created(row, read.PresentColumns, config, names, now, errors)
                : Updated(row, existing, read.PresentColumns, config, names, now, errors, time);

            if (built is null) continue;

            if (existing is null)
            {
                changes.Add(new ImportChange(row.Line, ImportRowKind.Create, built));
                continue;
            }

            notesRemoved += Missing(existing.Notes.Select(note => note.Id), built.Notes.Select(note => note.Id));
            stepsRemoved += Missing(existing.Steps.Select(step => step.Id), built.Steps.Select(step => step.Id));

            changes.Add(new ImportChange(
                row.Line,
                built.HasSameContentAs(existing) ? ImportRowKind.Unchanged : ImportRowKind.Update,
                built));
        }

        return new ImportPlan
        {
            Changes = changes,
            Errors = [.. errors.OrderBy(error => error.Line)],
            NewCategories = names.NewCategories,
            NewTags = names.NewTags,
            NotesRemoved = notesRemoved,
            StepsRemoved = stepsRemoved,
        };
    }

    private static int Missing(IEnumerable<string> before, IEnumerable<string> after)
    {
        var kept = after.ToHashSet(StringComparer.Ordinal);
        return before.Count(id => !kept.Contains(id));
    }

    private static MightDoTask? Created(
        CsvRow row,
        CsvColumns present,
        WorkspaceConfig config,
        NameResolver names,
        DateTime now,
        List<CsvRowError> errors)
    {
        if (row.Summary.Length == 0)
        {
            errors.Add(new CsvRowError(row.Line, "summary", "A new task needs a summary."));
            return null;
        }

        if (row.StatusId.Length == 0)
        {
            errors.Add(new CsvRowError(row.Line, "status", "A new task needs a status."));
            return null;
        }

        var created = row.CreatedAt ?? now;

        var task = new MightDoTask
        {
            Id = row.Id ?? Ulid.New(),
            Summary = row.Summary,
            Description = row.Description,
            StatusId = row.StatusId,
            CategoryId = present.HasFlag(CsvColumns.Category) ? names.Category(row.CategoryName) : null,
            Priority = row.Priority,
            DueDate = row.DueDate,
            EstimateMinutes = row.EstimateMinutes,
            TotalTimeMinutes = row.TotalTimeMinutes,
            Steps = [.. row.Steps.Select(step => step with { Id = Ulid.New() })],
            Notes = [.. row.Notes.Select(note => Dated(note, now) with { Id = Ulid.New() })],
            Reminders = [.. row.Reminders.Select(Reminder.Create)],
            CreatedAt = created,
            UpdatedAt = created,
        }.WithTags(names.Tags(row.TagNames));

        // The one place a completion date is taken from outside the app, and
        // only where it is consistent with the status the task is landing in.
        return config.IsFinal(row.StatusId) && row.CompletedAt is { } completed
            ? task.WithImportedCompletion(completed)
            : task;
    }

    private static MightDoTask? Updated(
        CsvRow row,
        MightDoTask existing,
        CsvColumns present,
        WorkspaceConfig config,
        NameResolver names,
        DateTime now,
        List<CsvRowError> errors,
        TimeProvider? time)
    {
        if (present.HasFlag(CsvColumns.Summary) && row.Summary.Length == 0)
        {
            errors.Add(new CsvRowError(row.Line, "summary", "A task needs a summary."));
            return null;
        }

        var task = existing;

        // Moving status goes through the domain rule, so an update into or out
        // of a Final status gets the same completion date it would from the UI.
        // completedAt in the file is informational on an update: the status owns
        // it, and honouring both would let the two disagree.
        if (present.HasFlag(CsvColumns.Status) && row.StatusId != existing.StatusId)
        {
            task = task.WithStatus(row.StatusId, config, time: time);
        }

        if (present.HasFlag(CsvColumns.Summary)) task = task with { Summary = row.Summary };
        if (present.HasFlag(CsvColumns.Description)) task = task with { Description = row.Description };
        if (present.HasFlag(CsvColumns.Priority)) task = task with { Priority = row.Priority };
        if (present.HasFlag(CsvColumns.DueDate)) task = task with { DueDate = row.DueDate };
        if (present.HasFlag(CsvColumns.EstimateMinutes)) task = task with { EstimateMinutes = row.EstimateMinutes };
        if (present.HasFlag(CsvColumns.TotalTimeMinutes)) task = task with { TotalTimeMinutes = row.TotalTimeMinutes };
        if (present.HasFlag(CsvColumns.Category)) task = task with { CategoryId = names.Category(row.CategoryName) };
        if (present.HasFlag(CsvColumns.Tags)) task = task.WithTags(names.Tags(row.TagNames));
        if (present.HasFlag(CsvColumns.Steps)) task = task with { Steps = MergeSteps(existing.Steps, row.Steps) };
        if (present.HasFlag(CsvColumns.Notes)) task = task with { Notes = MergeNotes(existing.Notes, row.Notes, now) };
        if (present.HasFlag(CsvColumns.Reminders)) task = task with { Reminders = MergeReminders(existing.Reminders, row.Reminders) };

        return task;
    }

    /// <summary>
    /// Keeps a step's id where its text has not moved.
    /// </summary>
    /// <remarks>
    /// Exporting and re-importing an untouched task therefore produces no write
    /// at all, and moving one line in a spreadsheet renumbers only what moved.
    /// </remarks>
    private static IReadOnlyList<Step> MergeSteps(
        IReadOnlyList<Step> existing, IReadOnlyList<Step> incoming) =>
    [
        .. incoming.Select((step, index) =>
            index < existing.Count && existing[index].Text == step.Text
                ? existing[index] with { Done = step.Done }
                : step with { Id = Ulid.New() }),
    ];

    /// <summary>
    /// Keeps a note's id where its timestamp <i>and</i> its body both match.
    /// </summary>
    /// <remarks>
    /// Notes are append-only in the app and are treated that way here. Deleting
    /// a line does delete the note — refusing would make the column a lie — but
    /// the plan counts those separately so the preview can say so.
    /// </remarks>
    private static IReadOnlyList<Note> MergeNotes(
        IReadOnlyList<Note> existing, IReadOnlyList<Note> incoming, DateTime now)
    {
        var spare = existing.ToList();
        var merged = new List<Note>(incoming.Count);

        foreach (var note in incoming.Select(note => Dated(note, now)))
        {
            var match = spare.FindIndex(
                other => other.CreatedAt == note.CreatedAt && other.Body == note.Body);

            if (match >= 0)
            {
                merged.Add(spare[match]);
                spare.RemoveAt(match);
            }
            else
            {
                merged.Add(note with { Id = Ulid.New() });
            }
        }

        return merged;
    }

    /// <summary>
    /// Replaces the pending reminders, leaving fired and dismissed ones alone.
    /// </summary>
    /// <remarks>
    /// The one place the CSV is a view of part of a field. A fired or dismissed
    /// reminder is bookkeeping about a notification that has already happened,
    /// and re-importing one would either resurrect a dismissed alert or drop the
    /// <c>firedAt</c> that stops it firing twice.
    /// </remarks>
    private static IReadOnlyList<Reminder> MergeReminders(
        IReadOnlyList<Reminder> existing, IReadOnlyList<DateTime> incoming)
    {
        var wanted = new List<DateTime>(incoming);
        var merged = new List<Reminder>(existing.Count + incoming.Count);

        // Walked in the order the task already holds them, so a reminder the
        // file still asks for keeps both its id and its place — otherwise
        // exporting and re-importing an untouched task would look like a change.
        foreach (var reminder in existing)
        {
            if (!reminder.IsPending)
            {
                merged.Add(reminder);
                continue;
            }

            var match = wanted.IndexOf(reminder.RemindAt);
            if (match < 0) continue;

            merged.Add(reminder);
            wanted.RemoveAt(match);
        }

        merged.AddRange(wanted.Select(Reminder.Create));
        return merged;
    }

    /// <summary>A note the file left undated is dated by the importing session's clock.</summary>
    private static Note Dated(Note note, DateTime now) =>
        note.CreatedAt == default ? note with { CreatedAt = now } : note;

    /// <summary>
    /// Turns the category and tag <i>names</i> a file carries into ids, minting
    /// the ones the workspace has not got.
    /// </summary>
    /// <remarks>
    /// Stateful across the whole file, so two rows mentioning the same new tag
    /// create it once. New ids are minted here rather than at apply time because
    /// the tasks in the plan have to be able to reference them.
    /// </remarks>
    private sealed class NameResolver(WorkspaceConfig config, bool create)
    {
        private readonly Dictionary<string, string> _categories =
            config.Categories.ToDictionary(c => c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, string> _tags =
            config.Tags.ToDictionary(t => t.Name, t => t.Id, StringComparer.OrdinalIgnoreCase);

        private readonly List<Category> _newCategories = [];
        private readonly List<Tag> _newTags = [];

        public IReadOnlyList<Category> NewCategories => _newCategories;

        public IReadOnlyList<Tag> NewTags => _newTags;

        public string? Category(string? name)
        {
            if (name is null) return null;
            if (_categories.TryGetValue(name, out var id)) return id;
            if (!create) return null;

            var added = new Domain.Category(
                Ulid.New(), name, Domain.Category.ColorAt(config.Categories.Count + _newCategories.Count));

            _newCategories.Add(added);
            _categories[name] = added.Id;
            return added.Id;
        }

        public IReadOnlyList<string> Tags(IReadOnlyList<string> names)
        {
            var ids = new List<string>(names.Count);
            foreach (var name in names)
            {
                if (_tags.TryGetValue(name, out var id))
                {
                    ids.Add(id);
                    continue;
                }

                if (!create) continue;

                var added = new Tag(Ulid.New(), name);
                _newTags.Add(added);
                _tags[name] = added.Id;
                ids.Add(added.Id);
            }

            return ids;
        }
    }
}
