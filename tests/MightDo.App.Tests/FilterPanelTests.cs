using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MightDo.App.ViewModels;
using MightDo.App.Views;
using MightDo.Core.Domain;
using MightDo.Core.Storage;
using MightDo.Platform;

namespace MightDo.App.Tests;

/// <summary>
/// The filter panel, and the count on the button that opens it.
/// </summary>
public class FilterPanelTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mightdo-filters-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private async Task<WorkspaceViewModel> OpenAsync()
    {
        var settings = AppSettings.Load(Path.Combine(_root, "settings.json"));
        var store = new TaskStore(new Core.Storage.Workspace(Path.Combine(_root, "ws")));
        var workspace = await WorkspaceViewModel.OpenAsync(store, settings, new NoPicker());
        _disposables.Add(workspace);
        return workspace;
    }

    private static FilterToggle Toggle(IEnumerable<FilterToggle> group, string name) =>
        group.First(t => t.Name == name);

    // ---- the count on the button -------------------------------------------

    [AvaloniaFact]
    public async Task TheCountIgnoresTheSearchBox()
    {
        // The decision from the grilling: the count describes what is hidden
        // behind the button, and the search box is a visible field outside it.
        var workspace = await OpenAsync();

        workspace.Search = "something";

        Assert.Equal(0, workspace.PanelFilterCount);
        Assert.False(workspace.HasPanelFilters);

        // ...while IsFiltered, which asks a different question, does count it.
        Assert.True(workspace.Query.IsFiltered);
    }

    [AvaloniaFact]
    public async Task EachGroupCountsOnceHoweverManyAreTickedInIt()
    {
        var workspace = await OpenAsync();

        Toggle(workspace.Statuses, "Backlog").IsSelected = true;
        Toggle(workspace.Statuses, "Done").IsSelected = true;

        Assert.Equal(1, workspace.PanelFilterCount);

        Toggle(workspace.Priorities, "High").IsSelected = true;

        Assert.Equal(2, workspace.PanelFilterCount);
    }

    [AvaloniaFact]
    public async Task ShowingCompletedCountsTowardsTheButton()
    {
        var workspace = await OpenAsync();

        workspace.IncludeCompleted = true;

        Assert.Equal(1, workspace.PanelFilterCount);
    }

    // ---- filtering ---------------------------------------------------------

    [AvaloniaFact]
    public async Task SelectingSeveralStatusesShowsAnyOfThem()
    {
        var workspace = await OpenAsync();
        await CreateAsync(workspace, "Waiting");
        await CreateAsync(workspace, "Doing", "In Progress");
        await CreateAsync(workspace, "Stuck", "Blocked");

        Toggle(workspace.Statuses, "Not Started").IsSelected = true;
        Toggle(workspace.Statuses, "Blocked").IsSelected = true;

        Assert.Equal(["Stuck", "Waiting"], workspace.Tasks.Select(t => t.Summary).Order());
    }

    [AvaloniaFact]
    public async Task SelectingTheFinalStatusTypeRevealsCompletedWork()
    {
        // Q1 from the grilling, now reachable through the UI: this control did
        // not exist when the rule was fixed.
        var workspace = await OpenAsync();
        await CreateAsync(workspace, "Open");
        await CreateAsync(workspace, "Shipped", "Done");

        Assert.Equal(["Open"], workspace.Tasks.Select(t => t.Summary));

        Toggle(workspace.StatusTypes, "Final").IsSelected = true;

        Assert.Equal(["Shipped"], workspace.Tasks.Select(t => t.Summary));
    }

    [AvaloniaFact]
    public async Task FiltersFromDifferentGroupsCombineAsAnd()
    {
        var workspace = await OpenAsync();
        await CreateAsync(workspace, "Match", "In Progress", Priority.High);
        await CreateAsync(workspace, "Wrong priority", "In Progress");
        await CreateAsync(workspace, "Wrong status", priority: Priority.High);

        Toggle(workspace.StatusTypes, "Active").IsSelected = true;
        Toggle(workspace.Priorities, "High").IsSelected = true;

        Assert.Equal(["Match"], workspace.Tasks.Select(t => t.Summary));
    }

    [AvaloniaFact]
    public async Task ClearingResetsEveryGroupAndTheSearchBox()
    {
        var workspace = await OpenAsync();
        await CreateAsync(workspace, "Waiting");

        workspace.Search = "nothing matches this";
        Toggle(workspace.Statuses, "Backlog").IsSelected = true;
        workspace.IncludeCompleted = true;
        Assert.Empty(workspace.Tasks);

        workspace.ClearFiltersCommand.Execute(null);

        Assert.Equal("", workspace.Search);
        Assert.Equal(0, workspace.PanelFilterCount);
        Assert.Single(workspace.Tasks);
    }

    [AvaloniaFact]
    public async Task ASelectionSurvivesAWorkspaceReload()
    {
        // Toggles are rebuilt from the config on every rescan. Rebuilding them
        // naively would clear the user's filters whenever a sync client touched
        // a file.
        var workspace = await OpenAsync();
        await CreateAsync(workspace, "Waiting");
        Toggle(workspace.Statuses, "Not Started").IsSelected = true;

        await workspace.RefreshCommand.ExecuteAsync(null!);
        Dispatcher.UIThread.RunJobs();

        Assert.True(Toggle(workspace.Statuses, "Not Started").IsSelected);
        Assert.Equal(1, workspace.PanelFilterCount);
    }

    [AvaloniaFact]
    public async Task RenamingAStatusUpdatesItsFilterLabel()
    {
        var workspace = await OpenAsync();
        var settings = workspace.CreateSettingsViewModel();
        _disposables.Add(settings);

        var row = settings.Statuses.First(s => s.Name == "Blocked");
        row.Name = "Waiting on someone";
        await settings.RenameStatusCommand.ExecuteAsync(row);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(workspace.Statuses, t => t.Name == "Waiting on someone");
        Assert.DoesNotContain(workspace.Statuses, t => t.Name == "Blocked");
    }

    // ---- rendering ---------------------------------------------------------

    [AvaloniaFact]
    public async Task ThePanelIsHiddenUntilTheButtonIsPressed()
    {
        var workspace = await OpenAsync();
        var window = new MainWindow
        {
            DataContext = new MainViewModel(
                AppSettings.Load(Path.Combine(_root, "settings.json")),
                new NoPicker(),
                new NoPicker())
            {
                Workspace = workspace,
            },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain("Status Type", VisibleTexts(window));

        workspace.ToggleFiltersCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var texts = VisibleTexts(window);
        Assert.Contains("Status", texts);
        Assert.Contains("Status Type", texts);
        Assert.Contains("Priority", texts);
        Assert.Contains("Category", texts);
        Assert.Contains("Tags", texts);

        // Q7: the glossary's term, never "Stage".
        Assert.DoesNotContain(texts, text => text.Contains("Stage"));
    }

    private static List<string> VisibleTexts(Visual root) =>
        [.. root.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text))
            .Select(t => t.Text!)];

    /// <summary>
    /// Creates a task the way the UI does, then adjusts it through the same
    /// public paths a user would: the detail pane for priority, a board move for
    /// status.
    /// </summary>
    private static async Task CreateAsync(
        WorkspaceViewModel workspace,
        string summary,
        string? statusName = null,
        Priority priority = Priority.Medium)
    {
        workspace.NewTaskSummary = summary;
        await workspace.CreateTaskCommand.ExecuteAsync(null!);
        Dispatcher.UIThread.RunJobs();

        var created = workspace.Tasks.FirstOrDefault(t => t.Summary == summary)
                      ?? throw new InvalidOperationException($"'{summary}' was not created");

        // Priority first: moving into a Final status can take the task out of
        // the list, and the detail pane follows the selection.
        if (priority != Priority.Medium)
        {
            workspace.SelectedTask = created;
            Dispatcher.UIThread.RunJobs();
            workspace.Detail!.SelectedPriority = priority;
            await Task.Delay(40);
            workspace.CloseDetailCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
        }

        if (statusName is not null)
        {
            var status = workspace.Statuses.First(s => s.Name == statusName);
            await workspace.MoveOnBoardAsync(created.Id, status.Id, null);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private sealed class NoPicker : IFolderPicker, IFilePicker
    {
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title) => Task.FromResult<string?>(null);
    }
}
