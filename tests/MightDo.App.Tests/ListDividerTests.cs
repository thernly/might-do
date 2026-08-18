using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MightDo.App.ViewModels;
using MightDo.App.Views;
using MightDo.Core.Storage;
using MightDo.Platform;

namespace MightDo.App.Tests;

/// <summary>
/// The hairlines between list rows.
/// </summary>
/// <remarks>
/// They are drawn by a style on the item rather than by anything in the row
/// template, which means two things a view-model test cannot see: that the
/// Fluent item template passes the border through to something that paints it,
/// and that the nth-last-child selector really does spare the final row.
/// </remarks>
public class ListDividerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mightdo-divider-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact]
    public async Task EveryRowButTheLastIsUnderlined()
    {
        var window = await OpenListAsync("First", "Second", "Third");

        var thicknesses = Rows(window)
            .Select(row => row.BorderThickness.Bottom)
            .ToList();

        Assert.Equal([1, 1, 0], thicknesses);
    }

    [AvaloniaFact]
    public async Task TheDividerReachesSomethingThatDrawsIt()
    {
        // The item template could ignore BorderThickness entirely and the
        // assertion above would still pass with nothing on screen.
        var window = await OpenListAsync("First", "Second");

        var presenter = Rows(window).First()
            .GetVisualDescendants().OfType<ContentPresenter>().First();

        Assert.Equal(1, presenter.BorderThickness.Bottom);
        Assert.NotNull(presenter.BorderBrush);
    }

    [AvaloniaFact]
    public async Task TheOnlyRowInAListIsNotUnderlined()
    {
        var window = await OpenListAsync("Alone");

        Assert.Equal(0, Assert.Single(Rows(window)).BorderThickness.Bottom);
    }

    private static List<ListBoxItem> Rows(Window window) =>
    [
        .. window.GetVisualDescendants().OfType<ListBox>()
            .First(list => list.Classes.Contains("rows"))
            .GetVisualDescendants().OfType<ListBoxItem>(),
    ];

    private async Task<Window> OpenListAsync(params string[] summaries)
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

        foreach (var summary in summaries)
        {
            workspace.NewTaskSummary = summary;
            await workspace.CreateTaskCommand.ExecuteAsync(null!);
            Dispatcher.UIThread.RunJobs();
        }

        workspace.ShowListCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        window.Measure(window.ClientSize);
        window.Arrange(new Rect(window.ClientSize));
        Dispatcher.UIThread.RunJobs();

        return window;
    }

    private sealed class NoPicker : IFolderPicker, IFilePicker
    {
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title) => Task.FromResult<string?>(null);
    }
}
