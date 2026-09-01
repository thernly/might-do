using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MightDo.App.ViewModels;
using MightDo.App.Views;
using MightDo.Core.Domain;
using MightDo.Core.Query;
using MightDo.Core.Storage;
using MightDo.Platform;

namespace MightDo.App.Tests;

/// <summary>
/// The single date a list row carries, which is the due date until the task
/// finishes and the completion date afterwards — the same rule as the board's
/// cards.
/// </summary>
public class ListRowDateTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "mightdo-rowdate-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact]
    public async Task AnUnfinishedRowShowsItsDueDate()
    {
        var due = DateTime.Today.AddDays(3);
        var (window, _) = await OpenListAsync(("Due soon", due));

        Assert.Equal(due.ToString("yyyy-MM-dd"), RowDate(window, "Due soon"));
    }

    [AvaloniaFact]
    public async Task AFinishedRowShowsWhenItWasCompletedRatherThanWhenItWasDue()
    {
        var due = DateTime.Today.AddDays(-3);
        var (window, workspace) = await OpenListAsync(("Shipped late", due));

        var id = workspace.Tasks.First(row => row.Summary == "Shipped late").Id;
        var done = workspace.Statuses.First(status => status.Name == "Done");
        await workspace.MoveOnBoardAsync(id, done.Id, null);

        // Completed work leaves the working set, so the row is only there to
        // look at once the filter asks for it.
        workspace.IncludeCompleted = true;
        Layout(window);

        Assert.Equal(
            $"Completed {DateTime.Now:yyyy-MM-dd}", RowDate(window, "Shipped late"));
        Assert.False(workspace.Tasks.First(row => row.Summary == "Shipped late").IsOverdue);
    }

    /// <summary>The date actually drawn on the row holding this summary.</summary>
    private static string RowDate(Window window, string summary) =>
        window.GetVisualDescendants().OfType<ListBoxItem>()
            .First(row => row.GetVisualDescendants().OfType<TextBlock>()
                .Any(block => block.Text == summary))
            .GetVisualDescendants().OfType<TextBlock>()
            .First(block => block.Classes.Contains("due")).Text ?? "";

    private async Task<(Window Window, WorkspaceViewModel Workspace)> OpenListAsync(
        params (string Summary, DateTime Due)[] tasks)
    {
        var store = new TaskStore(new Core.Storage.Workspace(
            Directory.CreateDirectory(Path.Combine(_root, "ws")).FullName));
        var config = await store.InitialiseAsync();

        foreach (var (summary, due) in tasks)
        {
            await store.SaveTaskAsync(MightDoTask.Create(
                summary: summary,
                statusId: config.DefaultStatusId,
                boardRank: BoardProjection.RankForBottomOf([]),
                dueDate: new CalendarDate(due.Year, due.Month, due.Day)));
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
        workspace.ShowListCommand.Execute(null);
        Layout(window);

        return (window, workspace);
    }

    private static void Layout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
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
