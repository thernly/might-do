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
/// <para>
/// A press on a card is ambiguous until the pointer either moves or comes back
/// up, so the two gestures are told apart by distance: past
/// <see cref="DragThreshold"/> it is a drag, and a release before that is a
/// click, which opens the card in the detail pane. Starting the drag on the
/// press itself, with no threshold, would make every click a zero-length drag
/// and leave no way to open a card at all.
/// </para>
/// </remarks>
public partial class BoardView : UserControl
{
    /// <summary>Our own format, so no other application's drag looks like ours.</summary>
    private static readonly DataFormat<string> TaskIdFormat =
        DataFormat.CreateStringApplicationFormat("might-do-task-id");

    /// <summary>How far the pointer must travel before a press becomes a drag.</summary>
    private const double DragThreshold = 4;

    private PointerPressedEventArgs? _press;
    private Point _pressedAt;
    private string? _pressedTaskId;

    public BoardView()
    {
        InitializeComponent();

        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, (_, _) => ForgetPress());
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ForgetPress();
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var taskId = TagOf(e.Source as Visual, "CardRoot");
        if (taskId is null) return;

        // Held rather than acted on: which gesture this is cannot be known yet.
        _press = e;
        _pressedAt = e.GetPosition(this);
        _pressedTaskId = taskId;
    }

    private async void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedTaskId is null || _press is null) return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ForgetPress();
            return;
        }

        var moved = e.GetPosition(this) - _pressedAt;
        if (Math.Abs(moved.X) < DragThreshold && Math.Abs(moved.Y) < DragThreshold) return;

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(TaskIdFormat, _pressedTaskId));

        // Cleared before the drag rather than after: the platform runs its own
        // loop until the drop, and the release that ends it is not ours to read
        // as a click.
        var press = _press;
        ForgetPress();

        try
        {
            await DragDrop.DoDragDropAsync(press, transfer, DragDropEffects.Move);
        }
        catch (Exception)
        {
            // A drag the platform refuses is not worth taking the app down for.
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var taskId = _pressedTaskId;
        ForgetPress();

        if (taskId is null || e.InitialPressMouseButton != MouseButton.Left) return;
        if (DataContext is not WorkspaceViewModel workspace) return;

        // A single click, matching the list: there is nothing else a click on a
        // card could mean, so making it a double-click would only be ceremony.
        workspace.SelectTaskById(taskId);
    }

    private void ForgetPress()
    {
        _press = null;
        _pressedTaskId = null;
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
