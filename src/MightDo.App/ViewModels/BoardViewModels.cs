using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using MightDo.Core.Domain;

namespace MightDo.App.ViewModels;

/// <summary>One Kanban column: a Status, and the tasks sitting in it.</summary>
public sealed class BoardColumnViewModel(Status status, IEnumerable<BoardCardViewModel> cards)
{
    public string StatusId { get; } = status.Id;

    public string Name { get; } = status.Name;

    // Style-class hooks for the status-type dot in the column header.
    public bool IsInitialStatus { get; } = status.Type == StatusType.Initial;

    public bool IsActiveStatus { get; } = status.Type == StatusType.Active;

    public bool IsFinalStatus { get; } = status.Type == StatusType.Final;

    public ObservableCollection<BoardCardViewModel> Cards { get; } = new(cards);

    public string CountLabel => Cards.Count == 0 ? "" : Cards.Count.ToString();

    public bool IsEmpty => Cards.Count == 0;
}

/// <summary>One card on the board.</summary>
public sealed partial class BoardCardViewModel(MightDoTask task, WorkspaceConfig config)
    : ObservableObject
{
    /// <summary>
    /// Whether this is the card the detail pane is showing. The board has no
    /// selection of its own — a card is selected exactly when the workspace's
    /// selected id is this one — so the workspace sets it.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    public string Id { get; } = task.Id;

    public string StatusId { get; } = task.StatusId;

    public string Summary { get; } = task.Summary;

    public string PriorityLabel { get; } = task.Priority.Label();

    public bool IsLowPriority { get; } = task.Priority == Priority.Low;

    public bool IsMediumPriority { get; } = task.Priority == Priority.Medium;

    public bool IsHighPriority { get; } = task.Priority == Priority.High;

    public bool IsCriticalPriority { get; } = task.Priority == Priority.Critical;

    /// <summary>
    /// The one date the card carries: when it is due, or — once the task has
    /// landed in a Final status — when it was completed. A finished card's due
    /// date is history, and the date the reader wants is the one it finished on.
    /// </summary>
    public string DateLabel { get; } = task.CompletedAt is { } completed
        ? $"Completed {completed.ToLocalTime():yyyy-MM-dd}"
        : task.DueDate?.ToIso() ?? "";

    public bool HasDate { get; } = task.CompletedAt is not null || task.DueDate is not null;

    public bool IsOverdue { get; } = task.IsOverdue;

    public string? CategoryName { get; } = config.CategoryById(task.CategoryId)?.Name;

    /// <summary>The category's stored colour, shown as a dot in its chip.</summary>
    public IBrush CategoryBrush { get; } =
        new ImmutableSolidColorBrush(config.CategoryById(task.CategoryId)?.Color ?? 0);

    public bool HasCategory { get; } = task.CategoryId is not null;

    public string StepsLabel { get; } =
        task.Steps.Count == 0 ? "" : $"{task.StepsDone}/{task.Steps.Count}";

    public bool HasSteps { get; } = task.Steps.Count > 0;
}
