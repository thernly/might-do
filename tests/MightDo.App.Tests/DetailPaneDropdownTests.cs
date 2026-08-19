using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MightDo.App.ViewModels;
using MightDo.App.Views;
using MightDo.Core.Storage;
using MightDo.Platform;

namespace MightDo.App.Tests;

/// <summary>
/// The detail pane's Status and Category dropdowns, driven through the real
/// controls in a real window.
/// </summary>
/// <remarks>
/// The view model's own tests set <c>SelectedStatus</c> and
/// <c>SelectedCategory</c> directly, which a ComboBox does not: it selects an
/// item out of its own ItemsSource. Only a real dropdown over a real pane shows
/// what happens when the collection behind it is rebuilt.
/// </remarks>
public class DetailPaneDropdownTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mightdo-dropdown-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact]
    public async Task PickingAStatusSavesIt()
    {
        var (window, workspace) = await OpenAsync();
        SelectRow(window, workspace);

        var moved = PickOther(Box(window, "StatusBox"));
        await SettleAsync(workspace);

        Assert.Equal(moved, workspace.Detail!.SelectedStatus?.Name);
        Assert.Equal(moved, workspace.Tasks.Single().StatusName);
    }

    [AvaloniaFact]
    public async Task PickingACategorySavesIt()
    {
        var (window, workspace) = await OpenAsync();
        SelectRow(window, workspace);

        var picked = PickOther(Box(window, "CategoryBox"));
        await SettleAsync(workspace);

        Assert.Equal(picked, workspace.Detail!.SelectedCategory?.Name);
        Assert.Equal(picked, workspace.Tasks.Single().CategoryName);
    }

    [AvaloniaFact]
    public async Task TheDropdownsStillShowTheTaskAfterARescan()
    {
        // A rescan refreshes the pane in place. Rebuilding the option lists
        // underneath a ComboBox drops its selection, which reads as the edit
        // having been lost.
        var (window, workspace) = await OpenAsync();
        SelectRow(window, workspace);

        var status = PickOther(Box(window, "StatusBox"));
        var category = PickOther(Box(window, "CategoryBox"));
        await SettleAsync(workspace);

        await workspace.RefreshCommand.ExecuteAsync(null!);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(status, workspace.Detail!.SelectedStatus?.Name);
        Assert.Equal(category, workspace.Detail.SelectedCategory?.Name);
        Assert.Equal(status, ((StatusOption)Box(window, "StatusBox").SelectedItem!).Name);
        Assert.Equal(category, ((CategoryOption)Box(window, "CategoryBox").SelectedItem!).Name);
    }

    [AvaloniaFact]
    public async Task TheDropdownsFollowTheTaskWhenAnotherIsOpened()
    {
        var (window, workspace) = await OpenAsync("First", "Second");
        var second = workspace.Tasks.First(row => row.Summary == "Second");

        // Read before anything is opened: read afterwards, this would agree with
        // whatever opening the task wrote to it.
        var secondStatus = second.StatusName;

        workspace.SelectTaskById(workspace.Tasks.First(row => row.Summary == "First").Id);
        Dispatcher.UIThread.RunJobs();
        PickOther(Box(window, "StatusBox"));
        var category = PickOther(Box(window, "CategoryBox"));
        await SettleAsync(workspace);

        workspace.SelectTaskById(second.Id);
        await SettleAsync(workspace);

        // Second was never edited, so its own status stands and it has no
        // category — neither dropdown carries First's choice across, and neither
        // writes it to the task on the way past.
        var untouched = workspace.Tasks.First(row => row.Summary == "Second");
        Assert.Equal(secondStatus, untouched.StatusName);
        Assert.Equal(secondStatus, NameOf(Box(window, "StatusBox").SelectedItem));
        Assert.Equal("No category", NameOf(Box(window, "CategoryBox").SelectedItem));
        Assert.NotEqual("No category", category);
        Assert.Null(untouched.CategoryName);
    }

    [AvaloniaFact]
    public async Task ChoosingNoCategoryClearsIt()
    {
        // "No category" is an option like any other, so clearing a task's
        // category has to keep working — it is not the same thing as a dropdown
        // with nothing selected, which the pane ignores.
        var (window, workspace) = await OpenAsync();
        SelectRow(window, workspace);

        PickOther(Box(window, "CategoryBox"));
        await SettleAsync(workspace);
        Assert.NotNull(workspace.Tasks.Single().CategoryName);

        Box(window, "CategoryBox").SelectedIndex = 0;
        await SettleAsync(workspace);

        Assert.Null(workspace.Tasks.Single().CategoryName);
        Assert.Equal("No category", NameOf(Box(window, "CategoryBox").SelectedItem));
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Picks the first option that is not the one already showing, the way a
    /// user choosing from an open dropdown does, and returns its name.
    /// </summary>
    /// <remarks>
    /// The name is read before the pointer leaves, so to speak: what the box
    /// says afterwards is the thing under test, not the thing to compare
    /// against.
    /// </remarks>
    private static string PickOther(ComboBox box)
    {
        var index = box.SelectedIndex == 0 ? 1 : 0;
        var name = NameOf(box.Items[index]);

        box.SelectedIndex = index;
        Dispatcher.UIThread.RunJobs();

        return name;
    }

    private static string NameOf(object? option) => option switch
    {
        StatusOption status => status.Name,
        CategoryOption category => category.Name,
        var other => other?.ToString() ?? "",
    };

    private static ComboBox Box(Window window, string name) =>
        window.GetVisualDescendants().OfType<ComboBox>()
            .Where(box => box.IsEffectivelyVisible)
            .First(box => box.Name == name);

    private static void SelectRow(Window window, WorkspaceViewModel workspace)
    {
        workspace.SelectTaskById(workspace.Tasks.Single().Id);
        Dispatcher.UIThread.RunJobs();
        window.Measure(window.ClientSize);
        window.Arrange(new Rect(window.ClientSize));
    }

    /// <summary>Lets the pending write land and the rescan it causes run.</summary>
    private static async Task SettleAsync(WorkspaceViewModel workspace)
    {
        if (workspace.Detail is { } detail) await detail.PendingSave;
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
    }

    private async Task<(Window Window, WorkspaceViewModel Workspace)> OpenAsync(
        params string[] summaries)
    {
        if (summaries.Length == 0) summaries = ["Only task"];

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

        // The default config ships no categories, and a dropdown with only
        // "No category" in it cannot be used to pick one.
        var settingsVm = workspace.CreateSettingsViewModel();
        foreach (var name in new[] { "Work", "Home" })
        {
            settingsVm.NewCategoryName = name;
            await settingsVm.AddCategoryCommand.ExecuteAsync(null!);
            Dispatcher.UIThread.RunJobs();
        }

        foreach (var summary in summaries)
        {
            workspace.NewTaskSummary = summary;
            await workspace.CreateTaskCommand.ExecuteAsync(null!);
            Dispatcher.UIThread.RunJobs();
        }

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
