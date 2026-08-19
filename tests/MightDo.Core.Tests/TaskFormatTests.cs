using System.Text.Json.Nodes;
using MightDo.Core.Domain;
using MightDo.Core.Serialization;

namespace MightDo.Core.Tests;

/// <summary>
/// Conformance against <c>fixtures/workspace-v1/</c> and
/// <c>fixtures/tolerance/</c>. A port passes when it reads every value the
/// original implementation wrote and writes values that survive the trip back.
/// </summary>
public class TaskFormatTests
{
    public static TheoryData<string> CanonicalTaskFiles()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(Fixtures.Path("workspace-v1", "tasks")))
        {
            var name = Path.GetFileName(path);
            if (Storage.WorkspaceFiles.IsOwnTaskFile(name)) data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CanonicalTaskFiles))]
    public void RoundTripsEveryCanonicalTaskFile(string fileName)
    {
        var original = Fixtures.ReadNode("workspace-v1", "tasks", fileName);

        var task = WorkspaceJson.Deserialize<MightDoTask>(original.ToJsonString())!;
        var rewritten = JsonNode.Parse(WorkspaceJson.Serialize(task));

        JsonAssert.SemanticallyEqual(original, rewritten, $"{fileName} did not round-trip");
    }

    [Fact]
    public void TheCorpusIsActuallyExercisingTheAwkwardCases()
    {
        // Guards the fixtures themselves: a corpus that quietly stopped covering
        // the hard cases would let a broken port pass.
        var files = Directory
            .EnumerateFiles(Fixtures.Path("workspace-v1", "tasks"))
            .Select(Path.GetFileName)
            .Where(name => Storage.WorkspaceFiles.IsOwnTaskFile(name!))
            .ToList();

        Assert.Equal(5, files.Count);
    }

    [Fact]
    public void ReadsATaskWithEveryFieldPopulated()
    {
        var task = Load("01m07z000000000000000000t1.json");

        Assert.Equal(MightDoTask.MaxTags, task.TagIds.Count);
        Assert.Equal(Priority.Critical, task.Priority);
        Assert.Equal(new CalendarDate(2026, 8, 21), task.DueDate);
        Assert.Contains(task.Steps, s => s.Done);
        Assert.Contains(task.Steps, s => !s.Done);
        Assert.Equal(2, task.StepsDone);
        Assert.Equal(75, task.EstimateVariance);
        Assert.Single(task.Attachments);

        // The three reminder states the format distinguishes.
        Assert.Contains(task.Reminders, r => r.IsPending);
        Assert.Contains(task.Reminders, r => !r.IsPending && r.IsOutstanding);
        Assert.Contains(task.Reminders, r => !r.IsOutstanding);
    }

    [Fact]
    public void ReadsATaskWithNothingOptionalSet()
    {
        var task = Load("01m07z000000000000000000t2.json");

        Assert.Null(task.CategoryId);
        Assert.Null(task.DueDate);
        Assert.Null(task.CompletedAt);
        Assert.Null(task.EstimateMinutes);
        Assert.Empty(task.TagIds);
        Assert.Empty(task.Notes);
        Assert.Equal("", task.Description);
        Assert.Equal(Priority.Medium, task.Priority);
    }

    [Fact]
    public void PreservesTextThatABrokenEncoderWouldMangle()
    {
        var task = Load("01m07z000000000000000000t3.json");

        // The escaping canary: emoji, CJK, quotes and HTML-sensitive characters.
        Assert.Contains("日本語", task.Summary);
        Assert.Contains("🎉", task.Summary);
        Assert.Contains("<tagged>", task.Summary);
        Assert.Contains("\"quoted\"", task.Summary);
        Assert.Contains("\\", task.Description);
        Assert.Contains("\n", task.Description);

        // And it must come back out as literal text, not as \uXXXX. This is the
        // System.Text.Json default that docs/format/workspace-v1.md calls out:
        // it escapes non-ASCII and HTML-sensitive characters unless told not to,
        // which would defeat the greppability ADR-0001 is built on.
        var written = WorkspaceJson.Serialize(task);
        Assert.Contains("Café", written);
        Assert.Contains("日本語", written);
        Assert.Contains("<tagged>", written);
        Assert.Contains("\"quoted\\\"", written); // quotes escaped, not "
        Assert.DoesNotContain("\\u00e9", written); // é
        Assert.DoesNotContain("\\u003c", written); // <
        Assert.DoesNotContain("\\u0026", written); // &

        // Two things System.Text.Json escapes no matter which encoder we pick,
        // both semantically identical to what the canonical corpus holds:
        //   - control characters, which JSON requires escaped
        //   - astral-plane characters, emitted as surrogate pairs
        // See the "Notes for the .NET port" section of the format spec.
        Assert.Contains("\\u0001", written);
        Assert.Contains("\\uD83C\\uDF89", written); // 🎉
    }

    [Fact]
    public void StampsCompletionOnATaskInAFinalStatus()
    {
        var config = WorkspaceJson.Deserialize<WorkspaceConfig>(
            Fixtures.ReadText("workspace-v1", "config.json"))!;
        var task = Load("01m07z000000000000000000t4.json");

        Assert.True(config.IsFinal(task.StatusId));
        Assert.NotNull(task.CompletedAt);
        Assert.True(task.IsComplete);
        Assert.Equal(DateTimeKind.Utc, task.CompletedAt!.Value.Kind);
    }

    [Fact]
    public void TreatsBoardRankAsAStringNotANumber()
    {
        var deep = Load("01m07z000000000000000000t5.json");
        var maximal = Load("01m07z000000000000000000t1.json");

        Assert.Equal("hzzzzy", deep.BoardRank);

        // 'hzzzzy' sorts before 'h'... only under a comparison that is wrong.
        Assert.True(string.CompareOrdinal(maximal.BoardRank, deep.BoardRank) < 0);
    }

    [Theory]
    [InlineData("sparse")]
    [InlineData("offset-timestamps")]
    [InlineData("future-keys")]
    public void NormalisesToleratedInputToTheCanonicalForm(string name)
    {
        var input = Fixtures.ReadText("tolerance", "input", $"{name}.json");
        var expected = Fixtures.ReadNode("tolerance", "expected", $"{name}.json");

        var task = WorkspaceJson.Deserialize<MightDoTask>(input)!;
        var actual = JsonNode.Parse(WorkspaceJson.Serialize(task));

        JsonAssert.SemanticallyEqual(expected, actual,
            $"tolerance case '{name}' did not normalise");
    }

    [Fact]
    public void AppliesTheDocumentedDefaultsWhenKeysAreAbsent()
    {
        var task = WorkspaceJson.Deserialize<MightDoTask>(
            Fixtures.ReadText("tolerance", "input", "sparse.json"))!;

        Assert.Equal("", task.Description);
        Assert.Equal(Priority.Medium, task.Priority);
        Assert.Equal("i", task.BoardRank);
        Assert.Empty(task.TagIds);
        Assert.Empty(task.Steps);
        Assert.Empty(task.Reminders);
    }

    [Fact]
    public void ConvertsANonUtcTimestampRatherThanReinterpretingIt()
    {
        var task = WorkspaceJson.Deserialize<MightDoTask>(
            Fixtures.ReadText("tolerance", "input", "offset-timestamps.json"))!;

        // '2026-08-16T16:22:09+02:00' is 14:22:09 UTC. A port that ignored the
        // offset would be two hours out and never notice.
        Assert.Equal(
            new DateTime(2026, 8, 16, 14, 22, 9, DateTimeKind.Utc),
            task.CompletedAt);
    }

    private static MightDoTask Load(string fileName) =>
        WorkspaceJson.Deserialize<MightDoTask>(
            Fixtures.ReadText("workspace-v1", "tasks", fileName))!;
}
