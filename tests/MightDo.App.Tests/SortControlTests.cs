using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MightDo.App.ViewModels;
using MightDo.App.Views;
using MightDo.Core.Query;
using MightDo.Core.Storage;
using MightDo.Platform;

namespace MightDo.App.Tests;

/// <summary>
/// The sort drop-down in the toolbar.
/// </summary>
/// <remarks>
/// It sits above both views but only orders the list: a board column is ordered
/// by <c>BoardRank</c>, which drag-and-drop writes, so a computed sort and a
/// manual one cannot both own that axis. It is therefore disabled rather than
/// obeyed on the board, and these check that it says so instead of looking live.
/// </remarks>
public class SortControlTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "mightdo-sort-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact]
    public async Task TheDropDownIsLiveInListView()
    {
        var (window, workspace) = await OpenAsync();

        workspace.ShowListCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(SortBox(window).IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public async Task TheDropDownIsDisabledOnTheBoard()
    {
        var (window, workspace) = await OpenAsync();

        workspace.ShowBoardCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // IsEffectivelyEnabled, not IsEnabled: the ComboBox is disabled through
        // its parent, so its own IsEnabled stays true.
        Assert.False(SortBox(window).IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public async Task TheDropDownStaysOnScreenOnTheBoard()
    {
        // Disabled, not hidden — hiding it would reshuffle the toolbar on every
        // view switch, which is the reason this was chosen over IsVisible.
        var (window, workspace) = await OpenAsync();

        workspace.ShowBoardCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(SortBox(window).IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task TheDropDownIsLabelled()
    {
        var (window, _) = await OpenAsync();

        Assert.Contains(
            window.GetVisualDescendants().OfType<TextBlock>()
                .Where(block => block.IsEffectivelyVisible),
            block => block.Text == "Sort");
    }

    [AvaloniaFact]
    public async Task TheOptionsReadAsEnglishRatherThanAsEnumNames()
    {
        var (window, _) = await OpenAsync();

        var labels = SortBox(window).ItemsSource!.Cast<SortOption>()
            .Select(option => option.Label)
            .ToList();

        Assert.Contains("Priority & due date", labels);
        Assert.Contains("Recently created", labels);
        Assert.DoesNotContain("Smart", labels);
        Assert.DoesNotContain("DueDate", labels);
    }

    [AvaloniaFact]
    public async Task EverySortIsOffered()
    {
        // A sort added to the enum and forgotten here would silently never be
        // reachable from the UI.
        var (window, _) = await OpenAsync();

        var values = SortBox(window).ItemsSource!.Cast<SortOption>()
            .Select(option => option.Value);

        Assert.Equal(Enum.GetValues<TaskSort>(), values);
    }

    [AvaloniaFact]
    public async Task ChoosingAnOptionSetsTheSort()
    {
        // The drop-down carries SortOption but binds through to TaskSort, so a
        // mismatched SelectedValueBinding would break selection silently.
        var (window, workspace) = await OpenAsync();
        var box = SortBox(window);

        box.SelectedItem = box.ItemsSource!.Cast<SortOption>()
            .Single(option => option.Value == TaskSort.Summary);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(TaskSort.Summary, workspace.Sort);
    }

    private static ComboBox SortBox(Window window) =>
        window.GetVisualDescendants().OfType<ComboBox>().First();

    private async Task<(Window Window, WorkspaceViewModel Workspace)> OpenAsync()
    {
        var settings = AppSettings.Load(Path.Combine(_root, "settings.json"));
        var store = new TaskStore(new Core.Storage.Workspace(
            Directory.CreateDirectory(Path.Combine(_root, "ws")).FullName));
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
