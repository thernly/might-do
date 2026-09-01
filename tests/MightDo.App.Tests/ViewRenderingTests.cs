using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MightDo.App.ViewModels;
using MightDo.App.Views;
using MightDo.Core.Domain;
using MightDo.Core.Session;
using MightDo.Core.Storage;
using MightDo.Platform;

namespace MightDo.App.Tests;

/// <summary>
/// Loads the real windows against a real workspace, without a display.
/// </summary>
/// <remarks>
/// These exist because three views were written and shipped without anything
/// proving they render: macOS accessibility cannot drive Avalonia, so the only
/// verification was a screenshot taken by hand. A XAML file naming a type that
/// does not exist, or an <c>x:DataType</c> that silently resolves a binding
/// against the wrong object, shows up here and nowhere else.
/// </remarks>
public class ViewRenderingTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "mightdo-views-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private AppSettings Settings() =>
        AppSettings.Load(Path.Combine(_root, "settings.json"));

    private async Task<WorkspaceViewModel> OpenWorkspaceAsync()
    {
        var store = new TaskStore(new Core.Storage.Workspace(
            Directory.CreateDirectory(Path.Combine(_root, "ws")).FullName));
        var workspace = await WorkspaceViewModel.OpenAsync(store, Settings(), new NoPicker());
        _disposables.Add(workspace);
        return workspace;
    }

    /// <summary>
    /// Descendants of a given type that are actually on screen.
    /// </summary>
    /// <remarks>
    /// Visibility has to be part of the question. Avalonia's
    /// <c>IsVisible="false"</c> leaves an element in the visual tree rather than
    /// removing it, so a plain descendant search finds the workspace picker
    /// while the list is showing, and finds the detail pane while nothing is
    /// selected — which would make these tests pass no matter what.
    /// </remarks>
    private static List<T> Descendants<T>(Visual root) where T : Visual =>
        [.. root.GetVisualDescendants().OfType<T>().Where(v => v.IsEffectivelyVisible)];

    private static List<string> TextsIn(Visual root) =>
        [.. Descendants<TextBlock>(root)
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)];

    // ---- the shell ---------------------------------------------------------

    [AvaloniaFact]
    public void TheAboutWindowShowsTheApplicationsIdentity()
    {
        var window = new AboutWindow();
        window.Show();

        var text = TextsIn(window);
        Assert.Contains("Might Do", text);
        Assert.Contains(text, value => value.StartsWith("Version "));
        Assert.Contains(text, value => value.Contains("Apache License 2.0"));
    }

    [AvaloniaFact]
    public void TheWindowLoadsAndShowsThePickerWhenThereIsNoWorkspace()
    {
        var window = new MainWindow
        {
            DataContext = new MainViewModel(Settings(), new NoPicker(), new NoPicker()),
        };

        window.Show();

        // The picker's explanation is the giveaway that the no-workspace branch
        // is the one showing.
        Assert.Contains(TextsIn(window), text => text.Contains("Choose a folder"));
    }

    [AvaloniaFact]
    public async Task OpeningAWorkspaceSwapsThePickerForTheList()
    {
        var main = new MainViewModel(Settings(), new NoPicker(), new NoPicker());
        var window = new MainWindow { DataContext = main };
        window.Show();

        await main.OpenAsync(Directory.CreateDirectory(Path.Combine(_root, "ws")).FullName);
        Assert.NotNull(main.Workspace);
        _disposables.Add(main.Workspace!);

        main.Workspace!.NewTaskSummary = "Visible in the list";
        await main.Workspace.CreateTaskCommand.ExecuteAsync(null!);

        Dispatcher.UIThread.RunJobs();

        Assert.Contains(TextsIn(window), text => text == "Visible in the list");
        Assert.DoesNotContain(TextsIn(window), text => text.Contains("Choose a folder"));
    }

    // ---- the detail pane ---------------------------------------------------

    [AvaloniaFact]
    public async Task SelectingATaskOpensTheDetailPane()
    {
        // The interaction that could not be driven from outside the process.
        var workspace = await OpenWorkspaceAsync();
        var window = new MainWindow
        {
            DataContext = new MainViewModel(Settings(), new NoPicker(), new NoPicker())
            {
                Workspace = workspace,
            },
        };
        window.Show();

        workspace.NewTaskSummary = "Open me";
        await workspace.CreateTaskCommand.ExecuteAsync(null!);
        Dispatcher.UIThread.RunJobs();

        Assert.False(workspace.HasSelection);
        Assert.Empty(Descendants<TaskDetailView>(window));

        workspace.SelectedTask = workspace.Tasks.Single();
        Dispatcher.UIThread.RunJobs();

        Assert.True(workspace.HasSelection);
        Assert.NotNull(workspace.Detail);

        var pane = Assert.Single(Descendants<TaskDetailView>(window));
        Assert.Contains(TextsIn(pane), text => text == "STEPS");
        Assert.Contains(TextsIn(pane), text => text == "NOTES");
        Assert.Contains(TextsIn(pane), text => text == "REMINDERS");
    }

    [AvaloniaFact]
    public async Task ClosingTheDetailPaneHidesIt()
    {
        var workspace = await OpenWorkspaceAsync();
        var window = new MainWindow
        {
            DataContext = new MainViewModel(Settings(), new NoPicker(), new NoPicker())
            {
                Workspace = workspace,
            },
        };
        window.Show();

        workspace.NewTaskSummary = "Open me";
        await workspace.CreateTaskCommand.ExecuteAsync(null!);
        Dispatcher.UIThread.RunJobs();
        workspace.SelectedTask = workspace.Tasks.Single();
        Dispatcher.UIThread.RunJobs();
        Assert.Single(Descendants<TaskDetailView>(window));

        workspace.CloseDetailCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(workspace.Detail);
        Assert.False(workspace.HasSelection);
    }

    // ---- the board ---------------------------------------------------------

    [AvaloniaFact]
    public async Task TheBoardRendersAColumnPerVisibleStatus()
    {
        var workspace = await OpenWorkspaceAsync();
        var window = new MainWindow
        {
            DataContext = new MainViewModel(Settings(), new NoPicker(), new NoPicker())
            {
                Workspace = workspace,
            },
        };
        window.Show();

        workspace.ShowBoardCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var board = Assert.Single(Descendants<BoardView>(window));
        var texts = TextsIn(board);

        // The seed hides Backlog and Abandoned from the board.
        Assert.Contains("Not Started", texts);
        Assert.Contains("In Progress", texts);
        Assert.Contains("Done", texts);
        Assert.DoesNotContain("Backlog", texts);
        Assert.DoesNotContain("Abandoned", texts);
    }

    [AvaloniaFact]
    public async Task TheBoardShowsCompletedWorkTheListHides()
    {
        var workspace = await OpenWorkspaceAsync();
        var window = new MainWindow
        {
            DataContext = new MainViewModel(Settings(), new NoPicker(), new NoPicker())
            {
                Workspace = workspace,
            },
        };
        window.Show();

        workspace.NewTaskSummary = "Shipped";
        await workspace.CreateTaskCommand.ExecuteAsync(null!);
        Dispatcher.UIThread.RunJobs();

        var done = workspace.Statuses.First(s => s.Name == "Done");
        await workspace.MoveOnBoardAsync(workspace.Tasks.Single().Id, done.Id, null);
        Dispatcher.UIThread.RunJobs();

        // Gone from the list, present on the board — the decision from the
        // query work, now proven through the actual views.
        Assert.Empty(workspace.Tasks);

        workspace.ShowBoardCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var board = Assert.Single(Descendants<BoardView>(window));
        Assert.Contains("Shipped", TextsIn(board));
    }

    // ---- settings ----------------------------------------------------------

    [AvaloniaFact]
    public async Task TheSettingsWindowRendersEverySection()
    {
        var workspace = await OpenWorkspaceAsync();
        var settings = workspace.CreateSettingsViewModel();
        _disposables.Add(settings);

        var window = new SettingsWindow { DataContext = settings };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var texts = TextsIn(window);
        Assert.Contains("STATUSES", texts);
        Assert.Contains("CATEGORIES", texts);
        Assert.Contains("TAGS", texts);
        Assert.Contains("IMPORT AND EXPORT", texts);

        // The glossary's term, not "Stage".
        Assert.Contains(texts, text => text.Contains("Status Types"));
        Assert.DoesNotContain(texts, text => text.Contains("Stage"));
    }

    /// <summary>
    /// The type drop-down is the only way a status is retyped, so the binding
    /// behind it is worth asserting through the window rather than the view
    /// model: a one-way binding would leave the control inert and every
    /// view-model test still green.
    /// </summary>
    [AvaloniaFact]
    public async Task ChangingAStatusTypeInItsDropDownSavesIt()
    {
        var workspace = await OpenWorkspaceAsync();
        var settings = workspace.CreateSettingsViewModel();
        _disposables.Add(settings);

        var window = new SettingsWindow { DataContext = settings };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var row = settings.Statuses.First(s => s.Name == "Blocked");
        var combo = Descendants<ComboBox>(window)
            .First(c => ReferenceEquals(c.DataContext, row));

        combo.SelectedItem = StatusType.Final;
        Dispatcher.UIThread.RunJobs();
        await settings.SetStatusTypeCommand.ExecutionTask!;

        Assert.Equal(StatusType.Final, row.Type);
        Assert.Equal(
            StatusType.Final,
            settings.Statuses.First(s => s.Name == "Blocked").Type);
    }

    /// <summary>
    /// A category's colour is offered by name and swatch. Eight hex digits are
    /// how it is stored, never how it is shown.
    /// </summary>
    [AvaloniaFact]
    public async Task ACategoryRowNamesItsColourRatherThanSpellingItInHex()
    {
        var workspace = await OpenWorkspaceAsync();
        var settings = workspace.CreateSettingsViewModel();
        _disposables.Add(settings);

        settings.NewCategoryName = "Home";
        settings.NewCategoryColor = Category.Palette[1];
        await settings.AddCategoryCommand.ExecuteAsync(null!);

        var window = new SettingsWindow { DataContext = settings };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var texts = TextsIn(window);
        Assert.Contains(Category.Palette[1].Name, texts);
        Assert.DoesNotContain(
            texts, text => text.Contains(Category.Palette[1].Value.ToString("X8")));
    }

    [AvaloniaFact]
    public async Task TheSettingsWindowExplainsWhyAStatusCannotBeDeleted()
    {
        var workspace = await OpenWorkspaceAsync();
        var settings = workspace.CreateSettingsViewModel();
        _disposables.Add(settings);

        var window = new SettingsWindow { DataContext = settings };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(TextsIn(window), text => text.Contains("new tasks start in"));
    }

    /// <summary>
    /// The preview is inside a collapsed border until there is a plan, so it is
    /// the one part of this window a rendering test would otherwise never build.
    /// </summary>
    [AvaloniaFact]
    public async Task TheImportPreviewRendersItsCountsErrorsAndWarning()
    {
        var session = await WorkspaceSession.OpenAsync(new TaskStore(new Core.Storage.Workspace(
            Directory.CreateDirectory(Path.Combine(_root, "preview")).FullName)));
        _disposables.Add(session);

        var picker = new CsvPicker(_root);
        var settings = new SettingsViewModel(
            session, Settings(), picker, fileSaver: null, exportSelection: null);
        _disposables.Add(settings);

        var status = session.Snapshot.Config.Statuses.First().Name;
        var task = await session.CreateTaskAsync("Has a note");
        await session.AddNoteAsync(task, "Worth keeping");

        picker.Write(
            "id,summary,status,notes",
            $"{task.Id},Renamed,{status},",
            $",Brand new,{status},",
            $",,{status},");

        var window = new SettingsWindow { DataContext = settings };
        window.Show();
        await settings.ChooseImportFileCommand.ExecuteAsync(null!);
        Dispatcher.UIThread.RunJobs();

        var texts = TextsIn(window);
        Assert.Contains(texts, text => text.Contains("Create 1"));
        Assert.Contains(texts, text => text.Contains("Removes 1 note"));
        Assert.Contains(texts, text => text.StartsWith("line ", StringComparison.Ordinal));
    }

    private sealed class CsvPicker(string root) : IFilePicker
    {
        private string? _path;

        public void Write(params string[] lines)
        {
            _path = Path.Combine(root, "import.csv");
            File.WriteAllText(_path, string.Join("\r\n", lines) + "\r\n");
        }

        public Task<string?> PickFileAsync(string title) => Task.FromResult(_path);
    }

    private sealed class NoPicker : IFolderPicker, IFilePicker
    {
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title) => Task.FromResult<string?>(null);
    }
}
