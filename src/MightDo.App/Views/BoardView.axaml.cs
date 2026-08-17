using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MightDo.App.ViewModels;

namespace MightDo.App.Views;

/// <summary>
/// The Kanban board, and the drag-and-drop that makes it a board rather than a
/// set of lists.
/// </summary>
/// <remarks>
/// Drop targets are deliberately simple and predictable: dropping on a card
/// inserts <i>above</i> that card, and dropping anywhere else in a column
/// appends to the bottom. Guessing an insertion point from how far down a gap
/// the pointer happens to be reads as unpredictable when the target is a
/// six-pixel margin.
/// <para>
/// Moving a card is a status change, so it goes through
/// <see cref="WorkspaceViewModel.MoveOnBoardAsync"/> and therefore the session —
/// which means dropping into a Final column stamps the completion date, exactly
/// as changing the status in the detail pane does. Only one rank is rewritten,
/// so a reorder touches one task file rather than renumbering the column, which
/// is the whole point of the fractional index.
/// </para>
/// </remarks>
public partial class BoardView : UserControl
{
    /// <summary>Our own format, so no other application's drag looks like ours.</summary>
    private static readonly DataFormat<string> TaskIdFormat =
        DataFormat.CreateStringApplicationFormat("might-do-task-id");

    public BoardView()
    {
        InitializeComponent();

        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);
    }

    private async void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var taskId = TagOf(e.Source as Visual, "CardRoot");
        if (taskId is null) return;

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(TaskIdFormat, taskId));

        try
        {
            await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move);
        }
        catch (Exception)
        {
            // A drag the platform refuses is not worth taking the app down for.
        }
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(TaskIdFormat)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        if (e.DataTransfer.TryGetValue(TaskIdFormat) is not { } taskId) return;
        if (DataContext is not WorkspaceViewModel workspace) return;

        var source = e.Source as Visual;
        var statusId = TagOf(source, "ColumnRoot");
        if (statusId is null) return;

        await workspace.MoveOnBoardAsync(taskId, statusId, TagOf(source, "CardRoot"));
    }

    /// <summary>
    /// Walks up from <paramref name="from"/> to a named element and returns its
    /// tag, which is where the board keeps the id each element stands for.
    /// </summary>
    private static string? TagOf(Visual? from, string name)
    {
        for (var visual = from; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Control control && control.Name == name) return control.Tag as string;
        }

        return null;
    }
}
