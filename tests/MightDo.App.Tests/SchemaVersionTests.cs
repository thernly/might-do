using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using MightDo.App.ViewModels;
using MightDo.Core.Domain;
using MightDo.Core.Serialization;
using MightDo.Core.Storage;
using MightDo.Platform;

namespace MightDo.App.Tests;

/// <summary>
/// What the app does with a workspace written by a newer version of itself.
/// </summary>
/// <remarks>
/// Refusing the data is only half the fix: a task that is neither shown nor
/// explained looks exactly like a task the app lost, and an unopenable folder
/// that reports nothing looks like a crash. Both refusals therefore have to
/// arrive as something the user can read.
/// </remarks>
public class SchemaVersionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mightdo-schema-" + Guid.NewGuid().ToString("N")[..8]);

    private MainViewModel? _main;

    public void Dispose()
    {
        _main?.Workspace?.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private MainViewModel Shell()
    {
        Directory.CreateDirectory(_root);
        var settings = AppSettings.Load(Path.Combine(_root, "settings.json"));
        return _main = new MainViewModel(settings, new NoPicker(), new NoPicker());
    }

    private string WorkspaceFolder()
    {
        var path = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(path);
        return path;
    }

    [AvaloniaFact]
    public async Task NamesATaskFileFromANewerVersionInsteadOfDroppingItSilently()
    {
        var folder = WorkspaceFolder();
        var store = new TaskStore(new Workspace(folder));
        var config = await store.InitialiseAsync(TestContext.Current.CancellationToken);

        var mine = MightDoTask.Create("Mine", config.DefaultStatusId, Rank.First);
        await store.SaveTaskAsync(mine, TestContext.Current.CancellationToken);

        var theirs = MightDoTask.Create("Theirs", mine.StatusId, Rank.Between(mine.BoardRank, ""));
        WriteFromTheFuture(store.Workspace.TaskFile(theirs.Id), theirs);

        var main = Shell();
        await main.OpenAsync(folder);

        // The workspace still opens, and the task that is safe to edit is there.
        Assert.True(main.HasWorkspace);
        Assert.Equal(["Mine"], main.Workspace!.Tasks.Select(t => t.Summary));

        var unreadable = Assert.Single(main.Workspace.Unreadable);
        Assert.Contains($"{theirs.Id}.json", unreadable);
        Assert.Contains("schema version 2", unreadable);
    }

    [AvaloniaFact]
    public async Task ExplainsAWorkspaceWhoseConfigIsFromANewerVersionRatherThanFailing()
    {
        var folder = WorkspaceFolder();
        var store = new TaskStore(new Workspace(folder));
        var config = await store.InitialiseAsync(TestContext.Current.CancellationToken);
        WriteFromTheFuture(store.Workspace.ConfigFile, config);

        var main = Shell();
        await main.OpenAsync(folder);

        // The refusal reaches the user as a message rather than as an exception
        // out of an async void window event.
        Assert.False(main.HasWorkspace);
        Assert.NotNull(main.Message);
        Assert.Contains("config.json", main.Message);
        Assert.Contains("schema version 2", main.Message);
    }

    /// <summary>Writes a file as a later version of the app would have left it.</summary>
    private static void WriteFromTheFuture<T>(string path, T value)
    {
        var node = JsonNode.Parse(WorkspaceJson.Serialize(value))!.AsObject();
        node["schemaVersion"] = 2;
        node["somethingNew"] = "a field this build has never heard of";
        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class NoPicker : IFolderPicker, IFilePicker
    {
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title) => Task.FromResult<string?>(null);
    }
}
