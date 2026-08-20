using System.Globalization;
using System.Text;
using MightDo.Core.Domain;

namespace MightDo.Core.Interchange;

/// <summary>
/// The columns of <c>csv-v1</c>, as a set, so "the file said nothing about this
/// field" can be told from "the file said this field is empty".
/// </summary>
/// <remarks>
/// The distinction is what makes bulk editing safe: deleting the <c>notes</c>
/// column from a spreadsheet before importing must not delete everybody's
/// notes, while clearing a cell in it must.
/// <para>
/// <c>statusType</c>, <c>attachments</c> and <c>updatedAt</c> have no flag. They
/// are written for information and ignored on read, so nothing downstream ever
/// asks whether they were present.
/// </para>
/// </remarks>
[Flags]
public enum CsvColumns
{
    None = 0,
    Id = 1 << 0,
    Summary = 1 << 1,
    Description = 1 << 2,
    Status = 1 << 3,
    Category = 1 << 4,
    Tags = 1 << 5,
    Priority = 1 << 6,
    DueDate = 1 << 7,
    CompletedAt = 1 << 8,
    EstimateMinutes = 1 << 9,
    TotalTimeMinutes = 1 << 10,
    Steps = 1 << 11,
    Notes = 1 << 12,
    Reminders = 1 << 13,
    CreatedAt = 1 << 14,
}

/// <summary>Something in one row that could not be read, named where the user can find it.</summary>
/// <param name="Line">The line in the file, one-based, as the spreadsheet numbers it.</param>
public sealed record CsvRowError(int Line, string Column, string Message);

/// <summary>
/// One row of a CSV, parsed but not yet reconciled with the workspace.
/// </summary>
/// <remarks>
/// Names, not ids: <see cref="CategoryName"/> and <see cref="TagNames"/> are
/// what the file carries, and turning them into ids needs the workspace, which
/// this layer deliberately does not have. <see cref="StatusId"/> is the
/// exception because an unknown status is a row error rather than something to
/// create, so it has to be resolved while the line number is still to hand.
/// <para>
/// Steps and notes arrive with empty ids. Which existing id each one keeps is a
/// question about the task it is being applied to, and is answered by
/// <see cref="ImportPlan"/>.
/// </para>
/// </remarks>
public sealed record CsvRow
{
    public required int Line { get; init; }

    /// <summary>Null when the cell was blank or the column absent: create a new task.</summary>
    public string? Id { get; init; }

    public string Summary { get; init; } = "";

    public string Description { get; init; } = "";

    public string StatusId { get; init; } = "";

    public string? CategoryName { get; init; }

    public IReadOnlyList<string> TagNames { get; init; } = [];

    public Priority Priority { get; init; } = Priority.Medium;

    public CalendarDate? DueDate { get; init; }

    public DateTime? CompletedAt { get; init; }

    public DateTime? CreatedAt { get; init; }

    public int? EstimateMinutes { get; init; }

    public int? TotalTimeMinutes { get; init; }

    public IReadOnlyList<Step> Steps { get; init; } = [];

    public IReadOnlyList<Note> Notes { get; init; } = [];

    /// <summary>Pending reminders only. See <c>docs/format/csv-v1.md</c>.</summary>
    public IReadOnlyList<DateTime> Reminders { get; init; } = [];
}

/// <summary>
/// What a CSV file turned out to contain.
/// </summary>
/// <remarks>
/// Errors are row-level, not file-level: one bad date on line 40 must not
/// refuse the other two hundred rows.
/// </remarks>
public sealed record CsvReadResult(
    IReadOnlyList<CsvRow> Rows,
    IReadOnlyList<CsvRowError> Errors,
    CsvColumns PresentColumns);

/// <summary>
/// A CSV file that could not be read at all.
/// </summary>
/// <remarks>
/// Kept to the cases where per-row recovery is meaningless — no header, no
/// recognisable columns, or a file too large to read — because everything else
/// is more useful reported as the row it happened on.
/// </remarks>
public sealed class CsvFormatException(string message) : Exception(message);

/// <summary>
/// The <c>csv-v1</c> interchange format, in both directions.
/// </summary>
/// <remarks>
/// Written for a human reading a spreadsheet, not for byte-perfect fidelity: the
/// workspace folder already does fidelity (ADR-0001), and a CSV that reproduced
/// it would be a wall of ULIDs nobody can edit. So the file carries status,
/// category and tag <i>names</i>, and is lossy by design — see ADR-0005 and
/// <c>docs/format/csv-v1.md</c>.
/// </remarks>
public static class TaskCsv
{
    /// <summary>
    /// The largest file we will read. The same limit, and the same reasoning, as
    /// the workspace's own.
    /// </summary>
    public const long MaxFileBytes = 16L * 1024 * 1024;

    /// <summary>The header row, in the order export writes it.</summary>
    private static readonly string[] Header =
    [
        "id", "summary", "description", "status", "statusType", "category", "tags",
        "priority", "dueDate", "completedAt", "estimateMinutes", "totalTimeMinutes",
        "steps", "notes", "reminders", "attachments", "createdAt", "updatedAt",
    ];

    /// <summary>Header name to the flag it sets, matched loosely. See <see cref="Normalise"/>.</summary>
    private static readonly Dictionary<string, CsvColumns> Recognised = new(StringComparer.Ordinal)
    {
        ["id"] = CsvColumns.Id,
        ["summary"] = CsvColumns.Summary,
        ["description"] = CsvColumns.Description,
        ["status"] = CsvColumns.Status,
        ["category"] = CsvColumns.Category,
        ["tags"] = CsvColumns.Tags,
        ["priority"] = CsvColumns.Priority,
        ["duedate"] = CsvColumns.DueDate,
        ["completedat"] = CsvColumns.CompletedAt,
        ["estimateminutes"] = CsvColumns.EstimateMinutes,
        ["totaltimeminutes"] = CsvColumns.TotalTimeMinutes,
        ["steps"] = CsvColumns.Steps,
        ["notes"] = CsvColumns.Notes,
        ["reminders"] = CsvColumns.Reminders,
        ["createdat"] = CsvColumns.CreatedAt,
    };

    // ---------------------------------------------------------------- writing

    /// <summary>Writes <paramref name="tasks"/> in the order given.</summary>
    public static string Write(IReadOnlyList<MightDoTask> tasks, WorkspaceConfig config)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(config);

        var builder = new StringBuilder();
        CsvWriter.WriteRow(builder, Header);

        foreach (var task in tasks)
        {
            CsvWriter.WriteRow(builder, Cells(task, config));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Writes to a stream as UTF-8 with a BOM.
    /// </summary>
    /// <remarks>
    /// The BOM is there for exactly one reason: Excel on Windows reads a UTF-8
    /// CSV as the local codepage without it, and <c>Café</c> arriving as
    /// <c>CafÃ©</c> in the first thing a user does with this feature is not
    /// acceptable.
    /// </remarks>
    public static async Task WriteAsync(
        Stream destination,
        IReadOnlyList<MightDoTask> tasks,
        WorkspaceConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var text = Write(tasks, config);

        await destination.WriteAsync(Encoding.UTF8.Preamble.ToArray(), cancellationToken);
        await destination.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
    }

    /// <summary>
    /// Writes an export to a path of the user's choosing.
    /// </summary>
    /// <remarks>
    /// Not through <c>TaskStore</c>: the destination is outside the workspace,
    /// so none of the workspace's write rules — the lock, the conflict copies,
    /// the stamping policy — have anything to say about it. It writes to a
    /// temporary file beside the target and renames anyway, so a failed export
    /// leaves no half-written file where the user expects their data.
    /// </remarks>
    public static async Task WriteFileAsync(
        string path,
        IReadOnlyList<MightDoTask> tasks,
        WorkspaceConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var temporary = $"{path}.{Ulid.New()}.tmp";
        try
        {
            await using (var file = File.Create(temporary))
            {
                await WriteAsync(file, tasks, config, cancellationToken);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    /// <summary>
    /// Reads a file the user chose, refusing one too large to be a task list.
    /// </summary>
    /// <exception cref="CsvFormatException">The file is larger than <see cref="MaxFileBytes"/>.</exception>
    public static async Task<string> ReadFileAsync(
        string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Checked before reading, so a file that is enormous by accident — a
        // truncated download, a sync client's mistake — fails as a refusal
        // rather than by taking the process down with it.
        var length = new FileInfo(path).Length;
        if (length > MaxFileBytes)
        {
            throw new CsvFormatException(
                $"That file is {length / (1024 * 1024)} MB. The largest this app will read is "
                + $"{MaxFileBytes / (1024 * 1024)} MB.");
        }

        return await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
    }

    private static string[] Cells(MightDoTask task, WorkspaceConfig config)
    {
        var status = config.StatusById(task.StatusId);

        return
        [
            task.Id,
            task.Summary,
            task.Description,
            status?.Name ?? "",
            status is null ? "" : StatusTypeName(status.Type),
            config.CategoryById(task.CategoryId)?.Name ?? "",
            string.Join(';', config.TagsByIds(task.TagIds).Select(tag => tag.Name)),
            PriorityName(task.Priority),
            task.DueDate?.ToIso() ?? "",
            Instant(task.CompletedAt),
            Number(task.EstimateMinutes),
            Number(task.TotalTimeMinutes),
            WriteSteps(task.Steps),
            WriteNotes(task.Notes),
            string.Join(';', task.Reminders.Where(r => r.IsPending).Select(r => Instants.ToIso(r.RemindAt))),
            string.Join(';', task.Attachments.Select(a => a.OriginalName)),
            Instants.ToIso(task.CreatedAt),
            Instants.ToIso(task.UpdatedAt),
        ];
    }

    private static string WriteSteps(IReadOnlyList<Step> steps) =>
        string.Join('\n', steps.Select(step => $"[{(step.Done ? "x" : " ")}] {Escape(step.Text)}"));

    private static string WriteNotes(IReadOnlyList<Note> notes) =>
        string.Join('\n', notes.Select(note => $"{Instants.ToIso(note.CreatedAt)}\t{Escape(note.Body)}"));

    /// <summary>
    /// Hides the line breaks inside a step or a note body.
    /// </summary>
    /// <remarks>
    /// One line per item is the whole grammar. A body carrying a literal newline
    /// would make the cell's line count stop matching its item count, and the
    /// cell unparseable — so the newline is written <c>\n</c>, and the backslash
    /// that makes that possible is written <c>\\</c>.
    /// </remarks>
    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\r\n", "\\n", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\n", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);

    private static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal)) return value;

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                builder.Append(value[i]);
                continue;
            }

            builder.Append(value[++i] switch
            {
                'n' => '\n',
                't' => '\t',
                '\\' => '\\',
                // An unknown escape is somebody's own backslash, kept as typed.
                var other => Keep(builder, other),
            });
        }

        return builder.ToString();

        static char Keep(StringBuilder builder, char other)
        {
            builder.Append('\\');
            return other;
        }
    }

    private static string Instant(DateTime? value) => value is { } moment ? Instants.ToIso(moment) : "";

    private static string Number(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string PriorityName(Priority priority) => priority.ToString().ToLowerInvariant();

    private static string StatusTypeName(StatusType type) => type.ToString().ToLowerInvariant();

    // ---------------------------------------------------------------- reading

    /// <summary>
    /// Parses <paramref name="text"/> against <paramref name="config"/>.
    /// </summary>
    /// <exception cref="CsvFormatException">
    /// The file has no header, or no column this format recognises.
    /// </exception>
    public static CsvReadResult Read(string text, WorkspaceConfig config)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(config);

        var lines = CsvReader.Read(text);
        if (lines.Count == 0) throw new CsvFormatException("That file is empty.");

        var (present, positions) = ReadHeader(lines[0]);
        if (present == CsvColumns.None)
        {
            throw new CsvFormatException(
                "That file's first row is not a header this app recognises. It needs at least "
                + "a 'summary' and a 'status' column.");
        }

        var rows = new List<CsvRow>();
        var errors = new List<CsvRowError>();
        var seenIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines.Skip(1))
        {
            var row = ReadRow(line, positions, present, config, errors);
            if (row is null) continue;

            if (row.Id is { } id && !seenIds.TryAdd(id, line.Number))
            {
                // Both rows are errors, and the first one is withdrawn: there is
                // no honest way to choose which of two rows claiming one task
                // should win.
                var first = seenIds[id];
                if (rows.RemoveAll(other => other.Id == id) > 0)
                {
                    errors.Add(new CsvRowError(
                        first, "id", $"'{id}' names the same task as another row in this file."));
                }

                errors.Add(new CsvRowError(
                    line.Number, "id", $"'{id}' is already used on line {first}."));
                continue;
            }

            rows.Add(row);
        }

        return new CsvReadResult(rows, errors, present);
    }

    /// <summary>
    /// Matches header cells to columns, ignoring case, spacing and order.
    /// </summary>
    /// <remarks>
    /// Unknown columns are skipped rather than refused, which is what lets a
    /// file another tool exported be imported without editing, and what lets a
    /// later csv-v2 add columns this reader has never heard of.
    /// </remarks>
    private static (CsvColumns Present, Dictionary<CsvColumns, int> Positions) ReadHeader(CsvLine header)
    {
        var present = CsvColumns.None;
        var positions = new Dictionary<CsvColumns, int>();

        for (var i = 0; i < header.Cells.Count; i++)
        {
            if (!Recognised.TryGetValue(Normalise(header.Cells[i]), out var column)) continue;

            // First wins, so a file with the column twice is read predictably.
            if (positions.TryAdd(column, i)) present |= column;
        }

        return (present, positions);
    }

    /// <summary>Lower-cased with spaces, underscores and hyphens dropped, so <c>Due Date</c>, <c>dueDate</c> and <c>due_date</c> are one column.</summary>
    private static string Normalise(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsWhiteSpace(c) || c is '_' or '-') continue;
            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    private static CsvRow? ReadRow(
        CsvLine line,
        Dictionary<CsvColumns, int> positions,
        CsvColumns present,
        WorkspaceConfig config,
        List<CsvRowError> errors)
    {
        var before = errors.Count;

        var id = Cell(CsvColumns.Id);
        if (id.Length > 0 && !Ulid.IsUlid(id))
        {
            Fail("id", $"'{id}' is not a task id.");
            id = "";
        }

        var summary = Cell(CsvColumns.Summary).Trim();
        if (present.HasFlag(CsvColumns.Summary) && summary.Length == 0)
        {
            Fail("summary", "A task needs a summary.");
        }

        var statusId = "";
        if (present.HasFlag(CsvColumns.Status))
        {
            var name = Cell(CsvColumns.Status).Trim();
            var status = config.Statuses.FirstOrDefault(
                s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

            if (status is null)
            {
                // Import never creates a status: one carries a type, an order
                // and a board visibility that a CSV cannot express, and
                // inventing those silently is what ADR-0002 exists to prevent.
                Fail(
                    "status",
                    name.Length == 0
                        ? "A task needs a status."
                        : $"There is no status called '{name}'. Add it in Settings first.");
            }
            else
            {
                statusId = status.Id;
            }
        }

        var priority = Priority.Medium;
        if (Cell(CsvColumns.Priority) is { Length: > 0 } priorityText
            && !Enum.TryParse(priorityText.Trim(), ignoreCase: true, out priority))
        {
            Fail("priority", $"'{priorityText}' is not low, medium, high or critical.");
        }

        CalendarDate? dueDate = null;
        if (Cell(CsvColumns.DueDate) is { Length: > 0 } dueText)
        {
            dueDate = CalendarDate.TryParse(dueText.Trim());
            if (dueDate is null) Fail("dueDate", $"'{dueText}' is not a date like 2026-08-21.");
        }

        var completedAt = Moment(CsvColumns.CompletedAt, "completedAt");
        var createdAt = Moment(CsvColumns.CreatedAt, "createdAt");
        var estimate = Minutes(CsvColumns.EstimateMinutes, "estimateMinutes");
        var totalTime = Minutes(CsvColumns.TotalTimeMinutes, "totalTimeMinutes");

        var reminders = new List<DateTime>();
        foreach (var entry in Split(Cell(CsvColumns.Reminders)))
        {
            if (Instants.TryParseIso(entry) is { } moment) reminders.Add(moment);
            else Fail("reminders", $"'{entry}' is not a date and time.");
        }

        if (errors.Count != before) return null;

        return new CsvRow
        {
            Line = line.Number,
            Id = id.Length == 0 ? null : id.ToLowerInvariant(),
            Summary = summary,
            Description = Cell(CsvColumns.Description),
            StatusId = statusId,
            CategoryName = Cell(CsvColumns.Category).Trim() is { Length: > 0 } category ? category : null,
            TagNames = [.. Split(Cell(CsvColumns.Tags)).Take(MightDoTask.MaxTags)],
            Priority = priority,
            DueDate = dueDate,
            CompletedAt = completedAt,
            CreatedAt = createdAt,
            EstimateMinutes = estimate,
            TotalTimeMinutes = totalTime,
            Steps = ReadSteps(Cell(CsvColumns.Steps)),
            Notes = ReadNotes(Cell(CsvColumns.Notes)),
            Reminders = reminders,
        };

        string Cell(CsvColumns column) =>
            positions.TryGetValue(column, out var at) && at < line.Cells.Count ? line.Cells[at] : "";

        void Fail(string column, string message) =>
            errors.Add(new CsvRowError(line.Number, column, message));

        DateTime? Moment(CsvColumns column, string name)
        {
            if (Cell(column) is not { Length: > 0 } text) return null;

            var parsed = Instants.TryParseIso(text.Trim());
            if (parsed is null) Fail(name, $"'{text}' is not a date and time.");
            return parsed;
        }

        int? Minutes(CsvColumns column, string name)
        {
            if (Cell(column) is not { Length: > 0 } text) return null;

            if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                && value >= 0)
            {
                return value;
            }

            Fail(name, $"'{text}' is not a whole number of minutes.");
            return null;
        }
    }

    private static IEnumerable<string> Split(string cell) => cell
        .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Reads the GitHub-checkbox grammar, forgivingly.
    /// </summary>
    /// <remarks>
    /// A line with no marker at all is a step that is not done — somebody typing
    /// three lines into a cell should get three steps, which is the point of
    /// using a notation the audience already reads.
    /// </remarks>
    private static IReadOnlyList<Step> ReadSteps(string cell)
    {
        var steps = new List<Step>();
        foreach (var line in Lines(cell))
        {
            var text = line;
            var done = false;

            if (text.StartsWith("[x]", StringComparison.OrdinalIgnoreCase))
            {
                done = true;
                text = text[3..];
            }
            else if (text.StartsWith("[ ]", StringComparison.Ordinal))
            {
                text = text[3..];
            }
            else if (text.StartsWith("[]", StringComparison.Ordinal))
            {
                text = text[2..];
            }

            steps.Add(new Step("", Unescape(text.Trim()), done));
        }

        return steps;
    }

    /// <summary>
    /// Reads <c>instant TAB body</c>, one note per line.
    /// </summary>
    /// <remarks>
    /// A tab rather than a space or a comma: the body is prose, and prose
    /// contains commas and dashes but almost never a literal tab. A line with no
    /// tab is a note whose timestamp is now — again so that typing prose into
    /// the cell works.
    /// </remarks>
    private static IReadOnlyList<Note> ReadNotes(string cell)
    {
        var notes = new List<Note>();
        foreach (var line in Lines(cell))
        {
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            var stamp = tab < 0 ? null : Instants.TryParseIso(line[..tab].Trim());

            // A default timestamp means "now": the clock belongs to the session
            // applying the import, not to this parser.
            notes.Add(new Note(
                "",
                stamp ?? default,
                Unescape(tab < 0 || stamp is null ? line.Trim() : line[(tab + 1)..])));
        }

        return notes;
    }

    private static IEnumerable<string> Lines(string cell) =>
        cell.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Trim().Length > 0);
}
