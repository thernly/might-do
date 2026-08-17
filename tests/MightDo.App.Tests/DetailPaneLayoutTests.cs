using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MightDo.App.ViewModels;
using MightDo.App.Views;
using MightDo.Core.Storage;
using MightDo.Platform;

namespace MightDo.App.Tests;

/// <summary>
/// The detail pane fits inside the detail pane.
/// </summary>
/// <remarks>
/// Some Fluent controls carry a minimum width the theme will not let go of —
/// <c>DatePicker</c> is 296px and <c>TimePicker</c> 242px, both wider than a
/// column of this 400px pane. A control past its minimum does not shrink and
/// does not wrap: it is arranged at its minimum and drawn over whatever is
/// beside and below it, off the edge of the pane. Nothing about that shows up
/// as an exception, a binding error or a failed assertion about content, which
/// is how it shipped. Measuring is the only thing that catches it.
/// </remarks>
public class DetailPaneLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mightdo-layout-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact]
    public async Task NothingInThePaneIsWiderThanThePane()
    {
        var (pane, _) = await OpenPaneAsync();

        var overflowing = Overflowing(pane);

        Assert.True(
            overflowing.Count == 0,
            "these are drawn past the right edge of the detail pane:\n"
            + string.Join("\n", overflowing));
    }

    [AvaloniaFact]
    public async Task TheDueDateFieldFitsItsColumn()
    {
        // The field that prompted all of this. Named on its own so a regression
        // says which control went back to a fixed minimum, not merely that
        // something did.
        var (pane, _) = await OpenPaneAsync();

        var due = pane.GetVisualDescendants()
            .OfType<CalendarDatePicker>()
            .First(picker => picker.PlaceholderText == "yyyy-mm-dd");

        // Half the pane, less the gutter between the two columns.
        Assert.True(
            due.Bounds.Width <= pane.Bounds.Width / 2,
            $"the due date field is {due.Bounds.Width:F0}px wide in a "
            + $"{pane.Bounds.Width / 2:F0}px column");
    }

    [AvaloniaFact]
    public async Task ThePaneStillFitsWithAllItsSectionsPopulated()
    {
        // Empty collections hide the widest rows. A reminder in particular
        // brings a whole row of controls that is not there otherwise.
        var (pane, detail) = await OpenPaneAsync();

        detail.DueDate = new DateTime(2026, 9, 1);
        detail.NewReminderDate = new DateTime(2026, 9, 1);
        detail.NewReminderTime = new TimeSpan(14, 30, 0);
        await detail.AddReminderCommand.ExecuteAsync(null!);

        detail.NewStepText = "A step whose text is long enough to need wrapping in the pane";
        await detail.AddStepCommand.ExecuteAsync(null!);
        detail.NewNoteBody = "A note, also long enough that it has to wrap rather than run on";
        await detail.AddNoteCommand.ExecuteAsync(null!);
        detail.TagNames = "one, two, three, four, five, six";
        await detail.CommitTagsCommand.ExecuteAsync(null!);

        Dispatcher.UIThread.RunJobs();
        Relayout(pane);

        var overflowing = Overflowing(pane);

        Assert.True(
            overflowing.Count == 0,
            "these are drawn past the right edge of the detail pane:\n"
            + string.Join("\n", overflowing));
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Descendants drawn past the pane's right edge, described well enough to
    /// act on.
    /// </summary>
    private static List<string> Overflowing(TaskDetailView pane) =>
        [.. pane.GetVisualDescendants()
            .OfType<Control>()
            .Where(Measurable)
            .Select(control => (Control: control, Left: control.TranslatePoint(default, pane)))
            .Where(item => item.Left is { } left
                           && left.X + item.Control.Bounds.Width > pane.Bounds.Width + 0.5)
            .Select(item => $"{item.Control.GetType().Name} at x={item.Left!.Value.X:F0} "
                            + $"is {item.Control.Bounds.Width:F0}px wide, "
                            + $"ending {item.Left.Value.X + item.Control.Bounds.Width:F0}px "
                            + $"into a {pane.Bounds.Width:F0}px pane")];

    /// <summary>
    /// Whether a control's bounds mean what they look like they mean.
    /// </summary>
    /// <remarks>
    /// A <c>Viewbox</c> arranges its child at natural size and then scales it,
    /// so the child's bounds are pre-transform and say nothing about the pixels.
    /// A <c>Popup</c>'s contents are not in the pane at all. A scrollbar's own
    /// parts sit deliberately at the edge.
    /// </remarks>
    private static bool Measurable(Control control) =>
        control.GetVisualAncestors().All(a => a is not (Viewbox or ScrollBar))
        && control.GetSelfAndVisualAncestors().OfType<Control>()
            .All(a => a.Parent is not Popup);

    private async Task<(TaskDetailView Pane, TaskDetailViewModel Detail)> OpenPaneAsync()
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

        workspace.NewTaskSummary = "Laid out";
        await workspace.CreateTaskCommand.ExecuteAsync(null!);
        Dispatcher.UIThread.RunJobs();
        workspace.SelectTaskById(workspace.Tasks.Single().Id);
        Dispatcher.UIThread.RunJobs();

        Relayout(window);

        var pane = window.GetVisualDescendants().OfType<TaskDetailView>().Single();
        Assert.True(pane.Bounds.Width > 0, "the pane was never laid out");

        return (pane, workspace.Detail!);
    }

    private static void Relayout(Visual visual)
    {
        var window = visual.GetSelfAndVisualAncestors().OfType<Window>().Single();
        window.Measure(window.ClientSize);
        window.Arrange(new Rect(window.ClientSize));
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class NoPicker : IFolderPicker, IFilePicker
    {
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title) => Task.FromResult<string?>(null);
    }
}
