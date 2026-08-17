using MightDo.Core.Domain;
using MightDo.Core.Query;

namespace MightDo.Core.Tests;

public class BoardProjectionTests
{
    private readonly WorkspaceConfig _config = WorkspaceConfig.Seed();

    private MightDoTask Task(string summary, Status status, string rank) =>
        MightDoTask.Create(summary, status.Id, rank);

    private Status Status(StatusType type) => _config.Statuses.First(s => s.Type == type);

    [Fact]
    public void OrdersAColumnByBoardRank()
    {
        var status = Status(StatusType.Active);
        List<MightDoTask> tasks =
        [
            Task("third", status, "j"),
            Task("first", status, "g"),
            Task("second", status, "h"),
        ];

        var column = BoardProjection.Column(tasks, status.Id);

        Assert.Equal(["first", "second", "third"], column.Select(t => t.Summary));
    }

    [Fact]
    public void OrdersRanksOrdinallyNotByCulture()
    {
        // The trap docs/format/workspace-v1.md calls out: the default .NET string
        // comparer is culture-sensitive and would order these differently, which
        // shows up as cards silently in the wrong place rather than as an error.
        var status = Status(StatusType.Active);
        List<MightDoTask> tasks =
        [
            Task("deep", status, "hzzzzy"),
            Task("after", status, "i"),
            Task("before", status, "h"),
        ];

        var column = BoardProjection.Column(tasks, status.Id);

        Assert.Equal(["before", "deep", "after"], column.Select(t => t.Summary));
    }

    [Fact]
    public void KeepsColumnsDisjointByStatus()
    {
        var active = Status(StatusType.Active);
        var initial = Status(StatusType.Initial);
        List<MightDoTask> tasks =
        [
            Task("doing", active, "h"),
            Task("waiting", initial, "h"),
        ];

        Assert.Equal(["doing"], BoardProjection.Column(tasks, active.Id).Select(t => t.Summary));
        Assert.Equal(["waiting"], BoardProjection.Column(tasks, initial.Id).Select(t => t.Summary));
    }

    [Fact]
    public void SkipsStatusesFlaggedOffTheBoard()
    {
        var columns = BoardProjection.Columns([], _config);

        // Backlog and Abandoned are hidden in the seed so they don't swamp it.
        Assert.Equal(["Not Started", "In Progress", "Blocked", "Done"],
            columns.Select(c => c.Status.Name));
    }

    [Fact]
    public void ShowsFinalStatusesEvenThoughTheListViewHidesThem()
    {
        // The board is a different view, not a query option: a column headed
        // "Done" with nothing in it would be worse than useless.
        var done = Status(StatusType.Final);
        List<MightDoTask> tasks = [Task("shipped", done, "h")];

        var columns = BoardProjection.Columns(tasks, _config);

        var doneColumn = Assert.Single(columns, c => c.Status.Id == done.Id);
        Assert.Equal(["shipped"], doneColumn.Tasks.Select(t => t.Summary));

        // ...and the list view still hides it.
        Assert.Empty(TaskQuery.Default.Apply(tasks, _config));
    }

    [Fact]
    public void AppendsToTheBottomOfAColumn()
    {
        var status = Status(StatusType.Active);
        List<MightDoTask> tasks = [Task("first", status, "h"), Task("second", status, "j")];
        var column = BoardProjection.Column(tasks, status.Id);

        var rank = BoardProjection.RankForBottomOf(column);

        Assert.True(string.CompareOrdinal(column[^1].BoardRank, rank) < 0);
    }

    [Fact]
    public void AppendsToAnEmptyColumn()
    {
        var rank = BoardProjection.RankForBottomOf([]);

        Assert.True(Rank.IsValid(rank));
    }

    [Fact]
    public void DropsACardBetweenTwoOthers()
    {
        var status = Status(StatusType.Active);
        var above = Task("above", status, "h");
        var below = Task("below", status, "j");

        var rank = BoardProjection.RankBetween(above, below);

        Assert.True(string.CompareOrdinal(above.BoardRank, rank) < 0);
        Assert.True(string.CompareOrdinal(rank, below.BoardRank) < 0);
    }

    [Fact]
    public void DropsACardAtEitherEndOfAColumn()
    {
        var status = Status(StatusType.Active);
        var only = Task("only", status, "h");

        var top = BoardProjection.RankBetween(null, only);
        var bottom = BoardProjection.RankBetween(only, null);

        Assert.True(string.CompareOrdinal(top, only.BoardRank) < 0);
        Assert.True(string.CompareOrdinal(only.BoardRank, bottom) < 0);
    }

    [Fact]
    public void SettlesDuplicateRanksRatherThanLeavingThemUndefined()
    {
        // A sync conflict can duplicate a rank. Ordering must still be
        // reproducible, or the board reshuffles itself between reloads.
        var status = Status(StatusType.Active);
        List<MightDoTask> tasks = [Task("a", status, "h"), Task("b", status, "h")];

        var first = BoardProjection.Column(tasks, status.Id).Select(t => t.Id);
        var again = BoardProjection.Column(Enumerable.Reverse(tasks), status.Id).Select(t => t.Id);

        Assert.Equal(first, again);
    }
}
