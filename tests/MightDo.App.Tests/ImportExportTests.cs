using Avalonia.Headless.XUnit;
using MightDo.App.ViewModels;
using MightDo.Core.Domain;
using MightDo.Core.Interchange;
using MightDo.Core.Session;
using MightDo.Core.Storage;
using MightDo.Platform;

namespace MightDo.App.Tests;

/// <summary>
/// The settings page's Import and export section. The format itself is pinned
/// in <c>MightDo.Core.Tests</c>; what is proven here is that the buttons write
/// what the window says they will, and nothing before the user says so.
/// </summary>
public class ImportExportTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "mightdo-interchange-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    private readonly Saver _saver = new();
    private readonly Picker _picker = new();

    private WorkspaceSession _session = null!;
    private SettingsViewModel _vm = null!;
    private TaskQuerySpy _query = null!;

    public async ValueTask InitializeAsync()
    {
        _session = await WorkspaceSession.OpenAsync(
            new TaskStore(new Core.Storage.Workspace(_root)));
        _query = new TaskQuerySpy(_session);
        _vm = new SettingsViewModel(
            _session,
            AppSettings.Load(Path.Combine(_root, "settings.json")),
            _picker,
            _saver,
            _query.Selection);
    }

    public ValueTask DisposeAsync()
    {
        _vm.Dispose();
        _session.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private string StatusNamed(StatusType type) =>
        _session.Snapshot.Config.Statuses.First(status => status.Type == type).Name;

    private string WriteCsv(params string[] lines)
    {
        var path = Path.Combine(_root, $"import-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
        return path;
    }

    // ---- export ------------------------------------------------------------

    [AvaloniaFact]
    public async Task ExportWritesExactlyWhatTheListIsShowingInTheOrderItIsShowingIt()
    {
        await _session.CreateTaskAsync("Alpha");
        await _session.CreateTaskAsync("Beta");
        _query.Only = "Beta";

        _saver.Target = Path.Combine(_root, "out.csv");
        await _vm.ExportCommand.ExecuteAsync(null!);

        var written = TaskCsv.Read(File.ReadAllText(_saver.Target), _session.Snapshot.Config);
        Assert.Equal(["Beta"], written.Rows.Select(row => row.Summary));
    }

    [AvaloniaFact]
    public async Task TheButtonSaysWhetherTheExportIsFilteredAndHowManyRowsItIs()
    {
        await _session.CreateTaskAsync("Alpha");
        await _session.CreateTaskAsync("Beta");

        Assert.Equal("Export all 2 tasks", _vm.ExportLabel);

        // Nobody should discover the filter applied by opening the file.
        _query.Only = "Beta";
        Assert.Equal("Export 1 task (filtered)", _vm.ExportLabel);
    }

    [AvaloniaFact]
    public async Task CancellingTheSavePickerWritesNothing()
    {
        await _session.CreateTaskAsync("Alpha");
        _saver.Target = null;

        await _vm.ExportCommand.ExecuteAsync(null!);

        Assert.Empty(Directory.GetFiles(_root, "*.csv"));
        Assert.Null(_vm.ImportResult);
    }

    // ---- the preview -------------------------------------------------------

    [AvaloniaFact]
    public async Task ThePreviewCountsWhatWouldHappenAndWritesNothingYet()
    {
        var existing = await _session.CreateTaskAsync("Already here");
        _picker.Target = WriteCsv(
            "id,summary,status",
            $"{existing.Id},Renamed,{StatusNamed(StatusType.Initial)}",
            $",Brand new,{StatusNamed(StatusType.Initial)}",
            ",,Nowhere");

        await _vm.ChooseImportFileCommand.ExecuteAsync(null!);

        Assert.True(_vm.IsPreviewingImport);
        // The last row is blank where it needs a summary and names a status that
        // does not exist, and is told both things rather than only the first.
        Assert.Equal("Create 1 · Update 1 · Unchanged 0 · Errors 2", _vm.ImportSummary);
        Assert.All(
            _vm.ImportErrors,
            error => Assert.StartsWith("line 4 — ", error, StringComparison.Ordinal));

        // An import is a commitment, so it waits to be made.
        Assert.Equal("Already here", _session.Snapshot.TaskById(existing.Id)!.Summary);
    }

    [AvaloniaFact]
    public async Task CancellingThePreviewWritesNothing()
    {
        var existing = await _session.CreateTaskAsync("Already here");
        _picker.Target = WriteCsv("id,summary", $"{existing.Id},Renamed");

        await _vm.ChooseImportFileCommand.ExecuteAsync(null!);
        _vm.CancelImportCommand.Execute(null);

        Assert.False(_vm.IsPreviewingImport);
        Assert.Empty(_vm.ImportErrors);
        Assert.Equal("Already here", _session.Snapshot.TaskById(existing.Id)!.Summary);
    }

    [AvaloniaFact]
    public async Task ImportingAppliesThePlanAndRaisesExactlyOneChange()
    {
        var raised = 0;
        _session.Changed += (_, _) => raised++;
        _picker.Target = WriteCsv(
            "summary,status",
            $"One,{StatusNamed(StatusType.Initial)}",
            $"Two,{StatusNamed(StatusType.Initial)}");

        await _vm.ChooseImportFileCommand.ExecuteAsync(null!);
        await _vm.ApplyImportCommand.ExecuteAsync(null!);

        Assert.Equal(1, raised);
        Assert.Equal(2, _session.Snapshot.Tasks.Count);
        Assert.False(_vm.IsPreviewingImport);
        Assert.Contains("2 created", _vm.ImportResult);
    }

    /// <summary>
    /// The warning is the one irreversible thing an import does that a user
    /// could easily not have meant, so it must not cry wolf.
    /// </summary>
    [AvaloniaFact]
    public async Task TheRemovalWarningAppearsOnlyWhenNotesWouldActuallyGo()
    {
        var task = await _session.CreateTaskAsync("Has a note");
        await _session.AddNoteAsync(task, "Worth keeping");

        _picker.Target = WriteCsv("id,summary", $"{task.Id},Renamed");
        await _vm.ChooseImportFileCommand.ExecuteAsync(null!);
        Assert.False(_vm.HasImportRemovals);

        _picker.Target = WriteCsv("id,notes", $"{task.Id},");
        await _vm.ChooseImportFileCommand.ExecuteAsync(null!);
        Assert.True(_vm.HasImportRemovals);
        Assert.Equal("Removes 1 note from existing tasks.", _vm.ImportRemovalWarning);
    }

    [AvaloniaFact]
    public async Task TurningOffTheCreateNamesOptionRePlansTheSameFile()
    {
        _picker.Target = WriteCsv(
            "summary,status,tags", $"One,{StatusNamed(StatusType.Initial)},allotment");

        await _vm.ChooseImportFileCommand.ExecuteAsync(null!);
        Assert.True(_vm.HasImportNewNames);

        _vm.CreateCategoriesAndTags = false;
        await _vm.PendingWork;

        Assert.False(_vm.HasImportNewNames);
    }

    [AvaloniaFact]
    public async Task AFileThatIsNotATaskListIsReportedRatherThanThrown()
    {
        _picker.Target = WriteCsv("alpha,beta", "1,2");

        await _vm.ChooseImportFileCommand.ExecuteAsync(null!);

        Assert.False(_vm.IsPreviewingImport);
        Assert.NotNull(_vm.Error);
    }

    // ---- doubles -----------------------------------------------------------

    /// <summary>Stands in for the list view's query, which is view state this page only reads.</summary>
    private sealed class TaskQuerySpy(WorkspaceSession session)
    {
        /// <summary>A summary to narrow to, standing in for any filter at all.</summary>
        public string? Only { get; set; }

        public ExportSelection Selection() => new(
            [.. session.Snapshot.Tasks.Where(task => Only is null || task.Summary == Only)],
            Only is not null,
            "workspace tasks 2026-08-20.csv");
    }

    private sealed class Saver : IFileSaver
    {
        public string? Target { get; set; }

        public Task<string?> PickSaveFileAsync(string title, string suggestedName) =>
            Task.FromResult(Target);
    }

    private sealed class Picker : IFilePicker
    {
        public string? Target { get; set; }

        public Task<string?> PickFileAsync(string title) => Task.FromResult(Target);
    }
}
