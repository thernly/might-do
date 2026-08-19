using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MightDo.App.ViewModels;
using MightDo.App.Views;
using MightDo.Core.Storage;
using MightDo.Platform;

namespace MightDo.App.Tests;

/// <summary>
/// Clicking on the board, driven through real pointer events.
/// </summary>
/// <remarks>
/// The view models could be exercised directly, but the bug these were written
/// for lived entirely in the view: every press started a drag, so a click on a
/// card was a zero-length drag and nothing opened. Only the pointer events find
/// that.
/// </remarks>
public class BoardInteractionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mightdo-board-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    // ---- the click ---------------------------------------------------------

    [AvaloniaFact]
    public async Task ClickingACardOpensItInTheDetailPane()
    {
        var (window, workspace) = await OpenBoardAsync(("Click me", null));

        Assert.False(workspace.HasSelection);

        ClickCard(window, "Click me");

        Assert.True(workspace.HasSelection);
        Assert.Equal("Click me", workspace.Detail!.Summary);
        Assert.Single(Descendants<TaskDetailView>(window));
    }

    [AvaloniaFact]
    public async Task ClickingACompletedCardOpensItThoughTheListHidesIt()
    {
        // The board shows Final columns populated where the list does not, so
        // its selection cannot be a row of the list.
        var (window, workspace) = await OpenBoardAsync(("Shipped", "Done"));

        // The list really does hide it: switching there leaves no row at all.
        workspace.ShowListCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(workspace.Tasks);

        workspace.ShowBoardCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        ClickCard(window, "Shipped");

        Assert.True(workspace.HasSelection);
        Assert.Equal("Shipped", workspace.Detail!.Summary);
        Assert.Null(workspace.SelectedTask);
    }

    [AvaloniaFact]
    public async Task TheClickedCardIsMarkedOnTheBoard()
    {
        var (window, workspace) = await OpenBoardAsync(("First", null), ("Second", null));

        ClickCard(window, "Second");

        var cards = workspace.Columns.SelectMany(column => column.Cards).ToList();
        Assert.Equal("Second", Assert.Single(cards, c => c.IsSelected).Summary);

        ClickCard(window, "First");

        cards = [.. workspace.Columns.SelectMany(column => column.Cards)];
        Assert.Equal("First", Assert.Single(cards, c => c.IsSelected).Summary);
    }

    [AvaloniaFact]
    public async Task AClickIsAReleaseWithoutMovement()
    {
        // The threshold that separates the two gestures: a press followed by a
        // real drag distance is not a click, whatever happens to the drag.
        var (window, workspace) = await OpenBoardAsync(("Drag me", null));

        var card = CardFor(window, "Drag me");
        var origin = PointIn(window, card);

        window.MouseDown(origin, MouseButton.Left);
        window.MouseMove(origin + new Point(40, 0));
        window.MouseUp(origin + new Point(40, 0), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.False(workspace.HasSelection);
    }

    // ---- what the pane does afterwards -------------------------------------

    [AvaloniaFact]
    public async Task ThePaneSurvivesARescan()
    {
        var (window, workspace) = await OpenBoardAsync(("Keep me open", null));

        ClickCard(window, "Keep me open");
        await workspace.RefreshCommand.ExecuteAsync(null!);
        Dispatcher.UIThread.RunJobs();

        Assert.True(workspace.HasSelection);
        Assert.Equal("Keep me open", workspace.Detail!.Summary);
    }

    [AvaloniaFact]
    public async Task ThePaneStaysOpenWhenAFilterHidesTheTask()
    {
        // A filter narrows a view. It does not answer the question the pane is
        // open to answer, so it should not close it under the user.
        var (window, workspace) = await OpenBoardAsync(("Filtered away", null));

        ClickCard(window, "Filtered away");
        workspace.Search = "matches nothing at all";
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(workspace.Tasks);
        Assert.True(workspace.HasSelection);
    }

    [AvaloniaFact]
    public async Task TheListSaysWhyItHasNothingSelected()
    {
        // Staying open leaves a pane with an empty list behind it and no reason
        // given. The board explains itself by marking the card; the list cannot.
        var (window, workspace) = await OpenBoardAsync(("Filtered away", null));

        ClickCard(window, "Filtered away");
        workspace.Search = "matches nothing at all";
        workspace.ShowListCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(workspace.SelectedTask);
        Assert.True(workspace.SelectionHiddenFromList);
    }

    [AvaloniaFact]
    public async Task TheListSaysNothingWhileTheTaskIsInIt()
    {
        var (window, workspace) = await OpenBoardAsync(("Right there", null));

        ClickCard(window, "Right there");
        workspace.ShowListCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(workspace.SelectedTask);
        Assert.False(workspace.SelectionHiddenFromList);
    }

    [AvaloniaFact]
    public async Task TheExplanationIsActuallyDrawn()
    {
        // The assertions above are on the view model, which a mistyped binding
        // would sail past. IsEffectivelyVisible rather than IsVisible: an
        // element hidden by a binding is still in the tree.
        var (window, workspace) = await OpenBoardAsync(("Filtered away", null));

        ClickCard(window, "Filtered away");
        workspace.Search = "matches nothing at all";
        workspace.ShowListCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        window.Measure(window.ClientSize);
        window.Arrange(new Rect(window.ClientSize));

        Assert.Contains(
            Descendants<TextBlock>(window),
            block => block.Text?.Contains("the current filter hides it") == true);
    }

    [AvaloniaFact]
    public async Task TheBoardDoesNotBorrowTheListsExplanation()
    {
        // The board shows completed and filtered-out work regardless, so the
        // hint would be answering a question nobody asked.
        var (window, workspace) = await OpenBoardAsync(("Filtered away", null));

        ClickCard(window, "Filtered away");
        workspace.Search = "matches nothing at all";
        Dispatcher.UIThread.RunJobs();

        Assert.True(workspace.IsBoardView);
        Assert.Null(workspace.SelectedTask);
        Assert.False(workspace.SelectionHiddenFromList);
    }

    [AvaloniaFact]
    public async Task ThePaneClosesWhenTheTaskLeavesTheWorkspace()
    {
        var (window, workspace) = await OpenBoardAsync(("Bin me", null));

        ClickCard(window, "Bin me");
        Assert.True(workspace.HasSelection);

        // Trashed from the list, which is where a row exists to trash — the
        // point being that the pane closes for a task that has left the
        // workspace, whichever view sent it there.
        workspace.ShowListCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        await workspace.TrashTaskCommand.ExecuteAsync(workspace.Tasks.Single());
        Dispatcher.UIThread.RunJobs();

        Assert.False(workspace.HasSelection);
        Assert.Null(workspace.Detail);
    }

    [AvaloniaFact]
    public async Task TheOpenTaskCanBeTrashedFromThePane()
    {
        var (window, workspace) = await OpenBoardAsync(("Bin me too", null));

        ClickCard(window, "Bin me too");

        // The button must actually be drawn in the pane, not just the command
        // exist — an unbound command is exactly the gap this closes.
        Assert.Contains(
            Descendants<TextBlock>(window),
            block => block.Text == "Move to Trash");

        await workspace.TrashOpenTaskCommand.ExecuteAsync(null!);
        Dispatcher.UIThread.RunJobs();

        Assert.False(workspace.HasSelection);
        Assert.Empty(workspace.Columns.SelectMany(column => column.Cards));
    }

    [AvaloniaFact]
    public async Task ATaskTheListHidesCanStillBeTrashed()
    {
        // The pane can show a task no list row holds, so the command works
        // from the pane's task, not the list's selection.
        var (window, workspace) = await OpenBoardAsync(("Shipped and done", "Done"));

        ClickCard(window, "Shipped and done");
        Assert.Null(workspace.SelectedTask);

        await workspace.TrashOpenTaskCommand.ExecuteAsync(null!);
        Dispatcher.UIThread.RunJobs();

        Assert.False(workspace.HasSelection);
        Assert.Empty(workspace.Columns.SelectMany(column => column.Cards));
    }

    [AvaloniaFact]
    public async Task ClosingThePaneClearsTheCardMarking()
    {
        var (window, workspace) = await OpenBoardAsync(("Close me", null));

        ClickCard(window, "Close me");
        workspace.CloseDetailCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(workspace.HasSelection);
        Assert.DoesNotContain(
            workspace.Columns.SelectMany(column => column.Cards), card => card.IsSelected);
    }

    [AvaloniaFact]
    public async Task TheSelectedCardIsOutlinedInTheAccent()
    {
        // The marking is a BorderBrush, and a BorderBrush painted from a
        // <Color> resource resolves to nothing rather than failing — the card
        // just quietly stops being outlined. Checking IsSelected would not
        // catch that; checking the brush arrived does.
        var (window, _) = await OpenBoardAsync(("Mark me", null));

        ClickCard(window, "Mark me");
        window.Measure(window.ClientSize);
        window.Arrange(new Rect(window.ClientSize));

        Application.Current!.TryGetResource(
            "AppAccentBrush", Application.Current.ActualThemeVariant, out var accent);

        Assert.Same(accent, CardFor(window, "Mark me").BorderBrush);
    }

    // ---- dragging across columns -------------------------------------------

    [AvaloniaFact]
    public async Task ACardDroppedOntoTwoCardsSharingARankStillChangesColumn()
    {
        // How two cards come to share a rank: a status change from the detail
        // pane carries the card's rank into the new column, and the first card
        // of every column is given the same first rank. Dropping onto the
        // second of the pair then asked for a rank between a value and itself,
        // which threw out of the drop handler — the card simply stayed put.
        var (window, workspace) = await OpenBoardAsync(
            ("Already there", "In Progress"), ("Arriving by pane", null), ("Dragged", null));

        var target = workspace.Statuses.First(status => status.Name == "In Progress");

        ClickCard(window, "Arriving by pane");
        workspace.Detail!.SelectedStatus =
            workspace.Detail.Statuses.First(status => status.Id == target.Id);
        await workspace.Detail.PendingSave;
        Dispatcher.UIThread.RunJobs();

        var column = workspace.Columns.First(c => c.StatusId == target.Id);
        Assert.Equal(2, column.Cards.Count);

        var dragged = workspace.Columns.SelectMany(c => c.Cards)
            .First(card => card.Summary == "Dragged");

        await workspace.MoveOnBoardAsync(dragged.Id, target.Id, column.Cards[1].Id);
        Dispatcher.UIThread.RunJobs();

        var moved = workspace.Columns.First(c => c.StatusId == target.Id).Cards;
        Assert.Equal(3, moved.Count);
        Assert.Contains(moved, card => card.Summary == "Dragged");
    }

    // ---- an edit still in a field when the next card is clicked -------------

    [AvaloniaFact]
    public async Task ClickingAnotherCardCommitsTheDescriptionBeingTyped()
    {
        // A card is a Border, which does not take focus, so a click on one used
        // to leave the description box focused and its LostFocus binding
        // uncommitted — the edit was dropped on the floor. The list has never
        // had the problem: its rows take focus.
        var (window, workspace) = await OpenBoardAsync(("First", null), ("Second", null));

        ClickCard(window, "First");
        var first = workspace.Detail!;

        TypeDescription(window, "typed but not tabbed out of");
        ClickCard(window, "Second");
        await first.PendingSave;
        Dispatcher.UIThread.RunJobs();

        ClickCard(window, "First");

        Assert.Equal("First", workspace.Detail!.Summary);
        Assert.Equal("typed but not tabbed out of", workspace.Detail.Description);
    }

    [AvaloniaFact]
    public async Task TheTypedDescriptionDoesNotFollowTheClickToTheNextCard()
    {
        // The worse half of the same bug: the box kept its text, so the edit
        // read as belonging to the card just opened and would land on it.
        var (window, workspace) = await OpenBoardAsync(("First", null), ("Second", null));

        ClickCard(window, "First");
        TypeDescription(window, "belongs to First");
        ClickCard(window, "Second");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Second", workspace.Detail!.Summary);
        Assert.Equal("", workspace.Detail.Description);
        Assert.Equal("", DescriptionBox(window).Text ?? "");
    }

    /// <summary>Types into the pane's description box, leaving it focused.</summary>
    private static void TypeDescription(Window window, string text)
    {
        var box = DescriptionBox(window);
        box.Focus();
        Dispatcher.UIThread.RunJobs();
        box.Text = text;
        Dispatcher.UIThread.RunJobs();

        Assert.True(box.IsFocused);
    }

    private static TextBox DescriptionBox(Window window) =>
        Descendants<TextBox>(window).First(box => box.Name == "DescriptionBox");

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// A window showing the board, with the given tasks created and moved into
    /// the named statuses.
    /// </summary>
    private async Task<(Window Window, WorkspaceViewModel Workspace)> OpenBoardAsync(
        params (string Summary, string? StatusName)[] tasks)
    {
        var settings = AppSettings.Load(Path.Combine(_root, "settings.json"));
        var store = new TaskStore(new Core.Storage.Workspace(Path.Combine(_root, "ws")));
        var workspace = await WorkspaceViewModel.OpenAsync(store, settings, new NoPicker());
        _disposables.Add(workspace);

        var window = new MainWindow
        {
            DataContext = new MainViewModel(settings, new NoPicker(), new NoPicker())
            {
                Workspace = workspace,
            },
        };
        window.Show();

        // The board goes up before the tasks are made: only the view on screen
        // is projected, so the cards this reads back exist only once it is the
        // one showing.
        workspace.ShowBoardCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        foreach (var (summary, statusName) in tasks)
        {
            workspace.NewTaskSummary = summary;
            await workspace.CreateTaskCommand.ExecuteAsync(null!);
            Dispatcher.UIThread.RunJobs();

            if (statusName is null) continue;

            var id = workspace.Columns.SelectMany(column => column.Cards)
                .First(card => card.Summary == summary).Id;
            var status = workspace.Statuses.First(s => s.Name == statusName);
            await workspace.MoveOnBoardAsync(id, status.Id, null);
            Dispatcher.UIThread.RunJobs();
        }

        window.Measure(window.ClientSize);
        window.Arrange(new Rect(window.ClientSize));

        return (window, workspace);
    }

    private static void ClickCard(Window window, string summary)
    {
        var point = PointIn(window, CardFor(window, summary));

        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The card Border whose template put the task's summary in it.</summary>
    private static Border CardFor(Window window, string summary) =>
        Descendants<Border>(window)
            .Where(border => border.Name == "CardRoot")
            .First(border => Descendants<TextBlock>(border).Any(t => t.Text == summary));

    /// <summary>A point inside a control, in the window's coordinates.</summary>
    private static Point PointIn(Window window, Visual visual) =>
        visual.TranslatePoint(new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), window)
        ?? throw new InvalidOperationException("the control is not in the window's tree");

    private static List<T> Descendants<T>(Visual root) where T : Visual =>
        [.. root.GetVisualDescendants().OfType<T>().Where(v => v.IsEffectivelyVisible)];

    private sealed class NoPicker : IFolderPicker, IFilePicker
    {
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title) => Task.FromResult<string?>(null);
    }
}
