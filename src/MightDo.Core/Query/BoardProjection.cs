using MightDo.Core.Domain;

namespace MightDo.Core.Query;

/// <summary>
/// Arranges tasks into Kanban columns.
/// </summary>
/// <remarks>
/// Separate from <see cref="TaskQuery"/> because the board is a different view
/// of the same data, not a sort option: its order is the user's manual one, held
/// in <see cref="MightDoTask.BoardRank"/>, and no list sort uses that field.
/// <para>
/// Ranks are compared with <see cref="StringComparer.Ordinal"/>. Keeping every
/// board ordering behind this one type is what stops the default,
/// culture-sensitive comparer from being used by accident somewhere — which
/// would silently produce the wrong column order rather than an error.
/// </para>
/// </remarks>
public static class BoardProjection
{
    /// <summary>The tasks in one column, top to bottom.</summary>
    public static IReadOnlyList<MightDoTask> Column(
        IEnumerable<MightDoTask> tasks, string statusId)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        return
        [
            .. tasks
                .Where(task => task.StatusId == statusId)
                .OrderBy(task => task.BoardRank, StringComparer.Ordinal)
                // Ranks are unique in practice, but a sync conflict can
                // duplicate one. Settle it rather than leaving it undefined.
                .ThenBy(task => task.Id, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// The columns of the board, left to right, skipping statuses flagged off it.
    /// </summary>
    /// <remarks>
    /// The board shows tasks in <c>Final</c> statuses even though the list view
    /// hides them by default — a column headed "Done" with nothing in it is
    /// worse than useless. That is a decision about this view, which is why it
    /// lives here rather than in a query default.
    /// </remarks>
    public static IReadOnlyList<BoardColumn> Columns(
        IEnumerable<MightDoTask> tasks, WorkspaceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var byStatus = tasks.ToLookup(task => task.StatusId, StringComparer.Ordinal);

        return
        [
            .. config.BoardStatuses.Select(status => new BoardColumn(
                status,
                Column(byStatus[status.Id], status.Id))),
        ];
    }

    /// <summary>
    /// A rank placing a task at the bottom of <paramref name="column"/>.
    /// </summary>
    public static string RankForBottomOf(IReadOnlyList<MightDoTask> column) =>
        Rank.Between(column.Count == 0 ? "" : column[^1].BoardRank, "");

    /// <summary>
    /// A rank placing a task between two others, either of which may be absent
    /// at the ends of the column.
    /// </summary>
    public static string RankBetween(MightDoTask? above, MightDoTask? below) =>
        Rank.Between(above?.BoardRank ?? "", below?.BoardRank ?? "");

    /// <summary>
    /// The neighbours a dropped card lands between: above
    /// <paramref name="beforeTaskId"/>, or at the bottom of the column when that
    /// is null.
    /// </summary>
    /// <remarks>
    /// The dragged task is excluded from the column first, so its own current
    /// rank can never end up as one of its own neighbours — which would ask
    /// <see cref="Rank.Between"/> for a rank between a value and itself.
    /// <para>
    /// Returns null when the drop is a no-op or cannot be placed: dropping a
    /// card onto itself, or onto a card that has since moved. The caller should
    /// do nothing rather than guess.
    /// </para>
    /// </remarks>
    public static (MightDoTask? Above, MightDoTask? Below)? DropTarget(
        IEnumerable<MightDoTask> tasks,
        string statusId,
        string taskId,
        string? beforeTaskId)
    {
        if (taskId == beforeTaskId) return null;

        var column = Column(tasks, statusId)
            .Where(candidate => candidate.Id != taskId)
            .ToList();

        if (beforeTaskId is null)
        {
            return (column.Count == 0 ? null : column[^1], null);
        }

        var index = column.FindIndex(candidate => candidate.Id == beforeTaskId);
        if (index < 0) return null;

        return (index == 0 ? null : column[index - 1], column[index]);
    }
}

public sealed record BoardColumn(Status Status, IReadOnlyList<MightDoTask> Tasks);
