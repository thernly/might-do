using System.Text.Json.Nodes;
using MightDo.Core.Domain;
using MightDo.Core.Serialization;

namespace MightDo.Core.Tests;

public class WorkspaceConfigTests
{
    [Fact]
    public void RoundTripsTheCanonicalConfig()
    {
        var original = Fixtures.ReadNode("workspace-v1", "config.json");

        var config = WorkspaceJson.Deserialize<WorkspaceConfig>(original.ToJsonString())!;
        var rewritten = JsonNode.Parse(WorkspaceJson.Serialize(config));

        JsonAssert.SemanticallyEqual(original, rewritten,
            "config.json did not survive a read/write round-trip");
    }

    [Fact]
    public void NormalisesAConfigWhoseStatusesAreOutOfOrder()
    {
        var input = Fixtures.ReadText("tolerance", "input", "config-unsorted.json");
        var expected = Fixtures.ReadNode("tolerance", "expected", "config-unsorted.json");

        var config = WorkspaceJson.Deserialize<WorkspaceConfig>(input)!;
        var actual = JsonNode.Parse(WorkspaceJson.Serialize(config));

        JsonAssert.SemanticallyEqual(expected, actual,
            "an unsorted config did not normalise to the canonical form");
    }

    [Fact]
    public void ReadsTheClosedSetOfStatusTypes()
    {
        var config = LoadCanonical();

        Assert.Equal(StatusType.Initial, config.StatusById(Ids.Backlog)!.Type);
        Assert.Equal(StatusType.Active, config.StatusById(Ids.InProgress)!.Type);
        Assert.Equal(StatusType.Final, config.StatusById(Ids.Done)!.Type);

        // Several statuses share a type routinely — there is no "the done
        // status". See ADR-0002.
        Assert.Equal(2, config.Statuses.Count(s => s.Type == StatusType.Final));
        Assert.True(config.IsFinal(Ids.Done));
        Assert.True(config.IsFinal(Ids.Abandoned));
        Assert.False(config.IsFinal(Ids.InProgress));
    }

    [Fact]
    public void OrdersStatusesAndHidesTheOnesFlaggedOffTheBoard()
    {
        var config = LoadCanonical();

        Assert.Equal(
            ["Backlog", "Not Started", "In Progress", "Blocked", "Done", "Abandoned"],
            config.Statuses.Select(s => s.Name));

        // Backlog and Abandoned are hidden so they don't swamp the board.
        Assert.Equal(
            ["Not Started", "In Progress", "Blocked", "Done"],
            config.BoardStatuses.Select(s => s.Name));
    }

    [Fact]
    public void ReadsAnOpaqueArgbColourWithoutOverflowing()
    {
        var config = LoadCanonical();

        // 0xFF2E7D32 exceeds int.MaxValue. A signed 32-bit field overflows on
        // every fully opaque colour the app has ever written.
        var work = config.CategoryById(Ids.CategoryWork)!;
        Assert.Equal(0xFF2E7D32u, work.Color);
        Assert.True(work.Color > int.MaxValue);
    }

    [Fact]
    public void SkipsTagsThatNoLongerExist()
    {
        var config = LoadCanonical();

        var tags = config.TagsByIds([config.Tags[0].Id, "01m07z0000000000000000gone"]);

        Assert.Single(tags);
    }

    [Fact]
    public void SeedsAUsableWorkspace()
    {
        var seed = WorkspaceConfig.Seed();

        Assert.Equal(6, seed.Statuses.Count);
        Assert.Contains(seed.Statuses, s => s.Type == StatusType.Initial);
        Assert.Contains(seed.Statuses, s => s.Type == StatusType.Active);
        Assert.Contains(seed.Statuses, s => s.Type == StatusType.Final);

        // The default status is always Initial. That designation means nothing else.
        Assert.Equal(StatusType.Initial, seed.StatusById(seed.DefaultStatusId)!.Type);

        // A seeded workspace must survive the format it will be written in.
        var rewritten = WorkspaceJson.Deserialize<WorkspaceConfig>(
            WorkspaceJson.Serialize(seed))!;
        Assert.Equal(seed.DefaultStatusId, rewritten.DefaultStatusId);
        Assert.Equal(seed.Statuses, rewritten.Statuses);
    }

    private static WorkspaceConfig LoadCanonical() =>
        WorkspaceJson.Deserialize<WorkspaceConfig>(
            Fixtures.ReadText("workspace-v1", "config.json"))!;

    private static class Ids
    {
        private const string Prefix = "01m07z000000000000000000";
        public const string Backlog = Prefix + "s1";
        public const string InProgress = Prefix + "s3";
        public const string Done = Prefix + "s5";
        public const string Abandoned = Prefix + "s6";
        public const string CategoryWork = Prefix + "c1";
    }
}
