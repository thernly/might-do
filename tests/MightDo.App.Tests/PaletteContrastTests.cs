using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

using MightDo.Platform;

namespace MightDo.App.Tests;

/// <summary>
/// The palette's legibility, as arithmetic rather than as a judgement.
/// </summary>
/// <remarks>
/// The tokens are picked by eye against a mock-up, and the next person to pick
/// one is picking it against a different mock-up. These pin the pairs that
/// actually meet on screen, so a colour that looks fine in isolation cannot be
/// swapped in over a background it cannot be read on.
/// <para>
/// Every check runs against every design theme. Legibility is a property of the
/// application, not of whichever look happens to be the default — a theme that
/// cannot be read is not a theme the user may choose.
/// </para>
/// </remarks>
public class PaletteContrastTests : IDisposable
{
    /// <summary>Every theme a user may choose, all of which must pass.</summary>
    private static readonly DesignTheme[] Designs =
        [DesignTheme.Cyrk66, DesignTheme.SageSlate];

    /// <summary>
    /// Puts back the default theme, because the headless application outlives
    /// the test that changed it and the next test would otherwise be checking
    /// whichever palette this one stopped on.
    /// </summary>
    public void Dispose()
    {
        Theme.ApplyDesign(DesignTheme.Cyrk66);
        GC.SuppressFinalize(this);
    }

    /// <summary>Puts the application in one theme so its palette can be read.</summary>
    private static void Wearing(DesignTheme design) => Theme.ApplyDesign(design);

    /// <summary>WCAG AA for body text.</summary>
    private const double Readable = 4.5;

    /// <summary>WCAG AA for shapes that carry meaning without carrying words.</summary>
    private const double Distinguishable = 3.0;

    /// <summary>
    /// CIE76 distance at which two tints stop being one colour someone picked
    /// twice. Roughly ten just-noticeable differences — comfortably past the
    /// threshold of "is that the same?" without demanding four loud chips.
    /// </summary>
    private const double Separable = 10.0;

    /// <summary>
    /// The smaller distance a tint needs from the plain chip ground. Lower than
    /// <see cref="Separable"/> on purpose: a chip only has to look deliberately
    /// tinted, and the quiet end of the scale is meant to stay quiet.
    /// </summary>
    private const double Noticeable = 5.0;

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
            {
                "PriorityCriticalForegroundBrush", "PriorityCriticalBackgroundBrush",
                "the Critical chip"
            },
            { "PriorityHighForegroundBrush", "PriorityHighBackgroundBrush", "the High chip" },
            { "PriorityMediumForegroundBrush", "PriorityMediumBackgroundBrush", "the Medium chip" },
            { "PriorityLowForegroundBrush", "PriorityLowBackgroundBrush", "the Low chip" },
        };

    [AvaloniaTheory]
    [MemberData(nameof(TextOnSurfaces))]
    public void TextIsReadableInBothVariants(string foreground, string background, string what)
    {
        foreach (var design in Designs)
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Wearing(design);

            var ratio = Contrast(Colour(foreground, variant), Colour(background, variant));

            Assert.True(
                ratio >= Readable,
                $"{what} is {ratio:F2}:1 in {design} {variant}, under the {Readable}:1 it needs.");
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
        foreach (var design in Designs)
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Wearing(design);

            var ratio = Contrast(Colour(dot, variant), Colour("AppSurfaceBrush", variant));

            Assert.True(
                ratio >= Distinguishable,
                $"{dot} is {ratio:F2}:1 in {design} {variant}, under the {Distinguishable}:1 it needs.");
        }
    }

    /// <summary>The four priority levels, quietest first.</summary>
    private static readonly string[] Priorities = ["Low", "Medium", "High", "Critical"];

    [AvaloniaFact]
    public void EveryPriorityChipIsADifferentColourFromEveryOther()
    {
        // A four-step scale is only a scale if you can tell the steps apart at
        // chip size, across a row, without putting two of them side by side.
        // Contrast is the wrong measure here — two pale tints of different hues
        // can sit at 1.1:1 and still be obviously different colours — so this
        // asks the perceptual distance instead.
        foreach (var design in Designs)
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Wearing(design);

            for (var i = 0; i < Priorities.Length; i++)
            for (var j = i + 1; j < Priorities.Length; j++)
            {
                var (a, b) = (Priorities[i], Priorities[j]);

                var apart = Distance(
                    Colour($"Priority{a}BackgroundBrush", variant),
                    Colour($"Priority{b}BackgroundBrush", variant));

                Assert.True(
                    apart >= Separable,
                    $"the {a} and {b} chips are {apart:F1} apart in {design} {variant}, "
                    + $"under the {Separable} two tints need to read as two tints.");
            }
        }
    }

    [AvaloniaTheory]
    [InlineData("Low")]
    [InlineData("Medium")]
    [InlineData("High")]
    [InlineData("Critical")]
    public void EveryPriorityChipIsADifferentColourFromAPlainOne(string priority)
    {
        // Priority is the one chip that carries a judgement rather than a fact,
        // and it sits in a row of chips that carry facts — a status, a category,
        // a step count. If it is painted the plain chip ground it is not saying
        // anything the row is not already saying. Medium was, and Low was that
        // ground at 70% opacity, which reads as switched off.
        foreach (var design in Designs)
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Wearing(design);

            var apart = Distance(
                Colour($"Priority{priority}BackgroundBrush", variant),
                Colour("AppChipBrush", variant));

            Assert.True(
                apart >= Noticeable,
                $"the {priority} chip is {apart:F1} from a plain chip in {design} {variant}, "
                + "which is close enough to look like one.");
        }
    }

    [AvaloniaFact]
    public void TextOnTheAccentIsReadableOnTheAccent()
    {
        // OnAccentBrush exists because the accent flips lightness between the
        // variants: white over light-mode slate, near-black over the dark-mode
        // one. Getting the pair the wrong way round is invisible in code.
        foreach (var design in Designs)
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Wearing(design);

            Assert.True(Application.Current!.TryGetResource(
                "SystemAccentColor", variant, out var accent));

            var ratio = Contrast(Colour("OnAccentBrush", variant), (Color)accent!);

            Assert.True(
                ratio >= Readable,
                $"accent text is {ratio:F2}:1 in {design} {variant}, under the {Readable}:1 it needs.");
        }
    }

    [AvaloniaFact]
    public void TheAccentBrushIsTheAccentColour()
    {
        // Two spellings of one colour: Fluent's templates read the Color, the
        // views paint the Brush. Nothing keeps them in step but this.
        foreach (var design in Designs)
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Wearing(design);

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
        foreach (var design in Designs)
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Wearing(design);

            // Read after the swap, not before: Fluent belongs to the theme now
            // being worn, and a palette fetched above the loop would be the
            // previous theme's answered against this theme's brushes.
            var palettes = Fluent().Palettes;

            Assert.Equal(Colour("AppAccentBrush", variant), palettes[variant].Accent);
        }
    }

    public static TheoryData<string> PaletteTokens() =>
        new()
        {
            "AppWindowBrush", "AppSurfaceBrush", "AppCardBrush", "AppChipBrush",
            "AppBorderBrush", "AppTextBrush", "AppTextSecondaryBrush", "AppAccentBrush",
            "OverdueBrush", "PriorityHighBackgroundBrush", "PriorityHighForegroundBrush",
            "PriorityCriticalBackgroundBrush", "PriorityCriticalForegroundBrush",
            "PriorityMediumBackgroundBrush", "PriorityMediumForegroundBrush",
            "PriorityLowBackgroundBrush", "PriorityLowForegroundBrush",
            "StatusInitialBrush", "StatusActiveBrush", "StatusFinalBrush",
        };

    [AvaloniaTheory]
    [MemberData(nameof(PaletteTokens))]
    public void NothingLiveIsGrey(string token)
    {
        // Grey is the disabled signal, so nothing that paints a live control
        // may be neutral. The palette is warm throughout and the narrowest
        // token still spreads six, so a stock #F5F5F5 wandering back in fails
        // here rather than three screens away.
        foreach (var design in Designs)
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Wearing(design);

            Assert.True(
                Spread(Colour(token, variant)) >= 4,
                $"{token} is neutral in {design} {variant}, and grey means disabled.");
        }
    }

    [AvaloniaTheory]
    [InlineData("AppDisabledBrush")]
    [InlineData("AppDisabledBorderBrush")]
    [InlineData("AppDisabledTextBrush")]
    public void OnlyTheDisabledTokensAreGrey(string token)
    {
        foreach (var design in Designs)
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Wearing(design);

            Assert.True(
                Spread(Colour(token, variant)) == 0,
                $"{token} carries a tint in {design} {variant}, which blunts the one cue for off.");
        }
    }

    [AvaloniaFact]
    public void FluentsControlFillsAreTintedToo()
    {
        // The fill behind every stock button. Fluent's default is pure black
        // or white at low alpha, and a neutral overlay averages away whatever
        // tint is under it — which is how the toolbar came out grey while
        // every token above was warm.
        foreach (var design in Designs)
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Wearing(design);

            // Read after the swap, not before: Fluent belongs to the theme now
            // being worn, and a palette fetched above the loop would be the
            // previous theme's answered against this theme's brushes.
            var palettes = Fluent().Palettes;

            var fill = palettes[variant].BaseLow;

            Assert.True(fill.A == 255, $"the {design} {variant} control fill is translucent, so it will "
                + "take its colour from whatever sits behind it rather than from the palette.");
            Assert.True(Spread(fill) >= 4, $"the {design} {variant} control fill is neutral.");
        }
    }

    /// <summary>
    /// The one FluentTheme in the application, wherever the design theme
    /// currently being worn happens to keep it.
    /// </summary>
    /// <remarks>
    /// Fluent is no longer a direct child of <c>Application.Styles</c>: each
    /// design theme carries its own palette-bearing FluentTheme inside its own
    /// file, and the application holds a single StyleInclude pointing at
    /// whichever theme is on. So the search has to open the includes.
    /// </remarks>
    private static FluentTheme Fluent() =>
        Flatten(Application.Current!.Styles).OfType<FluentTheme>().Single();

    private static IEnumerable<IStyle> Flatten(IEnumerable<IStyle> styles)
    {
        foreach (var style in styles)
        {
            var loaded = style is StyleInclude include ? include.Loaded : style;

            yield return loaded;

            if (loaded is Styles nested)
            {
                foreach (var child in Flatten(nested)) yield return child;
            }
        }
    }

    /// <summary>How far a colour sits from neutral, where zero is pure grey.</summary>
    private static int Spread(Color colour) =>
        Math.Max(colour.R, Math.Max(colour.G, colour.B))
        - Math.Min(colour.R, Math.Min(colour.G, colour.B));

    private static Color Colour(string key, ThemeVariant variant)
    {
        Assert.True(
            Application.Current!.TryGetResource(key, variant, out var value),
            $"{key} is not defined for {variant} in the theme now being worn.");

        return ((ISolidColorBrush)value!).Color;
    }

    /// <summary>The WCAG 2.x contrast ratio, which runs from 1:1 to 21:1.</summary>
    private static double Contrast(Color a, Color b)
    {
        var (high, low) = (Math.Max(Luminance(a), Luminance(b)),
                           Math.Min(Luminance(a), Luminance(b)));

        return (high + 0.05) / (low + 0.05);
    }

    /// <summary>
    /// Perceptual distance between two colours, as CIE76 in L*a*b*.
    /// </summary>
    /// <remarks>
    /// Contrast ratio answers "can this be read on that", which is a question
    /// about lightness alone — it scores two equally light tints of different
    /// hues at about 1:1 whether they are the same colour or opposites on the
    /// wheel. Telling four chips apart is the other question, so it needs the
    /// other measure. CIE76 is the crude one of the family and is used here
    /// because it is short enough to read; the thresholds above are set well
    /// clear of where its inaccuracy in the blues would matter.
    /// </remarks>
    private static double Distance(Color a, Color b)
    {
        var (first, second) = (Lab(a), Lab(b));

        return Math.Sqrt(
            Math.Pow(first.L - second.L, 2)
            + Math.Pow(first.A - second.A, 2)
            + Math.Pow(first.B - second.B, 2));
    }

    /// <summary>sRGB to L*a*b*, by way of XYZ under a D65 white point.</summary>
    private static (double L, double A, double B) Lab(Color colour)
    {
        var (r, g, b) = (Channel(colour.R), Channel(colour.G), Channel(colour.B));

        var x = ((0.4124 * r) + (0.3576 * g) + (0.1805 * b)) / 0.95047;
        var y = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
        var z = ((0.0193 * r) + (0.1192 * g) + (0.9505 * b)) / 1.08883;

        var (fx, fy, fz) = (Curve(x), Curve(y), Curve(z));

        return ((116 * fy) - 16, 500 * (fx - fy), 200 * (fy - fz));

        static double Curve(double t) =>
            t > 0.008856 ? Math.Cbrt(t) : (7.787 * t) + (16.0 / 116.0);
    }

    private static double Luminance(Color colour) =>
        (0.2126 * Channel(colour.R)) + (0.7152 * Channel(colour.G)) + (0.0722 * Channel(colour.B));

    private static double Channel(byte value)
    {
        var c = value / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
