namespace MightDo.Core.Query;

/// <summary>How the list view is sorted. The board has its own manual order.</summary>
public enum TaskSort
{
    /// <summary>Overdue first, then priority, then soonest due, then oldest.</summary>
    Smart,

    DueDate,
    Priority,
    Summary,
    Created,
    Updated,
}

public static class TaskSortExtensions
{
    public static string Label(this TaskSort sort) => sort switch
    {
        TaskSort.Smart => "Priority & due date",
        TaskSort.DueDate => "Due date",
        TaskSort.Priority => "Priority",
        TaskSort.Summary => "Summary",
        TaskSort.Created => "Recently created",
        TaskSort.Updated => "Recently updated",
        _ => throw new ArgumentOutOfRangeException(nameof(sort)),
    };
}
