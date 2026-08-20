using System.Text;

namespace MightDo.Core.Interchange;

/// <summary>One record read from a CSV file, with the line it started on.</summary>
/// <param name="Number">
/// One-based, counting physical lines, so it is the number the user's
/// spreadsheet shows. A quoted cell containing newlines spans several.
/// </param>
internal sealed record CsvLine(int Number, IReadOnlyList<string> Cells);

/// <summary>
/// Writes RFC 4180: comma-delimited, <c>"</c>-quoted, CRLF-terminated.
/// </summary>
/// <remarks>
/// Hand-rolled rather than taken as a dependency. <c>MightDo.Core</c> has no
/// third-party references, the grammar is small enough to read in one sitting,
/// and the fixtures under <c>fixtures/csv-v1/</c> pin it down.
/// </remarks>
internal static class CsvWriter
{
    /// <summary>Every line ends with one, including the last.</summary>
    public const string LineBreak = "\r\n";

    public static void WriteRow(StringBuilder builder, IReadOnlyList<string> cells)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0) builder.Append(',');
            AppendCell(builder, cells[i]);
        }

        builder.Append(LineBreak);
    }

    /// <summary>
    /// Quotes only when the value would otherwise be misread.
    /// </summary>
    /// <remarks>
    /// Quoting everything would be simpler to write and worse to read: the file
    /// is meant to be opened in a text editor as well as a spreadsheet.
    /// </remarks>
    private static void AppendCell(StringBuilder builder, string value)
    {
        if (value.AsSpan().IndexOfAny(",\"\r\n") < 0)
        {
            builder.Append(value);
            return;
        }

        builder.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
    }
}

/// <summary>
/// Reads RFC 4180, and rather more besides.
/// </summary>
/// <remarks>
/// Deliberately more liberal than <see cref="CsvWriter"/>, in the same spirit as
/// the JSON reader: the BOM may be there or not, lines may end LF, CRLF or CR,
/// and the delimiter is sniffed from the header because a European Excel writes
/// semicolons. What it never does is guess at a cell's meaning — that is
/// <see cref="TaskCsv"/>'s job, and it reports what it cannot read rather than
/// inventing a value.
/// </remarks>
internal static class CsvReader
{
    private static readonly char[] Delimiters = [',', ';', '\t'];

    /// <summary>Reads every record, skipping rows that are entirely empty.</summary>
    public static IReadOnlyList<CsvLine> Read(string text)
    {
        var span = text.AsSpan();
        if (span.Length > 0 && span[0] == '\uFEFF') span = span[1..];

        var delimiter = SniffDelimiter(span);
        var rows = new List<CsvLine>();
        var cells = new List<string>();
        var cell = new StringBuilder();

        var line = 1;
        var rowLine = 1;
        var quoted = false;
        var index = 0;

        while (index < span.Length)
        {
            var c = span[index];

            if (quoted)
            {
                if (c == '"')
                {
                    // A doubled quote is a literal one; a lone quote ends the cell.
                    if (index + 1 < span.Length && span[index + 1] == '"')
                    {
                        cell.Append('"');
                        index += 2;
                        continue;
                    }

                    quoted = false;
                    index++;
                    continue;
                }

                // Embedded breaks are normalised to LF, whatever the file used,
                // so a cell's line count equals what the grammars below expect.
                if (c is '\r' or '\n')
                {
                    if (c == '\r' && index + 1 < span.Length && span[index + 1] == '\n') index++;
                    cell.Append('\n');
                    line++;
                    index++;
                    continue;
                }

                cell.Append(c);
                index++;
                continue;
            }

            if (c == '"' && cell.Length == 0)
            {
                quoted = true;
                index++;
                continue;
            }

            if (c == delimiter)
            {
                cells.Add(cell.ToString());
                cell.Clear();
                index++;
                continue;
            }

            if (c is '\r' or '\n')
            {
                if (c == '\r' && index + 1 < span.Length && span[index + 1] == '\n') index++;
                index++;
                line++;

                cells.Add(cell.ToString());
                cell.Clear();
                Emit(rows, cells, rowLine);
                cells = [];
                rowLine = line;
                continue;
            }

            cell.Append(c);
            index++;
        }

        if (cell.Length > 0 || cells.Count > 0)
        {
            cells.Add(cell.ToString());
            Emit(rows, cells, rowLine);
        }

        return rows;
    }

    /// <summary>
    /// A row of nothing but empty cells is dropped, so a trailing blank line —
    /// which most spreadsheets write — is not read as an empty task.
    /// </summary>
    private static void Emit(List<CsvLine> rows, List<string> cells, int line)
    {
        if (cells.All(string.IsNullOrWhiteSpace)) return;

        rows.Add(new CsvLine(line, cells));
    }

    /// <summary>
    /// Picks the delimiter by counting candidates in the header, outside quotes.
    /// </summary>
    /// <remarks>
    /// Only the header is looked at: it is the one line whose shape the format
    /// fixes, so a comma inside somebody's task summary cannot outvote it. Ties
    /// go to the comma, which is what this app writes.
    /// </remarks>
    private static char SniffDelimiter(ReadOnlySpan<char> text)
    {
        Span<int> counts = stackalloc int[Delimiters.Length];
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (quoted) continue;
            if (c is '\r' or '\n') break;

            var at = Delimiters.AsSpan().IndexOf(c);
            if (at >= 0) counts[at]++;
        }

        var best = 0;
        for (var i = 1; i < counts.Length; i++)
        {
            if (counts[i] > counts[best]) best = i;
        }

        return counts[best] == 0 ? ',' : Delimiters[best];
    }
}
