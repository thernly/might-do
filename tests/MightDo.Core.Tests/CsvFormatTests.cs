using System.Text;
using MightDo.Core.Domain;
using MightDo.Core.Interchange;
using MightDo.Core.Storage;

namespace MightDo.Core.Tests;

/// <summary>
/// Pins <c>csv-v1</c> down the way <c>workspace-v1</c> is pinned: against a
/// fixture corpus, so the format is a set of files rather than whatever the
/// code happens to do today. See <c>fixtures/csv-v1/</c>.
/// </summary>
public class CsvFormatTests
{
    private static async Task<LoadedWorkspace> CorpusAsync() =>
        await new TaskStore(new Core.Storage.Workspace(Fixtures.Path("workspace-v1"))).LoadAsync();

    private static IReadOnlyList<MightDoTask> Ordered(LoadedWorkspace loaded) =>
        [.. loaded.Tasks.OrderBy(task => task.Id, StringComparer.Ordinal)];

    // ---- writing -----------------------------------------------------------

    [Fact]
    public async Task TheCorpusExportsToTheFixtureByteForByte()
    {
        var loaded = await CorpusAsync();

        var written = TaskCsv.Write(Ordered(loaded), loaded.Config);

        Assert.Equal(Fixtures.ReadText("csv-v1", "export", "workspace-v1.csv"), written);
    }

    [Fact]
    public async Task TheFileIsUtf8WithABomAndCrlfEndings()
    {
        var loaded = await CorpusAsync();

        using var stream = new MemoryStream();
        await TaskCsv.WriteAsync(stream, Ordered(loaded), loaded.Config);
        var bytes = stream.ToArray();

        // The BOM earns its place for one reason: Excel on Windows reads a
        // UTF-8 CSV as the local codepage without it.
        Assert.Equal(Encoding.UTF8.Preamble.ToArray(), bytes[..3]);
        Assert.Equal("\r\n"u8.ToArray(), bytes[^2..]);
    }

    /// <summary>
    /// A summary starting <c>=</c> is a summary starting <c>=</c>. The usual
    /// mitigation prefixes an apostrophe, which corrupts the user's own data in
    /// the user's own file, permanently, on the way out. See ADR-0005.
    /// </summary>
    [Fact]
    public void AFormulaLookingSummaryIsWrittenAndReadBackUnchanged()
    {
        var config = WorkspaceConfig.Seed();
        var status = config.StatusById(config.DefaultStatusId)!;
        const string Dangerous = "=cmd|'/c calc'!A0";

        var written = TaskCsv.Write(
            [MightDoTask.Create(Dangerous, status.Id, "i")], config);

        Assert.Contains(Dangerous, written, StringComparison.Ordinal);
        Assert.Equal(Dangerous, TaskCsv.Read(written, config).Rows.Single().Summary);
    }

    // ---- reading -----------------------------------------------------------

    [Fact]
    public async Task ASemicolonFileWithReorderedMixedCaseHeadersReadsAsItLooks()
    {
        var config = (await CorpusAsync()).Config;

        var read = TaskCsv.Read(
            Fixtures.ReadText("csv-v1", "tolerance", "semicolons.csv"), config);

        Assert.Empty(read.Errors);
        Assert.Collection(
            read.Rows,
            first =>
            {
                Assert.Equal("Pay the water bill", first.Summary);
                Assert.Equal("Not Started", config.StatusById(first.StatusId)!.Name);
                Assert.Equal(new CalendarDate(2026, 9, 1), first.DueDate);
                Assert.Equal(Priority.High, first.Priority);
            },
            second =>
            {
                Assert.Equal("Fix the gate", second.Summary);
                Assert.Null(second.DueDate);
                Assert.Equal(Priority.Low, second.Priority);
            });
    }

    /// <summary>
    /// Absent is not blank. A file with no <c>notes</c> column says nothing
    /// about notes, and the columns it does carry are the only ones an import
    /// may touch.
    /// </summary>
    [Fact]
    public async Task AFileWithOnlyTheRequiredColumnsSaysNothingAboutTheRest()
    {
        var config = (await CorpusAsync()).Config;

        var read = TaskCsv.Read(Fixtures.ReadText("csv-v1", "tolerance", "plain.csv"), config);

        Assert.Equal(CsvColumns.Summary | CsvColumns.Status, read.PresentColumns);

        // The trailing blank line is a blank line, not a task with no summary.
        Assert.Equal(2, read.Rows.Count);
        Assert.Empty(read.Errors);
    }

    [Fact]
    public async Task AQuotedCellKeepsItsCommasQuotesAndLineBreaks()
    {
        var config = (await CorpusAsync()).Config;

        var read = TaskCsv.Read(Fixtures.ReadText("csv-v1", "tolerance", "quoted.csv"), config);

        var row = Assert.Single(read.Rows);
        Assert.Equal("Tea, milk and \"two\" sugars", row.Summary);

        // The file uses lone CRs; embedded breaks come back as LF whatever the
        // file used, so the grammars that count lines can rely on it.
        Assert.Equal("Line one\nLine two, with a comma", row.Description);
    }

    [Fact]
    public void AFileWithNoRecognisableHeaderIsRefusedWhole()
    {
        var config = WorkspaceConfig.Seed();

        // Per-row recovery is meaningless here: there is nothing to say which
        // cell was meant to be which.
        Assert.Throws<CsvFormatException>(
            () => TaskCsv.Read("alpha,beta\r\n1,2\r\n", config));
    }

    [Fact]
    public void AnEmptyFileIsRefusedWhole() =>
        Assert.Throws<CsvFormatException>(() => TaskCsv.Read("", WorkspaceConfig.Seed()));

    [Fact]
    public async Task AFileOverTheSizeLimitIsRefusedWithoutBeingRead()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mightdo-huge-{Guid.NewGuid():N}.csv");
        try
        {
            // Sparse where the filesystem allows it: the point is that the
            // length is checked, not that 17 MB is written.
            await using (var file = File.Create(path))
            {
                file.SetLength(TaskCsv.MaxFileBytes + 1);
            }

            await Assert.ThrowsAsync<CsvFormatException>(() => TaskCsv.ReadFileAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- the grammars inside cells -----------------------------------------

    [Fact]
    public void StepsReadAsCheckboxesAndAsPlainLines()
    {
        var config = WorkspaceConfig.Seed();
        var status = config.Statuses.First();

        var read = TaskCsv.Read(
            $"summary,status,steps\r\nGrammar,{status.Name},\"[x] Done one\n[ ] Not this\n[]Nor this\nJust typed\"\r\n",
            config);

        Assert.Collection(
            read.Rows.Single().Steps,
            step => Assert.Equal(("Done one", true), (step.Text, step.Done)),
            step => Assert.Equal(("Not this", false), (step.Text, step.Done)),
            step => Assert.Equal(("Nor this", false), (step.Text, step.Done)),
            step => Assert.Equal(("Just typed", false), (step.Text, step.Done)));
    }

    [Fact]
    public void ANoteLineWithNoTimestampIsANoteAllTheSame()
    {
        var config = WorkspaceConfig.Seed();
        var status = config.Statuses.First();

        var read = TaskCsv.Read(
            $"summary,status,notes\r\nGrammar,{status.Name},\"2026-08-14T09:12:00Z\tDated\nJust prose\"\r\n",
            config);

        Assert.Collection(
            read.Rows.Single().Notes,
            note =>
            {
                Assert.Equal("Dated", note.Body);
                Assert.Equal(new DateTime(2026, 8, 14, 9, 12, 0, DateTimeKind.Utc), note.CreatedAt);
            },
            note =>
            {
                Assert.Equal("Just prose", note.Body);

                // Undated: the clock belongs to the session applying the import.
                Assert.Equal(default, note.CreatedAt);
            });
    }

    [Fact]
    public void ANoteBodysOwnLineBreaksSurviveTheOneLinePerNoteGrammar()
    {
        var config = WorkspaceConfig.Seed();
        var status = config.Statuses.First();
        var task = MightDoTask.Create("Wrapped", status.Id, "i") with
        {
            Notes = [new Note("01m07z000000000000000000n1", new DateTime(2026, 8, 14, 9, 0, 0, DateTimeKind.Utc), "One\nTwo\\Three")],
        };

        var written = TaskCsv.Write([task], config);

        // Written as an escape, so the cell's line count still equals its note
        // count and the grammar stays parseable.
        Assert.Contains("One\\nTwo\\\\Three", written, StringComparison.Ordinal);
        Assert.Equal("One\nTwo\\Three", TaskCsv.Read(written, config).Rows.Single().Notes.Single().Body);
    }
}
