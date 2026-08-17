using System.Collections.ObjectModel;
using MightDo.Core.Domain;

namespace MightDo.App.ViewModels;

/// <summary>One Kanban column: a Status, and the tasks sitting in it.</summary>
public sealed class BoardColumnViewModel(Status status, IEnumerable<BoardCardViewModel> cards)
{
    public string StatusId { get; } = status.Id;

    public string Name { get; } = status.Name;

    public ObservableCollection<BoardCardViewModel> Cards { get; } = new(cards);

    public string CountLabel => Cards.Count == 0 ? "" : Cards.Count.ToString();

    public bool IsEmpty => Cards.Count == 0;
}

/// <summary>One card on the board.</summary>
public sealed class BoardCardViewModel(MightDoTask task, WorkspaceConfig config)
{
    public string Id { get; } = task.Id;

    public string StatusId { get; } = task.StatusId;

    public string Summary { get; } = task.Summary;

    public string PriorityLabel { get; } = task.Priority.Label();

    public string DueLabel { get; } = task.DueDate?.ToIso() ?? "";

    public bool HasDue { get; } = task.DueDate is not null;

    public bool IsOverdue { get; } = task.IsOverdue;

    public string? CategoryName { get; } = config.CategoryById(task.CategoryId)?.Name;

    public bool HasCategory { get; } = task.CategoryId is not null;

    public string StepsLabel { get; } =
        task.Steps.Count == 0 ? "" : $"{task.StepsDone}/{task.Steps.Count}";

    public bool HasSteps { get; } = task.Steps.Count > 0;
}
