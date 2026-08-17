using MightDo.Core.Domain;
using MightDo.Core.Query;

namespace MightDo.Core.Tests;

/// <summary>
/// Where a dragged card lands. Kept in Core rather than the view model so it can
/// be tested without a window — dragging is the one board interaction that has
/// real logic behind it.
/// </summary>
public class BoardDropTests
{
    private readonly WorkspaceConfig _config = WorkspaceConfig.Seed();
    private readonly Status _active;
    private readonly Status _done;
    private readonly List<MightDoTask> _column = [];

    public BoardDropTests()
    {
        _active = _config.Statuses.First(s => s.Type == StatusType.Active);
        _done = _config.Statuses.First(s => s.Type == StatusType.Final);

        foreach (var (summary, rank) in new[] { ("a", "g"), ("b", "h"), ("c", "j") })
        {
            _column.Add(MightDoTask.Create(summary, _active.Id, rank));
        }
    }

    private MightDoTask Card(string summary) => _column.First(t => t.Summary == summary);

    private string RankFor(string taskId, string statusId, string? beforeTaskId)
    {
        var target = BoardProjection.DropTarget(_column, statusId, taskId, beforeTaskId);
        Assert.NotNull(target);
        return BoardProjection.RankBetween(target!.Value.Above, target.Value.Below);
    }

    [Fact]
    public void DroppingOnACardLandsAboveIt()
    {
        // c is dropped onto b, so it should end up between a and b.
        var target = BoardProjection.DropTarget(
            _column, _active.Id, Card("c").Id, Card("b").Id);

        Assert.NotNull(target);
        Assert.Equal("a", target!.Value.Above!.Summary);
        Assert.Equal("b", target.Value.Below!.Summary);
    }

    [Fact]
    public void DroppingOnTheTopCardLandsAboveEverything()
    {
        var target = BoardProjection.DropTarget(
            _column, _active.Id, Card("c").Id, Card("a").Id);

        Assert.NotNull(target);
        Assert.Null(target!.Value.Above);
        Assert.Equal("a", target.Value.Below!.Summary);
    }

    [Fact]
    public void DroppingOnEmptySpaceAppendsToTheBottom()
    {
        var target = BoardProjection.DropTarget(_column, _active.Id, Card("a").Id, null);

        Assert.NotNull(target);
        Assert.Equal("c", target!.Value.Above!.Summary);
        Assert.Null(target.Value.Below);
    }

    [Fact]
    public void DroppingIntoAnEmptyColumnHasNoNeighbours()
    {
        var target = BoardProjection.DropTarget(_column, _done.Id, Card("a").Id, null);

        Assert.NotNull(target);
        Assert.Null(target!.Value.Above);
        Assert.Null(target.Value.Below);
    }

    [Fact]
    public void DroppingACardOnItselfIsANoOp() =>
        Assert.Null(BoardProjection.DropTarget(
            _column, _active.Id, Card("b").Id, Card("b").Id));

    [Fact]
    public void DroppingOnACardThatHasSinceMovedIsRefused() =>
        Assert.Null(BoardProjection.DropTarget(
            _column, _active.Id, Card("a").Id, "01m07z0000000000000000gone"));

    [Fact]
    public void TheDraggedCardIsNeverItsOwnNeighbour()
    {
        // Otherwise Rank.Between would be asked for a rank between a value and
        // itself, which throws.
        var target = BoardProjection.DropTarget(
            _column, _active.Id, Card("b").Id, Card("c").Id);

        Assert.NotNull(target);
        Assert.Equal("a", target!.Value.Above!.Summary);
        Assert.Equal("c", target.Value.Below!.Summary);
    }

    [Fact]
    public void EveryDropProducesARankThatSortsWhereItWasDropped()
    {
        // Above b (between a and b)
        var middle = RankFor(Card("c").Id, _active.Id, Card("b").Id);
        Assert.True(string.CompareOrdinal("g", middle) < 0);
        Assert.True(string.CompareOrdinal(middle, "h") < 0);

        // Above a, i.e. the very top
        var top = RankFor(Card("c").Id, _active.Id, Card("a").Id);
        Assert.True(string.CompareOrdinal(top, "g") < 0);

        // The bottom
        var bottom = RankFor(Card("a").Id, _active.Id, null);
        Assert.True(string.CompareOrdinal("j", bottom) < 0);
    }

    [Fact]
    public void ReorderingRewritesOneRankNotTheWholeColumn()
    {
        // The point of the fractional index: a reorder touches one task file
        // rather than renumbering the column, because per-file sync makes a
        // whole-column rewrite the worst possible conflict shape (ADR-0001).
        var before = _column.Select(t => t.BoardRank).ToList();

        var moved = RankFor(Card("c").Id, _active.Id, Card("b").Id);

        Assert.Equal(before, _column.Select(t => t.BoardRank));
        Assert.DoesNotContain(moved, before);
    }
}
