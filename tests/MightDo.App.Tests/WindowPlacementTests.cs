using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MightDo.App.ViewModels;
using MightDo.App.Views;
using MightDo.Platform;

namespace MightDo.App.Tests;

/// <summary>
/// The main window reopens at the size it was left.
/// </summary>
public class WindowPlacementTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mightdo-window-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string SettingsPath => Path.Combine(_root, "settings.json");

    private AppSettings Settings() => AppSettings.Load(SettingsPath);

    private MainWindow WindowWith(AppSettings settings) =>
        new() { DataContext = new MainViewModel(settings, new NoPicker(), new NoPicker()) };

    // ---- remembering -------------------------------------------------------

    [AvaloniaFact]
    public void ClosingRecordsTheSize()
    {
        var window = WindowWith(Settings());
        window.Show();

        window.Width = 1440;
        window.Height = 900;
        Dispatcher.UIThread.RunJobs();

        window.Close();

        // Reloaded from disk rather than read back off the same object, because
        // reopening is a different process reading the file.
        var placement = Settings().WindowPlacement;
        Assert.NotNull(placement);
        Assert.Equal(1440, placement.Width);
        Assert.Equal(900, placement.Height);
        Assert.False(placement.Maximized);
    }

    [AvaloniaFact]
    public void MaximizingIsRecorded()
    {
        var window = WindowWith(Settings());
        window.Show();

        window.WindowState = WindowState.Maximized;
        Dispatcher.UIThread.RunJobs();
        window.Close();

        Assert.True(Settings().WindowPlacement?.Maximized);
    }

    // ---- what to come back to ----------------------------------------------
    //
    // A headless window reports the same size maximized as it did before, so
    // driving one cannot tell a right answer from a wrong one here. These go
    // at WindowSizeMemory directly, which is why it is a separate class.

    [Fact]
    public void TheSizeToComeBackToIsTheLastOrdinaryOne()
    {
        // Not the size of the screen it filled: unmaximizing has to land
        // somewhere, and that somewhere is where the user last put it.
        var memory = new WindowSizeMemory();

        memory.Resized(new Size(1200, 800), WindowState.Normal);
        memory.Resized(new Size(3440, 1440), WindowState.Maximized);

        var placement = memory.Placement(new Size(3440, 1440), WindowState.Maximized);

        Assert.Equal(1200, placement.Width);
        Assert.Equal(800, placement.Height);
        Assert.True(placement.Maximized);
    }

    [Fact]
    public void AWindowOpenedMaximizedCarriesItsRememberedSizeThrough()
    {
        // Opened maximized and closed maximized, it is never seen at an
        // ordinary size, so the only thing it can come back to is what the
        // settings already held.
        var memory = new WindowSizeMemory();

        memory.Remembered(new Size(1280, 860));
        memory.Resized(new Size(3440, 1440), WindowState.Maximized);

        var placement = memory.Placement(new Size(3440, 1440), WindowState.Maximized);

        Assert.Equal(1280, placement.Width);
        Assert.Equal(860, placement.Height);
    }

    [Fact]
    public void AWindowThatWasNeverResizedFallsBackToItsCurrentSize()
    {
        var memory = new WindowSizeMemory();

        var placement = memory.Placement(new Size(1000, 700), WindowState.Normal);

        Assert.Equal(1000, placement.Width);
        Assert.Equal(700, placement.Height);
        Assert.False(placement.Maximized);
    }

    [Fact]
    public void MinimizingDoesNotCountAsASize()
    {
        // A minimized window can report a size, and it is not one to reopen at.
        var memory = new WindowSizeMemory();

        memory.Resized(new Size(1200, 800), WindowState.Normal);
        memory.Resized(new Size(160, 40), WindowState.Minimized);

        Assert.Equal(1200, memory.Placement(new Size(160, 40), WindowState.Minimized).Width);
    }

    // ---- restoring ---------------------------------------------------------

    [AvaloniaFact]
    public void OpeningUsesTheRememberedSize()
    {
        var settings = Settings();
        settings.SetWindowPlacement(new WindowPlacement(1280, 860, Maximized: false));

        var window = WindowWith(Settings());

        // Before Show: the size must be right the first time it is painted,
        // never applied as a visible jump afterwards.
        Assert.Equal(1280, window.Width);
        Assert.Equal(860, window.Height);

        window.Show();
        Assert.Equal(WindowState.Normal, window.WindowState);
    }

    [AvaloniaFact]
    public void OpeningRestoresBeingMaximized()
    {
        var settings = Settings();
        settings.SetWindowPlacement(new WindowPlacement(1280, 860, Maximized: true));

        var window = WindowWith(Settings());
        window.Show();

        Assert.Equal(WindowState.Maximized, window.WindowState);
    }

    [AvaloniaFact]
    public void NothingRememberedLeavesTheWindowsOwnDefault()
    {
        var window = WindowWith(Settings());

        Assert.Equal(1000, window.Width);
        Assert.Equal(700, window.Height);
    }

    // ---- sizes that are not sizes ------------------------------------------

    [AvaloniaFact]
    public void ARememberedSizeSmallerThanTheWindowAllowsIsRaisedToTheMinimum()
    {
        var settings = Settings();
        settings.SetWindowPlacement(new WindowPlacement(80, 40, Maximized: false));

        var window = WindowWith(Settings());

        Assert.Equal(window.MinWidth, window.Width);
        Assert.Equal(window.MinHeight, window.Height);
    }

    [AvaloniaFact]
    public void ARememberedSizeLargerThanTheScreenIsBroughtBackOntoIt()
    {
        // Sized on a desk monitor, opened on an undocked laptop.
        var settings = Settings();
        settings.SetWindowPlacement(new WindowPlacement(99_000, 99_000, Maximized: false));

        var window = WindowWith(Settings());
        var screen = window.Screens.Primary;
        Assert.NotNull(screen);

        Assert.True(
            window.Width <= screen.WorkingArea.Width / screen.Scaling,
            $"{window.Width} is wider than the screen's {screen.WorkingArea.Width} px");
        Assert.True(
            window.Height <= screen.WorkingArea.Height / screen.Scaling,
            $"{window.Height} is taller than the screen's {screen.WorkingArea.Height} px");
    }

    [AvaloniaFact]
    public void ANonsenseSizeInTheFileIsIgnoredRatherThanApplied()
    {
        // Hand-edited, or half-written by a machine that lost power mid-save.
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            SettingsPath,
            """{"windowWidth": -1, "windowHeight": "NaN", "windowMaximized": true}""");

        var settings = Settings();

        Assert.Null(settings.WindowPlacement);

        var window = WindowWith(settings);
        Assert.Equal(1000, window.Width);
        Assert.Equal(WindowState.Normal, window.WindowState);
    }

    private sealed class NoPicker : IFolderPicker, IFilePicker
    {
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title) => Task.FromResult<string?>(null);
    }
}
