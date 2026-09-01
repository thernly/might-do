using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Controls;
using MightDo.App.ViewModels;
using MightDo.App.Views;
using MightDo.Core.Session;
using MightDo.Core.Storage;
using MightDo.Platform;

namespace MightDo.App.Tests;

/// <summary>
/// The design theme — the whole look — and the promise that swapping one is a
/// swap rather than an overlay.
/// </summary>
/// <remarks>
/// A theme is only swappable while every theme answers for every key the views
/// ask about. The moment one of them leaves a key out, the previous theme's
/// answer is what shows, and the bug appears in whichever screen happens to
/// read that key — never in the file that dropped it. So the contract is
/// checked here rather than discovered there.
/// </remarks>
public class DesignThemeTests : IDisposable
{
    /// <summary>
    /// Every resource the views and the shared styles read from a theme, in
    /// both of the schemes a theme has to provide.
    /// </summary>
    private static readonly string[] ThemedKeys =
    [
        "AppAccentBrush", "AppWindowBrush", "AppSurfaceBrush", "AppCardBrush",
        "AppChipBrush", "AppBorderBrush", "AppCardBorderBrush",
        "AppTextBrush", "AppTextSecondaryBrush",
        "AppInkBrush", "AppStrongBorderBrush", "AppHoverBrush", "AppActiveBrush",
        "AppFocusBrush",
        "AppDisabledBrush", "AppDisabledBorderBrush", "AppDisabledTextBrush",
        "OverdueBrush", "OnAccentBrush",
        "PriorityHighBackgroundBrush", "PriorityHighForegroundBrush",
        "PriorityCriticalBackgroundBrush", "PriorityCriticalForegroundBrush",
        "StatusInitialBrush", "StatusActiveBrush", "StatusFinalBrush",
    ];

    /// <summary>Resources a theme provides once, for both schemes together.</summary>
    private static readonly string[] SharedKeys =
        ["FontDisplay", "FontText", "FontMono", "ControlCornerRadius", "OverlayCornerRadius"];

    private static readonly DesignTheme[] Designs =
        [DesignTheme.Cyrk66, DesignTheme.SageSlate];

    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "mightdo-design-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose()
    {
        Theme.ApplyDesign(DesignTheme.Cyrk66);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact]
    public void EveryThemeAnswersForEveryKeyInBothSchemes()
    {
        foreach (var design in Designs)
        {
            Theme.ApplyDesign(design);

            foreach (var key in SharedKeys)
            {
                Assert.True(
                    Application.Current!.TryGetResource(key, null, out _),
                    $"{design} does not define {key}.");
            }

            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                foreach (var key in ThemedKeys)
                {
                    Assert.True(
                        Application.Current!.TryGetResource(key, variant, out var value),
                        $"{design} does not define {key} for {variant}.");
                    Assert.NotNull(value);
                }
            }
        }
    }

    [AvaloniaFact]
    public void SwappingReplacesTheThemeRatherThanStackingOnIt()
    {
        // The failure this prevents is silent: an added theme leaves the old
        // one underneath answering for anything the new one missed, and the
        // stack grows by one on every switch.
        var before = Application.Current!.Styles.Count;

        Theme.ApplyDesign(DesignTheme.SageSlate);
        Theme.ApplyDesign(DesignTheme.Cyrk66);
        Theme.ApplyDesign(DesignTheme.SageSlate);

        Assert.Equal(before, Application.Current.Styles.Count);
        Assert.Equal(
            Theme.SourceFor(DesignTheme.SageSlate),
            Assert.IsType<StyleInclude>(Application.Current.Styles[0]).Source);
    }

    [AvaloniaFact]
    public void TheTwoThemesAreActuallyDifferentLooks()
    {
        // Two entries in a settings page that paint the same thing would be a
        // convincing feature and no feature at all.
        Theme.ApplyDesign(DesignTheme.Cyrk66);
        var cyrk = Colour("AppWindowBrush", ThemeVariant.Dark);
        var cyrkCorner = Corner();

        Theme.ApplyDesign(DesignTheme.SageSlate);

        Assert.NotEqual(cyrk, Colour("AppWindowBrush", ThemeVariant.Dark));
        Assert.NotEqual(cyrkCorner, Corner());
    }

    [AvaloniaFact]
    public void TheSchemeSurvivesAChangeOfTheme()
    {
        // The two choices are orthogonal, and the way that goes wrong is that
        // picking a theme quietly drops you back to whatever the OS is doing.
        Theme.Apply(ThemePreference.Dark);
        Theme.ApplyDesign(DesignTheme.SageSlate);

        Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);

        Theme.ApplyDesign(DesignTheme.Cyrk66);

        Assert.Equal(ThemeVariant.Dark, Application.Current.RequestedThemeVariant);
    }

    [AvaloniaFact]
    public void TheChoiceIsRememberedAndDefaultsToCyrk()
    {
        Assert.Equal(DesignTheme.Cyrk66, new AppSettingsData().Design);

        var file = Path.Combine(_root, "settings.json");
        AppSettings.Load(file).SetDesign(DesignTheme.SageSlate);

        Assert.Equal(DesignTheme.SageSlate, AppSettings.Load(file).Design);
    }

    [AvaloniaFact]
    public async Task TheSettingsPageMarksTheThemeInUseAndRepaints()
    {
        using var session = await WorkspaceSession.OpenAsync(
            new TaskStore(new Core.Storage.Workspace(
                Directory.CreateDirectory(Path.Combine(_root, "ws")).FullName)));

        using var viewModel = new SettingsViewModel(
            session, AppSettings.Load(Path.Combine(_root, "settings.json")));

        var window = new SettingsWindow { DataContext = viewModel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var radios = window.GetVisualDescendants().OfType<RadioButton>()
            .Where(radio => radio.GroupName == "Design")
            .ToList();

        Assert.Equal(2, radios.Count);
        Assert.Equal(
            "Cyrk 66", Assert.Single(radios, radio => radio.IsChecked == true).Content);

        viewModel.SetDesignCommand.Execute(DesignTheme.SageSlate);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            "Sage & Slate",
            Assert.Single(radios, radio => radio.IsChecked == true).Content);

        // And the application is actually wearing it, not merely recording it.
        Assert.Equal(
            Theme.SourceFor(DesignTheme.SageSlate),
            Assert.IsType<StyleInclude>(Application.Current!.Styles[0]).Source);

        window.Close();
    }

    private static Color Colour(string key, ThemeVariant variant)
    {
        Assert.True(Application.Current!.TryGetResource(key, variant, out var value));
        return ((ISolidColorBrush)value!).Color;
    }

    private static CornerRadius Corner()
    {
        Assert.True(Application.Current!.TryGetResource("ControlCornerRadius", null, out var value));
        return (CornerRadius)value!;
    }
}
