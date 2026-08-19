using System.Text.Json.Nodes;
using MightDo.Core.Domain;
using MightDo.Core.Serialization;

namespace MightDo.Core.Tests;

/// <summary>
/// Conformance against <c>fixtures/interop/dotnet-written/</c> — the corpus
/// this implementation publishes as the ".NET wrote this" half of
/// <c>docs/format/workspace-v1.md</c>.
/// </summary>
/// <remarks>
/// The other corpora were written by the original implementation, so reading
/// them proves something about a foreign writer. This one this implementation
/// wrote itself, so round-tripping it proves nothing about correctness — it is
/// a change detector, and deliberately so. The test that used to read these
/// files is gone, which left a directory of committed JSON that nothing
/// checked and that serialization changes could silently invalidate.
/// <para>
/// A failure here means the format this implementation writes has moved away
/// from what is published. That is a decision, not a breakage: either the
/// change was unintended, or the specification needs regenerating with
/// <c>tools/MightDo.FixtureWriter</c> and the consequences for anything already
/// written against it accepted.
/// </para>
/// </remarks>
public class InteropCorpusTests
{
    public static TheoryData<string> TaskFiles()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(Fixtures.Path("interop", "dotnet-written", "tasks")))
        {
            var name = Path.GetFileName(path);
            if (Storage.WorkspaceFiles.IsOwnTaskFile(name)) data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TaskFiles))]
    public void RoundTripsEveryPublishedTaskFile(string fileName)
    {
        var original = Fixtures.ReadNode("interop", "dotnet-written", "tasks", fileName);

        var task = WorkspaceJson.Deserialize<MightDoTask>(original.ToJsonString())!;
        var rewritten = JsonNode.Parse(WorkspaceJson.Serialize(task));

        JsonAssert.SemanticallyEqual(original, rewritten,
            $"{fileName} no longer matches what this implementation writes");
    }

    [Fact]
    public void RoundTripsThePublishedConfig()
    {
        var original = Fixtures.ReadNode("interop", "dotnet-written", "config.json");

        var config = WorkspaceJson.Deserialize<WorkspaceConfig>(original.ToJsonString())!;
        var rewritten = JsonNode.Parse(WorkspaceJson.Serialize(config));

        JsonAssert.SemanticallyEqual(original, rewritten,
            "config.json no longer matches what this implementation writes");
    }

    [Fact]
    public void RoundTripsTheTrashedTask()
    {
        // .trash/ is part of the format and holds whole task files, so a change
        // that only broke trashed tasks would otherwise go unnoticed here.
        var dir = Fixtures.Path("interop", "dotnet-written", ".trash", "tasks");
        var fileName = Directory.EnumerateFiles(dir).Select(Path.GetFileName).Single();

        var original = Fixtures.ReadNode("interop", "dotnet-written", ".trash", "tasks", fileName!);

        var task = WorkspaceJson.Deserialize<MightDoTask>(original.ToJsonString())!;
        var rewritten = JsonNode.Parse(WorkspaceJson.Serialize(task));

        JsonAssert.SemanticallyEqual(original, rewritten,
            $"{fileName} no longer matches what this implementation writes");
    }

    [Fact]
    public void TheCorpusStillCoversTheAwkwardCases()
    {
        // Guards the corpus itself. Regenerating from a workspace that had
        // drifted would quietly shrink what is published, and every round-trip
        // above would still pass.
        var files = Directory
            .EnumerateFiles(Fixtures.Path("interop", "dotnet-written", "tasks"))
            .Select(Path.GetFileName)
            .Where(name => Storage.WorkspaceFiles.IsOwnTaskFile(name!))
            .ToList();

        Assert.Equal(6, files.Count);
    }
}
