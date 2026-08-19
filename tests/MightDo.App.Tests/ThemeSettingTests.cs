using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MightDo.App.ViewModels;
using MightDo.App.Views;
using MightDo.Core.Session;
using MightDo.Core.Storage;
using MightDo.Platform;

namespace MightDo.App.Tests;

/// <summary>
/// Choosing light, dark, or whatever the operating system is doing.
/// </summary>
/// <remarks>
/// Three things have to line up and only one of them is visible: the choice is
/// applied to the running application, it survives a restart, and Auto stays
/// Avalonia's Default rather than being resolved to a variant at the moment it
/// was picked — which would freeze the app in whichever scheme the machine was
/// in that evening.
/// </remarks>
public class ThemeSettingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mightdo-theme-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly List<IDisposable> _disposables = [];
    private readonly ThemeVariant? _original = Application.Current?.RequestedThemeVariant;

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        if (Application.Current is { } app) app.RequestedThemeVariant = _original;
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact]
    public void AutoIsWhatAnUnansweredSettingsFileMeans()
    {
        Assert.Equal(ThemePreference.Auto, new AppSettingsData().Theme);
        Assert.Equal(ThemeVariant.Light, Theme.Resolve(ThemePreference.Light));
        Assert.Equal(ThemeVariant.Dark, Theme.Resolve(ThemePreference.Dark));
    }

    [AvaloniaFact]
    public void AutoTakesTheSchemeTheOperatingSystemIsIn()
    {
        var system = Application.Current!.PlatformSettings!.GetColorValues().ThemeVariant;

        Assert.Equal(
            system == PlatformThemeVariant.Dark ? ThemeVariant.Dark : ThemeVariant.Light,
            Theme.Resolve(ThemePreference.Auto));
    }

    [AvaloniaFact]
    public async Task AutoStillResolvesAfterAnExplicitSchemeHasBeenUsed()
    {
        // Auto is not ThemeVariant.Default, which is the obvious way to write
        // it and works exactly once: set after any explicit variant it leaves
        // ActualThemeVariant empty, no dictionary matches, and every brush in
        // the app resolves to nothing. This is that regression, in one test.
        var (viewModel, _) = await OpenAsync();

        viewModel.SetThemeCommand.Execute(ThemePreference.Dark);
        viewModel.SetThemeCommand.Execute(ThemePreference.Auto);

        var app = Application.Current!;
        Assert.NotEqual(ThemeVariant.Default, app.ActualThemeVariant);
        Assert.True(
            app.TryGetResource("AppWindowBrush", app.ActualThemeVariant, out var brush),
            "the palette stopped resolving, so the window is painting stock white.");
        Assert.NotNull(brush);
    }

    [AvaloniaTheory]
    [InlineData(ThemePreference.Light)]
    [InlineData(ThemePreference.Dark)]
    [InlineData(ThemePreference.Auto)]
    public async Task ChoosingASchemeRepaintsTheApplication(ThemePreference choice)
    {
        var (viewModel, _) = await OpenAsync();

        // Away from the choice first, so passing cannot mean "nothing happened".
        viewModel.SetThemeCommand.Execute(
            choice == ThemePreference.Dark ? ThemePreference.Light : ThemePreference.Dark);
        viewModel.SetThemeCommand.Execute(choice);

        Assert.Equal(Theme.Resolve(choice), Application.Current!.RequestedThemeVariant);
    }

    [AvaloniaFact]
    public async Task TheChoiceOutlivesTheApplication()
    {
        var (viewModel, path) = await OpenAsync();

        viewModel.SetThemeCommand.Execute(ThemePreference.Dark);

        Assert.Equal(ThemePreference.Dark, AppSettings.Load(path).Theme);
    }

    [AvaloniaFact]
    public async Task TheChoiceIsMachineLocalRatherThanPartOfTheWorkspace()
    {
        // The workspace folder syncs between machines; the scheme must not go
        // with it, or a laptop set to dark drags the desktop dark too.
        var (viewModel, _) = await OpenAsync();

        viewModel.SetThemeCommand.Execute(ThemePreference.Dark);

        var workspaceFiles = Directory.EnumerateFiles(
            Path.Combine(_root, "ws"), "*", SearchOption.AllDirectories);

        Assert.DoesNotContain(
            workspaceFiles, file => File.ReadAllText(file).Contains("dark", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task TheSettingsPageMarksTheSchemeInUse()
    {
        var (viewModel, _) = await OpenAsync();
        viewModel.SetThemeCommand.Execute(ThemePreference.Dark);

        var window = new SettingsWindow { DataContext = viewModel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.Measure(window.ClientSize);
        window.Arrange(new Rect(window.ClientSize));

        var radios = window.GetVisualDescendants().OfType<RadioButton>()
            .Where(radio => radio.GroupName == "Theme")
            .ToList();

        Assert.Equal(3, radios.Count);
        Assert.Equal(
            "Dark", Assert.Single(radios, radio => radio.IsChecked == true).Content);

        // And the marking follows a later change rather than being set once.
        viewModel.SetThemeCommand.Execute(ThemePreference.Auto);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            "Auto", Assert.Single(radios, radio => radio.IsChecked == true).Content);

        window.Close();
    }

    private async Task<(SettingsViewModel ViewModel, string SettingsPath)> OpenAsync()
    {
        var path = Path.Combine(_root, "settings.json");
        var session = await WorkspaceSession.OpenAsync(
            new TaskStore(new Core.Storage.Workspace(Path.Combine(_root, "ws"))));
        _disposables.Add(session);

        var viewModel = new SettingsViewModel(session, AppSettings.Load(path));
        _disposables.Add(viewModel);

        return (viewModel, path);
    }
}
