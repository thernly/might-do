using Avalonia.Headless.XUnit;
using MightDo.App.ViewModels;
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
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mightdo-background-" + Guid.NewGuid().ToString("N")[..8]);

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

    private sealed class NoPicker : IFolderPicker, IFilePicker
    {
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title) => Task.FromResult<string?>(null);
    }
}
