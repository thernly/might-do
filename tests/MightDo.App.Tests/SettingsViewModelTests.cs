using Avalonia.Headless.XUnit;
using MightDo.App.ViewModels;
using MightDo.Core.Domain;
using MightDo.Core.Session;
using MightDo.Core.Storage;
using MightDo.Platform;

namespace MightDo.App.Tests;

/// <remarks>
/// Driven on Avalonia's UI thread, like the other view-model suites here. The
/// settings page marshals onto it before touching the collections the window is
/// bound to — a rescan arrives on a background thread — so a test running
/// without a dispatcher would post its work into a loop nothing pumps and watch
/// nothing happen.
/// </remarks>
public class SettingsViewModelTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mightdo-settings-vm-" + Guid.NewGuid().ToString("N")[..8]);

    private WorkspaceSession _session = null!;
    private SettingsViewModel _vm = null!;

    public async ValueTask InitializeAsync()
    {
        _session = await WorkspaceSession.OpenAsync(
            new TaskStore(new Core.Storage.Workspace(_root)));
        _vm = new SettingsViewModel(_session, AppSettings.Load(Path.Combine(_root, "settings.json")));
    }

    public ValueTask DisposeAsync()
    {
        _vm.Dispose();
        _session.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private WorkspaceConfig Config => _session.Snapshot.Config;

    // ---- trash -------------------------------------------------------------

    [AvaloniaFact]
    public async Task TheTrashSectionListsTrashedTasks()
    {
        var task = await _session.CreateTaskAsync("Threw it out");
        await _session.TrashTaskAsync(task);

        await _vm.RefreshTrashCommand.ExecuteAsync(null!);

        var row = Assert.Single(_vm.TrashedTasks);
        Assert.Equal("Threw it out", row.Summary);
        Assert.Equal("Not Started", row.StatusName);
    }

    [AvaloniaFact]
    public async Task RestoringReturnsTheTaskAndEmptiesTheList()
    {
        var task = await _session.CreateTaskAsync("Wanted after all");
        await _session.TrashTaskAsync(task);
        await _vm.RefreshTrashCommand.ExecuteAsync(null!);

        await _vm.RestoreTaskCommand.ExecuteAsync(_vm.TrashedTasks.Single());

        Assert.Empty(_vm.TrashedTasks);
        Assert.NotNull(_session.Snapshot.TaskById(task.Id));
    }

    private StatusRowViewModel Row(string name) => _vm.Statuses.First(s => s.Name == name);

    // ---- statuses ----------------------------------------------------------

    [AvaloniaFact]
    public void ShowsTheSeededStatusesInBoardOrder() =>
        Assert.Equal(
            ["Backlog", "Not Started", "In Progress", "Blocked", "Done", "Abandoned"],
            _vm.Statuses.Select(s => s.Name));

    [AvaloniaFact]
    public async Task AddingAStatusAppendsIt()
    {
        _vm.NewStatusName = "In Review";
        _vm.NewStatusType = StatusType.Active;

        await _vm.AddStatusCommand.ExecuteAsync(null!);

        Assert.Equal("In Review", _vm.Statuses[^1].Name);
        Assert.Equal("", _vm.NewStatusName);
    }

    [AvaloniaFact]
    public async Task RenamingAStatusSavesIt()
    {
        var row = Row("Blocked");
        row.Name = "Waiting on someone";

        await _vm.RenameStatusCommand.ExecuteAsync(row);

        Assert.Contains(Config.Statuses, s => s.Name == "Waiting on someone");
    }

    [AvaloniaFact]
    public async Task HidingAStatusTakesItsColumnOffTheBoard()
    {
        var row = Row("Blocked");
        row.HiddenFromBoard = true;

        await _vm.SetStatusHiddenCommand.ExecuteAsync(row);

        Assert.DoesNotContain(Config.BoardStatuses, s => s.Name == "Blocked");
    }

    [AvaloniaFact]
    public async Task ReorderingAStatusMovesTheBoardColumn()
    {
        var row = Row("Blocked");

        await _vm.MoveStatusUpCommand.ExecuteAsync(row);

        Assert.Equal(
            ["Backlog", "Not Started", "Blocked", "In Progress", "Done", "Abandoned"],
            Config.Statuses.Select(s => s.Name));

        // Orders stay contiguous rather than drifting apart.
        Assert.Equal(Enumerable.Range(0, 6), Config.Statuses.Select(s => s.Order));
    }

    [AvaloniaFact]
    public async Task ReorderingPastTheEndDoesNothing()
    {
        var first = _vm.Statuses[0];

        await _vm.MoveStatusUpCommand.ExecuteAsync(first);

        Assert.Equal("Backlog", Config.Statuses[0].Name);
    }

    [AvaloniaFact]
    public void TheDefaultStatusCannotBeDeleted()
    {
        var row = _vm.Statuses.First(s => s.IsDefault);

        Assert.False(row.CanDelete);
        Assert.Contains("new tasks start in", row.BlockerMessage);
    }

    [AvaloniaFact]
    public async Task TheLastStatusOfATypeCannotBeDeleted()
    {
        // Seed has two Active statuses; remove one and the other becomes stuck.
        var blocked = Row("Blocked");
        _vm.BeginDeleteStatusCommand.Execute(blocked);
        _vm.StatusReassignTarget = _vm.StatusReassignOptions
            .First(o => o.Name == "In Progress");
        await _vm.ConfirmDeleteStatusCommand.ExecuteAsync(null!);

        var last = Row("In Progress");

        Assert.False(last.CanDelete);
        Assert.Contains("only Active status", last.BlockerMessage);
    }

    [AvaloniaFact]
    public async Task DeletingAStatusMovesItsTasksRatherThanDeletingThem()
    {
        var doomed = await _session.AddStatusAsync("Doomed", StatusType.Active);
        var task = await _session.CreateTaskAsync("Survivor");
        await _session.MoveToStatusAsync(task, doomed.Id);

        var row = _vm.Statuses.First(s => s.Id == doomed.Id);
        Assert.Equal(1, row.TaskCount);

        _vm.BeginDeleteStatusCommand.Execute(row);
        _vm.StatusReassignTarget = _vm.StatusReassignOptions.First(o => o.Name == "In Progress");
        await _vm.ConfirmDeleteStatusCommand.ExecuteAsync(null!);

        Assert.Null(Config.StatusById(doomed.Id));
        Assert.Single(_session.Snapshot.Tasks);
        Assert.Equal("In Progress",
            Config.StatusById(_session.Snapshot.TaskById(task.Id)!.StatusId)!.Name);
    }

    [AvaloniaFact]
    public void TheReassignListNeverOffersTheStatusBeingDeleted()
    {
        var row = Row("Blocked");

        _vm.BeginDeleteStatusCommand.Execute(row);

        Assert.DoesNotContain(_vm.StatusReassignOptions, o => o.Id == row.Id);
        Assert.True(_vm.IsConfirmingStatusDelete);
    }

    [AvaloniaFact]
    public async Task MakingANonInitialStatusTheDefaultIsRefusedAndExplained()
    {
        await _vm.MakeDefaultCommand.ExecuteAsync(Row("In Progress"));

        Assert.NotNull(_vm.Error);
        Assert.Contains("Initial", _vm.Error);
    }

    [AvaloniaFact]
    public async Task MakingAnotherInitialStatusTheDefaultWorks()
    {
        await _vm.MakeDefaultCommand.ExecuteAsync(Row("Backlog"));

        Assert.Null(_vm.Error);
        Assert.Equal("Backlog", Config.StatusById(Config.DefaultStatusId)!.Name);
    }

    // ---- categories --------------------------------------------------------

    [AvaloniaFact]
    public async Task AddingACategoryParsesItsColour()
    {
        _vm.NewCategoryName = "Work";
        _vm.NewCategoryColor = "FF2E7D32";

        await _vm.AddCategoryCommand.ExecuteAsync(null!);

        var category = Assert.Single(Config.Categories);
        Assert.Equal(0xFF2E7D32u, category.Color);
        Assert.True(category.Color > int.MaxValue, "an opaque colour must not overflow");
    }

    [AvaloniaFact]
    public async Task ABadColourIsExplainedRatherThanSwallowed()
    {
        _vm.NewCategoryName = "Work";
        _vm.NewCategoryColor = "not a colour";

        await _vm.AddCategoryCommand.ExecuteAsync(null!);

        Assert.Empty(Config.Categories);
        Assert.NotNull(_vm.Error);
    }

    [AvaloniaFact]
    public async Task DeletingACategoryCanClearItFromTasks()
    {
        var category = await _session.AddCategoryAsync("Home", 0xFF00FF00);
        var task = await _session.CreateTaskAsync("Fix the door", categoryId: category.Id);

        var row = _vm.Categories.First(c => c.Id == category.Id);
        _vm.BeginDeleteCategoryCommand.Execute(row);
        _vm.CategoryReassignTarget = _vm.CategoryReassignOptions.First(o => o.Id is null);
        await _vm.ConfirmDeleteCategoryCommand.ExecuteAsync(null!);

        Assert.Empty(Config.Categories);
        Assert.Null(_session.Snapshot.TaskById(task.Id)!.CategoryId);
        Assert.Single(_session.Snapshot.Tasks);
    }

    [AvaloniaFact]
    public async Task DeletingACategoryCanReassignInstead()
    {
        var from = await _session.AddCategoryAsync("Old", 0xFF00FF00);
        var to = await _session.AddCategoryAsync("New", 0xFF0000FF);
        var task = await _session.CreateTaskAsync("Move me", categoryId: from.Id);

        var row = _vm.Categories.First(c => c.Id == from.Id);
        _vm.BeginDeleteCategoryCommand.Execute(row);
        _vm.CategoryReassignTarget = _vm.CategoryReassignOptions.First(o => o.Id == to.Id);
        await _vm.ConfirmDeleteCategoryCommand.ExecuteAsync(null!);

        Assert.Equal(to.Id, _session.Snapshot.TaskById(task.Id)!.CategoryId);
    }

    // ---- tags --------------------------------------------------------------

    [AvaloniaFact]
    public async Task AddingATagTwiceReusesIt()
    {
        _vm.NewTagName = "urgent";
        await _vm.AddTagCommand.ExecuteAsync(null!);
        _vm.NewTagName = "URGENT";
        await _vm.AddTagCommand.ExecuteAsync(null!);

        Assert.Single(Config.Tags);
    }

    [AvaloniaFact]
    public async Task DeletingATagDetachesItWithoutAPrompt()
    {
        var tag = await _session.AddTagAsync("waiting");
        var task = await _session.CreateTaskAsync("Tagged", tagIds: [tag.Id]);

        var row = _vm.Tags.First(t => t.Id == tag.Id);
        Assert.Equal(1, row.TaskCount);

        await _vm.DeleteTagCommand.ExecuteAsync(row);

        Assert.Empty(Config.Tags);
        Assert.Empty(_session.Snapshot.TaskById(task.Id)!.TagIds);
    }

    [AvaloniaFact]
    public async Task TheViewFollowsChangesMadeElsewhere()
    {
        // Another window, or a rescan picking up an edit from another machine.
        await _session.AddTagAsync("from elsewhere");

        Assert.Contains(_vm.Tags, t => t.Name == "from elsewhere");
    }
}
