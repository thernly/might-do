using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform;
using Avalonia.Styling;
using MightDo.Platform;

namespace MightDo.App;

/// <summary>
/// Turns the stored colour-scheme preference into the application's theme, and
/// keeps Auto following the operating system.
/// </summary>
/// <remarks>
/// The obvious implementation of Auto is <see cref="ThemeVariant.Default"/>,
/// which is Avalonia's "ask the platform". It works exactly once: setting it
/// after any explicit variant leaves <c>ActualThemeVariant</c> empty, no theme
/// dictionary matches, and every brush in the application resolves to nothing —
/// a white window with black text. So Auto is resolved to the real variant here
/// and re-resolved whenever the OS changes its mind, which follows the system
/// just as closely and survives being switched away from and back.
/// </remarks>
public static class Theme
{
    private static readonly object Gate = new();
    private static ThemePreference _preference = ThemePreference.Auto;
    private static bool _following;

    /// <summary>What a preference means right now, given what the OS is doing.</summary>
    public static ThemeVariant Resolve(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => ThemeVariant.Light,
        ThemePreference.Dark => ThemeVariant.Dark,
        _ => SystemVariant(),
    };

    /// <summary>
    /// Applies the preference, if there is an application to apply it to.
    /// </summary>
    public static void Apply(ThemePreference preference)
    {
        lock (Gate) _preference = preference;

        if (Application.Current is not { } application) return;

        Follow(application);
        application.RequestedThemeVariant = Resolve(preference);
    }

    /// <summary>Where each design theme's one file lives.</summary>
    /// <remarks>
    /// A theme is a whole look — palette, geometry, type and Fluent's own
    /// control colours — held in a single <c>Styles</c> file, so applying one is
    /// a single assignment and there is no state in which half of the previous
    /// theme is still on screen.
    /// </remarks>
    public static Uri SourceFor(DesignTheme design) => new(design switch
    {
        DesignTheme.SageSlate => "avares://MightDo.App/Themes/SageSlate.axaml",
        _ => "avares://MightDo.App/Themes/Cyrk66.axaml",
    });

    /// <summary>
    /// Wears the given design theme, if there is an application to wear it.
    /// </summary>
    /// <remarks>
    /// The theme replaces the application's single style entry rather than
    /// being added to it. Adding would leave the old theme underneath, still
    /// answering for every key the new one happens not to define — and the
    /// stack would grow by one on each switch, so the tenth choice would be
    /// painted over nine abandoned ones.
    /// <para>
    /// Everything the views read is a <c>DynamicResource</c>, which is what
    /// makes the swap visible immediately rather than at the next restart.
    /// </para>
    /// </remarks>
    public static void ApplyDesign(DesignTheme design)
    {
        if (Application.Current is not { } application) return;

        var include = new StyleInclude(BaseUri) { Source = SourceFor(design) };

        if (application.Styles.Count == 0) application.Styles.Add(include);
        else application.Styles[0] = include;
    }

    /// <summary>
    /// The root every theme URI is resolved against. The sources above are
    /// absolute <c>avares://</c> URIs, so this only has to be a valid base.
    /// </summary>
    private static readonly Uri BaseUri = new("avares://MightDo.App/");

    /// <summary>Whichever scheme the operating system is in, defaulting to light.</summary>
    private static ThemeVariant SystemVariant() =>
        Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant
            == PlatformThemeVariant.Dark
            ? ThemeVariant.Dark
            : ThemeVariant.Light;

    /// <summary>
    /// Subscribes once, so a machine that goes dark at sunset takes the app
    /// with it without the window being reopened.
    /// </summary>
    private static void Follow(Application application)
    {
        lock (Gate)
        {
            if (_following || application.PlatformSettings is not { } platform) return;
            _following = true;

            platform.ColorValuesChanged += (_, _) =>
            {
                ThemePreference preference;
                lock (Gate) preference = _preference;

                if (preference != ThemePreference.Auto) return;

                application.RequestedThemeVariant = SystemVariant();
            };
        }
    }
}
