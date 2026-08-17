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
