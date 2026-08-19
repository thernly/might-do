using Avalonia;
using Avalonia.Controls;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MightDo.App.ViewModels;
using MightDo.Core.Domain;
using MightDo.Core.Query;
using MightDo.Core.Storage;
using MightDo.App.Views;
using MightDo.Platform;

namespace MightDo.App.Tests;

/// <summary>
/// The colour semantics: overdue dates, priority tints, status-type dots.
/// </summary>
/// <remarks>
/// The colours themselves come from styles keyed on classes, and the classes
/// are attached with <c>Classes.x</c> bindings — which fail silently when a
/// property is renamed. These check the classes land on the elements, which is
/// the part a compiled binding cannot.
/// </remarks>
public class SemanticColorTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "mightdo-color-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact]
    public async Task AnOverdueDueDateIsMarkedInTheList()
    {
        var yesterday = DateTime.Today.AddDays(-1);
        var (window, _) = await OpenListAsync(("Late", Priority.Medium, yesterday));

        var due = DueLabel(window, yesterday);
        Assert.Contains("overdue", due.Classes);
    }

    [AvaloniaFact]
    public async Task AFutureDueDateStaysQuiet()
    {
        var tomorrow = DateTime.Today.AddDays(1);
        var (window, _) = await OpenListAsync(("On time", Priority.Medium, tomorrow));

        var due = DueLabel(window, tomorrow);
        Assert.DoesNotContain("overdue", due.Classes);
    }

    [AvaloniaFact]
    public async Task PriorityChipsCarryTheirPriority()
    {
        var (window, _) = await OpenListAsync(
            ("Urgent", Priority.Critical, null),
            ("Soon", Priority.High, null),
            ("Whenever", Priority.Medium, null));

        Assert.Contains("critical", ChipFor(window, "Critical").Classes);
        Assert.Contains("high", ChipFor(window, "High").Classes);

        var medium = ChipFor(window, "Medium");
        Assert.DoesNotContain("critical", medium.Classes);
        Assert.DoesNotContain("high", medium.Classes);
        Assert.DoesNotContain("low", medium.Classes);
    }

    [AvaloniaFact]
    public async Task ColumnHeadersCarryTheirStatusTypeDot()
    {
        // The seed config puts one visible column of each type on the board,
        // so all three dot colours should be present.
        var (window, workspace) = await OpenListAsync(("Any", Priority.Medium, null));

        workspace.ShowBoardCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        window.Measure(window.ClientSize);
        window.Arrange(new Rect(window.ClientSize));

        var dots = window.GetVisualDescendants().OfType<Ellipse>()
            .Where(dot => dot.Classes.Contains("statusdot"))
            .ToList();

        Assert.Contains(dots, dot => dot.Classes.Contains("initial"));
        Assert.Contains(dots, dot => dot.Classes.Contains("active"));
        Assert.Contains(dots, dot => dot.Classes.Contains("final"));
    }

    /// <summary>The due-date label of the row showing this date.</summary>
    private static TextBlock DueLabel(Window window, DateTime date) =>
        window.GetVisualDescendants().OfType<TextBlock>()
            .First(block => block.Classes.Contains("due")
                && block.Text == new CalendarDate(date.Year, date.Month, date.Day).ToIso());

    /// <summary>The chip Border holding this priority label.</summary>
    private static Border ChipFor(Window window, string label) =>
        window.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("chip"))
            .First(border => border.GetVisualDescendants().OfType<TextBlock>()
                .Any(block => block.Text == label));

    /// <summary>A window on the list view, with these tasks already on disk.</summary>
    private async Task<(Window Window, WorkspaceViewModel Workspace)> OpenListAsync(
        params (string Summary, Priority Priority, DateTime? Due)[] tasks)
    {
        var store = new TaskStore(new Core.Storage.Workspace(
            Directory.CreateDirectory(Path.Combine(_root, "ws")).FullName));
        var config = await store.InitialiseAsync();

        foreach (var (summary, priority, due) in tasks)
        {
            await store.SaveTaskAsync(MightDoTask.Create(
                summary: summary,
                statusId: config.DefaultStatusId,
                boardRank: BoardProjection.RankForBottomOf([]),
                priority: priority,
                dueDate: due is { } d ? new CalendarDate(d.Year, d.Month, d.Day) : null));
        }

        var settings = AppSettings.Load(Path.Combine(_root, "settings.json"));
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
        window.Measure(window.ClientSize);
        window.Arrange(new Rect(window.ClientSize));

        return (window, workspace);
    }

    private sealed class NoPicker : IFolderPicker, IFilePicker
    {
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title) => Task.FromResult<string?>(null);
    }
}
