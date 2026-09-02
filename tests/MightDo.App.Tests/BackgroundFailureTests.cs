using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MightDo.App.ViewModels;
using MightDo.Core.Query;
using MightDo.Core.Session;
using MightDo.Core.Storage;
using MightDo.Platform;

namespace MightDo.App.Tests;

/// <summary>
/// Where a failure goes when there is no user action to fail.
/// </summary>
/// <remarks>
/// Startup runs from the window's <c>Opened</c> event and rescans run from the
/// watcher, neither of which has a caller. An exception thrown there used to
/// end the process or vanish, the second leaving the list quietly showing state
/// that was no longer true — the one thing live reload exists to prevent.
/// </remarks>
public class BackgroundFailureTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "mightdo-background-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    private MainViewModel? _main;

    public void Dispose()
    {
        _main?.Workspace?.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private MainViewModel Shell(string? remembering = null)
    {
        Directory.CreateDirectory(_root);
        var settings = AppSettings.Load(Path.Combine(_root, "settings.json"));
        if (remembering is not null) settings.AddWorkspace(remembering);

        return _main = new MainViewModel(settings, new NoPicker(), new NoPicker());
    }

    private string WorkspaceFolder()
    {
        var path = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<string> OpenableWorkspaceAsync(string folder)
    {
        var store = new TaskStore(new Workspace(folder));
        await store.InitialiseAsync(TestContext.Current.CancellationToken);
        return store.Workspace.ConfigFile;
    }

    [AvaloniaFact]
    public async Task ExplainsACorruptConfigInsteadOfThrowingOutOfTheWindowEvent()
    {
        var folder = WorkspaceFolder();
        var config = await OpenableWorkspaceAsync(folder);
        await File.WriteAllTextAsync(
            config, "{ half a file", TestContext.Current.CancellationToken);

        var main = Shell();
        await main.OpenAsync(folder);

        Assert.False(main.HasWorkspace);
        Assert.NotNull(main.Message);
        Assert.Contains("config.json", main.Message);

        // And it is still the user's file, not a fresh one seeded over it.
        Assert.Equal(
            "{ half a file",
            await File.ReadAllTextAsync(config, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task StartupSurvivesAWorkspaceThatCannotBeOpened()
    {
        var folder = WorkspaceFolder();
        var config = await OpenableWorkspaceAsync(folder);

        var main = Shell();
        await main.OpenAsync(folder);
        main.Workspace!.Dispose();

        await File.WriteAllTextAsync(
            config, "{ half a file", TestContext.Current.CancellationToken);

        // The same path the window's Opened event takes on the next launch.
        var reopened = Shell(remembering: folder);
        await reopened.InitialiseAsync();

        Assert.False(reopened.HasWorkspace);
        Assert.NotNull(reopened.Message);
    }

    [AvaloniaFact]
    public async Task AFailedRescanSaysSoInsteadOfShowingStaleTasks()
    {
        var folder = WorkspaceFolder();
        var config = await OpenableWorkspaceAsync(folder);

        var main = Shell();
        await main.OpenAsync(folder);
        var workspace = main.Workspace!;

        await File.WriteAllTextAsync(
            config, "{ half a file", TestContext.Current.CancellationToken);

        workspace.RefreshInBackground();
        await workspace.PendingBackgroundWork;

        Assert.NotNull(workspace.Banner);
        Assert.Contains("Refresh", workspace.Banner);
    }

    [AvaloniaFact]
    public async Task ARescanThatWorksAgainTakesTheBannerDown()
    {
        var folder = WorkspaceFolder();
        var config = await OpenableWorkspaceAsync(folder);
        var good = await File.ReadAllTextAsync(config, TestContext.Current.CancellationToken);

        var main = Shell();
        await main.OpenAsync(folder);
        var workspace = main.Workspace!;

        await File.WriteAllTextAsync(
            config, "{ half a file", TestContext.Current.CancellationToken);
        workspace.RefreshInBackground();
        await workspace.PendingBackgroundWork;
        Assert.NotNull(workspace.Banner);

        await File.WriteAllTextAsync(config, good, TestContext.Current.CancellationToken);
        workspace.RefreshInBackground();
        await workspace.PendingBackgroundWork;

        Assert.Null(workspace.Banner);
    }

    [AvaloniaFact]
    public async Task ClosingAWorkspaceDuringARescanIsNotReportedAsAFailure()
    {
        var folder = WorkspaceFolder();
        await OpenableWorkspaceAsync(folder);

        var main = Shell();
        await main.OpenAsync(folder);
        var workspace = main.Workspace!;

        workspace.Dispose();
        workspace.RefreshInBackground();
        await workspace.PendingBackgroundWork;

        // Shutting down is what the user asked for, not something to report.
        Assert.Null(workspace.Banner);
    }

    /// <summary>
    /// A watcher rescan posts its projection to the dispatcher. If projection
    /// throws there, the task that loaded the workspace has already completed and
    /// there is no awaiting caller to receive it; without the projection boundary
    /// the exception terminates the process.
    /// </summary>
    [AvaloniaFact]
    public async Task APostedProjectionFailureIsReportedInsteadOfEscapingTheDispatcher()
    {
        var folder = WorkspaceFolder();
        await OpenableWorkspaceAsync(folder);

        var main = Shell();
        await main.OpenAsync(folder);
        var workspace = main.Workspace!;

        // Put an impossible value directly in the generated backing field. Going
        // through the property would project immediately; the scenario under test
        // is the later projection posted by a background workspace change.
        var sort = typeof(WorkspaceViewModel).GetField(
            "_sort", BindingFlags.Instance | BindingFlags.NonPublic)!;
        sort.SetValue(workspace, (TaskSort)int.MaxValue);

        using (var external = await WorkspaceSession.OpenAsync(
                   new TaskStore(new Workspace(folder))))
        {
            await external.CreateTaskAsync("Arrived from another machine");
            await external.CreateTaskAsync("Another external task");
        }

        await Task.Run(workspace.RefreshInBackground);
        await workspace.PendingBackgroundWork;
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(workspace.Banner);
        Assert.Contains("could not be displayed", workspace.Banner);
    }

    private sealed class NoPicker : IFolderPicker, IFilePicker
    {
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title) => Task.FromResult<string?>(null);
    }
}
