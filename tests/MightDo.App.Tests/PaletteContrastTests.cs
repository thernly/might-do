using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace MightDo.App.Tests;

/// <summary>
/// The palette's legibility, as arithmetic rather than as a judgement.
/// </summary>
/// <remarks>
/// The tokens are picked by eye against a mock-up, and the next person to pick
/// one is picking it against a different mock-up. These pin the pairs that
/// actually meet on screen, so a colour that looks fine in isolation cannot be
/// swapped in over a background it cannot be read on.
/// </remarks>
public class PaletteContrastTests
{
    /// <summary>WCAG AA for body text.</summary>
    private const double Readable = 4.5;

    /// <summary>WCAG AA for shapes that carry meaning without carrying words.</summary>
    private const double Distinguishable = 3.0;

    public static TheoryData<string, string, string> TextOnSurfaces() =>
        new()
        {
            { "AppTextBrush", "AppWindowBrush", "body text on the window" },
            { "AppTextBrush", "AppSurfaceBrush", "body text on a panel" },
            { "AppTextBrush", "AppCardBrush", "body text on a card" },
            { "AppTextBrush", "AppChipBrush", "body text on a chip" },
            { "AppTextSecondaryBrush", "AppWindowBrush", "hints on the window" },
            { "AppTextSecondaryBrush", "AppSurfaceBrush", "hints on a panel" },
            { "AppTextSecondaryBrush", "AppChipBrush", "hints on a chip" },
            { "OverdueBrush", "AppWindowBrush", "an overdue date in the list" },
            { "OverdueBrush", "AppCardBrush", "an overdue date on a card" },
            { "PriorityHighForegroundBrush", "PriorityHighBackgroundBrush", "the High chip" },
            {
                "PriorityCriticalForegroundBrush", "PriorityCriticalBackgroundBrush",
                "the Critical chip"
            },
        };

    [AvaloniaTheory]
    [MemberData(nameof(TextOnSurfaces))]
    public void TextIsReadableInBothVariants(string foreground, string background, string what)
    {
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var ratio = Contrast(Colour(foreground, variant), Colour(background, variant));

            Assert.True(
                ratio >= Readable,
                $"{what} is {ratio:F2}:1 in {variant}, under the {Readable}:1 it needs.");
        }
    }

    [AvaloniaTheory]
    [InlineData("StatusInitialBrush")]
    [InlineData("StatusActiveBrush")]
    [InlineData("StatusFinalBrush")]
    public void StatusDotsStandOffTheirColumnHeader(string dot)
    {
        // The dots are 8px and wordless, so they only have to be seen, not
        // read — but a dot that vanishes into the header says nothing at all.
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var ratio = Contrast(Colour(dot, variant), Colour("AppSurfaceBrush", variant));

            Assert.True(
                ratio >= Distinguishable,
                $"{dot} is {ratio:F2}:1 in {variant}, under the {Distinguishable}:1 it needs.");
        }
    }

    [AvaloniaFact]
    public void TextOnTheAccentIsReadableOnTheAccent()
    {
        // OnAccentBrush exists because the accent flips lightness between the
        // variants: white over light-mode slate, near-black over the dark-mode
        // one. Getting the pair the wrong way round is invisible in code.
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Assert.True(Application.Current!.TryGetResource(
                "SystemAccentColor", variant, out var accent));

            var ratio = Contrast(Colour("OnAccentBrush", variant), (Color)accent!);

            Assert.True(
                ratio >= Readable,
                $"accent text is {ratio:F2}:1 in {variant}, under the {Readable}:1 it needs.");
        }
    }

    [AvaloniaFact]
    public void TheAccentBrushIsTheAccentColour()
    {
        // Two spellings of one colour: Fluent's templates read the Color, the
        // views paint the Brush. Nothing keeps them in step but this.
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Assert.True(Application.Current!.TryGetResource(
                "SystemAccentColor", variant, out var accent));

            Assert.Equal((Color)accent!, Colour("AppAccentBrush", variant));
        }
    }

    [AvaloniaFact]
    public void FluentTakesTheSameAccentTheViewsDo()
    {
        // Fluent's palette is the third place the accent is written down, and
        // the only one nothing else reads — let it drift and the Add button
        // and the view radios go a different blue from everything around them.
        var palettes = Application.Current!.Styles.OfType<FluentTheme>().Single().Palettes;

        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Assert.Equal(Colour("AppAccentBrush", variant), palettes[variant].Accent);
        }
    }

    private static Color Colour(string key, ThemeVariant variant)
    {
        Assert.True(
            Application.Current!.TryGetResource(key, variant, out var value),
            $"{key} is not defined for {variant}.");

        return ((ISolidColorBrush)value!).Color;
    }

    /// <summary>The WCAG 2.x contrast ratio, which runs from 1:1 to 21:1.</summary>
    private static double Contrast(Color a, Color b)
    {
        var (high, low) = (Math.Max(Luminance(a), Luminance(b)),
                           Math.Min(Luminance(a), Luminance(b)));

        return (high + 0.05) / (low + 0.05);
    }

    private static double Luminance(Color colour) =>
        (0.2126 * Channel(colour.R)) + (0.7152 * Channel(colour.G)) + (0.0722 * Channel(colour.B));

    private static double Channel(byte value)
    {
        var c = value / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
