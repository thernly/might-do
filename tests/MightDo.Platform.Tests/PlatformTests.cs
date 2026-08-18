using MightDo.Core.Domain;
using MightDo.Platform;

namespace MightDo.Platform.Tests;

public class AppSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "mightdo-settings-" + Guid.NewGuid().ToString("N")[..8]);

    private string Path_ => System.IO.Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void StartsWithDefaultsWhenThereIsNoFile()
    {
        var settings = AppSettings.Load(Path_);

        Assert.Null(settings.WorkspacePath);
        Assert.Null(settings.RememberedWorkspacePath);
        Assert.Equal(ViewMode.List, settings.ViewMode);
    }

    [Fact]
    public void RoundTripsThroughTheFile()
    {
        var settings = AppSettings.Load(Path_);
        settings.SetWorkspacePath(_dir);
        settings.SetViewMode(ViewMode.Board);

        var reloaded = AppSettings.Load(Path_);

        Assert.Equal(_dir, reloaded.WorkspacePath);
        Assert.Equal(ViewMode.Board, reloaded.ViewMode);
    }

    [Fact]
    public void RemembersAPathThatNoLongerResolves()
    {
        // So the app can say "couldn't find your workspace at X" rather than
        // silently starting over — an unmounted drive, a moved OneDrive folder.
        var settings = AppSettings.Load(Path_);
        var gone = System.IO.Path.Combine(_dir, "not-here");
        settings.SetWorkspacePath(gone);

        var reloaded = AppSettings.Load(Path_);

        Assert.Null(reloaded.WorkspacePath);
        Assert.Equal(gone, reloaded.RememberedWorkspacePath);
    }

    [Fact]
    public void ForgettingTheWorkspaceClearsBoth()
    {
        var settings = AppSettings.Load(Path_);
        settings.SetWorkspacePath(_dir);

        settings.ForgetWorkspace();

        var reloaded = AppSettings.Load(Path_);
        Assert.Null(reloaded.RememberedWorkspacePath);
    }

    [Fact]
    public void ACorruptFileYieldsDefaultsRatherThanThrowing()
    {
        // Losing a remembered folder path is a small annoyance. Refusing to
        // start over it would not be.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, "{ this is not json");

        var settings = AppSettings.Load(Path_);

        Assert.Null(settings.RememberedWorkspacePath);
        Assert.Equal(ViewMode.List, settings.ViewMode);
    }

    [Fact]
    public void WritesTheViewModeAsAStableWireValue()
    {
        var settings = AppSettings.Load(Path_);
        settings.SetViewMode(ViewMode.Board);

        Assert.Contains("\"board\"", File.ReadAllText(Path_));
    }
}

public class AppleScriptQuotingTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("say \"hello\"", "say \\\"hello\\\"")]
    [InlineData("back\\slash", "back\\\\slash")]
    [InlineData("two\nlines", "two lines")]
    [InlineData("tab\there", "tab here")]
    public void EscapesWhatWouldBreakTheScript(string input, string expected) =>
        Assert.Equal(expected, AppleScript.Quote(input));

    [Fact]
    public void NeutralisesAnAttemptToAppendStatements()
    {
        // A task summary is arbitrary user text and is interpolated into a
        // script. Without escaping, this would close the string and run.
        const string hostile = "x\" \n do shell script \"touch /tmp/pwned\" \n display notification \"";

        var quoted = AppleScript.Quote(hostile);

        // Every quote that could close the literal is escaped, and the newlines
        // that would separate statements are gone.
        Assert.DoesNotContain('\n', quoted);
        foreach (var (c, i) in quoted.Select((c, i) => (c, i)))
        {
            if (c != '"') continue;
            Assert.True(i > 0 && quoted[i - 1] == '\\', "an unescaped quote survived");
        }
    }

    [Fact]
    public void LeavesOrdinaryUnicodeAlone() =>
        Assert.Equal("Café — 日本語 🎉", AppleScript.Quote("Café — 日本語 🎉"));
}

public class ReminderTextTests
{
    private static MightDoTask Task(string summary, string description = "", CalendarDate? due = null) =>
        MightDoTask.Create(summary, "status", Rank.First, description: description, dueDate: due);

    [Fact]
    public void PrefersTheDueDate() =>
        Assert.Equal("Due 2026-08-21",
            ReminderText.Body(Task("x", "some description", new CalendarDate(2026, 8, 21))));

    [Fact]
    public void FallsBackToTheFirstLineOfTheDescription() =>
        Assert.Equal("first line", ReminderText.Body(Task("x", "first line\nsecond line")));

    [Fact]
    public void FallsBackToABareWord() =>
        Assert.Equal("Reminder", ReminderText.Body(Task("x")));

    [Fact]
    public void IgnoresLeadingBlankLinesInTheDescription() =>
        Assert.Equal("actual content", ReminderText.Body(Task("x", "\n\nactual content")));
}

public class NotifierSelectionTests
{
    [Fact]
    public void PicksSomethingForThisPlatformWithoutThrowing()
    {
        var notifier = ReminderNotifiers.ForCurrentPlatform();

        Assert.NotNull(notifier);
        if (OperatingSystem.IsMacOS()) Assert.IsType<MacOsReminderNotifier>(notifier);
        else if (OperatingSystem.IsLinux()) Assert.IsType<LinuxReminderNotifier>(notifier);
    }
}

/// <summary>
/// The list of workspaces, and the per-workspace view state beside it.
/// </summary>
public class WorkspaceListTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "mightdo-workspaces-" + Guid.NewGuid().ToString("N")[..8]);

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private string Folder(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void RemembersSeveralWorkspacesAndWhichIsOpen()
    {
        var settings = AppSettings.Load(SettingsPath);
        var work = Folder("work");
        var home = Folder("home");

        settings.AddWorkspace(work);
        settings.AddWorkspace(home);

        var reloaded = AppSettings.Load(SettingsPath);

        Assert.Equal([work, home], reloaded.Workspaces.Select(w => w.Path));
        Assert.Equal(home, reloaded.CurrentWorkspace?.Path);
    }

    [Fact]
    public void NamesAWorkspaceAfterItsFolderUntilItIsRenamed()
    {
        var settings = AppSettings.Load(SettingsPath);
        var work = Folder("work");

        settings.AddWorkspace(work);
        Assert.Equal("work", settings.CurrentWorkspace?.Name);

        settings.RenameWorkspace(work, "  The day job  ");

        Assert.Equal("The day job", AppSettings.Load(SettingsPath).CurrentWorkspace?.Name);
    }

    [Fact]
    public void RenamingToNothingLeavesTheNameAlone()
    {
        // An empty name is a row in the switcher you cannot see or point at.
        var settings = AppSettings.Load(SettingsPath);
        var work = Folder("work");
        settings.AddWorkspace(work);

        settings.RenameWorkspace(work, "   ");

        Assert.Equal("work", settings.CurrentWorkspace?.Name);
    }

    [Fact]
    public void AddingAFolderAlreadyInTheListSwitchesToItInstead()
    {
        // Two rows for one folder cannot be told apart, and one of them would
        // hold a stale copy of the view state.
        var settings = AppSettings.Load(SettingsPath);
        var work = Folder("work");
        var home = Folder("home");

        settings.AddWorkspace(work, "The day job");
        settings.AddWorkspace(home);
        settings.AddWorkspace(work);

        Assert.Equal(2, settings.Workspaces.Count);
        Assert.Equal(work, settings.CurrentWorkspace?.Path);
        Assert.Equal("The day job", settings.CurrentWorkspace?.Name);
    }

    [Fact]
    public void ForgettingTheOpenWorkspaceLeavesNothingOpen()
    {
        // And not the neighbour: quietly loading a different set of tasks into
        // the window is a worse surprise than an empty one.
        var settings = AppSettings.Load(SettingsPath);
        var work = Folder("work");
        var home = Folder("home");
        settings.AddWorkspace(work);
        settings.AddWorkspace(home);

        settings.ForgetWorkspace(home);

        var reloaded = AppSettings.Load(SettingsPath);
        Assert.Equal([work], reloaded.Workspaces.Select(w => w.Path));
        Assert.Null(reloaded.CurrentWorkspace);
    }

    [Fact]
    public void ClosingAWorkspaceKeepsItInTheList()
    {
        var settings = AppSettings.Load(SettingsPath);
        var work = Folder("work");
        settings.AddWorkspace(work);

        settings.CloseWorkspace();

        var reloaded = AppSettings.Load(SettingsPath);
        Assert.Single(reloaded.Workspaces);
        Assert.Null(reloaded.CurrentWorkspace);
    }

    [Fact]
    public void AWorkspaceWhoseFolderHasGoneStaysInTheList()
    {
        // An unmounted drive, or a OneDrive folder that has not synced yet.
        var settings = AppSettings.Load(SettingsPath);
        var gone = Path.Combine(_dir, "not-here");
        settings.AddWorkspace(gone);

        var reloaded = AppSettings.Load(SettingsPath);

        Assert.Single(reloaded.Workspaces);
        Assert.False(reloaded.Workspaces[0].Exists);
        Assert.Equal(gone, reloaded.RememberedWorkspacePath);
        Assert.Null(reloaded.WorkspacePath);
    }

    [Fact]
    public void SwitchingToAFolderNobodyAddedIsRefused()
    {
        var settings = AppSettings.Load(SettingsPath);

        Assert.Throws<ArgumentException>(() => settings.SetCurrentWorkspace(Folder("work")));
    }

    // ---- view state ---------------------------------------------------------

    [Fact]
    public void EachWorkspaceKeepsItsOwnView()
    {
        var settings = AppSettings.Load(SettingsPath);
        var work = Folder("work");
        var home = Folder("home");
        settings.AddWorkspace(work);
        settings.AddWorkspace(home);

        settings.SaveViewState(work, new WorkspaceViewState
        {
            ViewMode = ViewMode.Board,
            Sort = "DueDate",
            Search = "invoice",
            OverdueOnly = true,
            TagIds = ["01hq"],
        });
        settings.SaveViewState(home, new WorkspaceViewState { ViewMode = ViewMode.List });

        var reloaded = AppSettings.Load(SettingsPath);

        var workView = reloaded.ViewStateFor(work);
        Assert.Equal(ViewMode.Board, workView.ViewMode);
        Assert.Equal("DueDate", workView.Sort);
        Assert.Equal("invoice", workView.Search);
        Assert.True(workView.OverdueOnly);
        Assert.Equal(["01hq"], workView.TagIds);

        Assert.Equal(ViewMode.List, reloaded.ViewStateFor(home).ViewMode);
        Assert.False(reloaded.ViewStateFor(home).HasAnyFilter);
    }

    [Fact]
    public void ANewWorkspaceStartsInTheViewLastUsedAnywhere()
    {
        // Adding a second workspace should not throw a board user back to the
        // list on their first look at it.
        var settings = AppSettings.Load(SettingsPath);
        var work = Folder("work");
        settings.AddWorkspace(work);
        settings.SaveViewState(work, new WorkspaceViewState { ViewMode = ViewMode.Board });

        var home = settings.AddWorkspace(Folder("home"));

        Assert.Equal(ViewMode.Board, home.View.ViewMode);
    }

    [Fact]
    public void ViewStateForAWorkspaceNobodyAddedIsADefault()
    {
        var settings = AppSettings.Load(SettingsPath);

        var state = settings.ViewStateFor(Folder("stranger"));

        Assert.Equal(ViewMode.List, state.ViewMode);
        Assert.False(state.HasAnyFilter);
    }

    // ---- migration ----------------------------------------------------------

    [Fact]
    public void MigratesTheSingleWorkspacePathOlderVersionsWrote()
    {
        // Upgrading must not drop someone at the "choose a folder" screen
        // having apparently forgotten everything they had.
        var work = Folder("work");
        File.WriteAllText(SettingsPath, $$"""
            {
              "workspacePath": {{System.Text.Json.JsonSerializer.Serialize(work)}},
              "viewMode": "board",
              "windowWidth": 900,
              "windowHeight": 600
            }
            """);

        var settings = AppSettings.Load(SettingsPath);

        var only = Assert.Single(settings.Workspaces);
        Assert.Equal(work, only.Path);
        Assert.Equal("work", only.Name);
        Assert.Equal(ViewMode.Board, only.View.ViewMode);
        Assert.Equal(work, settings.WorkspacePath);
        Assert.Equal(900, settings.WindowPlacement?.Width);
    }

    [Fact]
    public void TheMigratedPathIsNotWrittenBack()
    {
        var work = Folder("work");
        File.WriteAllText(SettingsPath, $$"""
            { "workspacePath": {{System.Text.Json.JsonSerializer.Serialize(work)}} }
            """);

        var settings = AppSettings.Load(SettingsPath);
        settings.RenameWorkspace(work, "The day job");

        var written = File.ReadAllText(SettingsPath);
        Assert.DoesNotContain("\"workspacePath\"", written);
        Assert.Contains("\"workspaces\"", written);
    }

    [Fact]
    public void AWorkspaceListAlreadyThereWinsOverTheOldKey()
    {
        var work = Folder("work");
        File.WriteAllText(SettingsPath, $$"""
            {
              "workspaces": [ { "path": {{System.Text.Json.JsonSerializer.Serialize(work)}}, "name": "Kept" } ],
              "currentWorkspacePath": {{System.Text.Json.JsonSerializer.Serialize(work)}},
              "workspacePath": "/somewhere/stale"
            }
            """);

        var settings = AppSettings.Load(SettingsPath);

        Assert.Single(settings.Workspaces);
        Assert.Equal("Kept", settings.CurrentWorkspace?.Name);
    }
}
