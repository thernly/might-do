using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using MightDo.Platform;

namespace MightDo.App.Tests;

/// <summary>
/// The faces a theme names, and the fact that they arrive.
/// </summary>
/// <remarks>
/// An embedded font fails silently and completely: get one character of the
/// <c>avares://</c> URI wrong, or rename a file, and Avalonia quietly falls back
/// to the platform default. Every screen still renders, every other test still
/// passes, and the application is simply no longer wearing the design it says it
/// is. Nothing but this notices.
///
/// Only embedded faces can be checked this way. The headless font manager ships
/// five stub families and resolves every system font name to "BareMinimum", so a
/// theme that names the platform's own face is making a claim no test in this
/// project can falsify.
/// </remarks>
public class TypographyTests : IDisposable
{
    public void Dispose()
    {
        Theme.ApplyDesign(DesignTheme.Cyrk66);
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact]
    public void TheEmbeddedFacesActuallyLoad()
    {
        Theme.ApplyDesign(DesignTheme.Cyrk66);

        // The wordmark is set in italic and only in italic, which is why the
        // upright Bevan is not shipped — asking for the italic has to keep
        // finding a real Bevan rather than a synthesised slant of something else.
        Resolves("FontDisplay", FontStyle.Italic, "Bevan");

        // FontText is deliberately not checked here, and cannot be: it names the
        // platform's own UI face, and the headless font manager installs five
        // stub families and resolves every system name to "BareMinimum". It will
        // answer True for a face that does not exist and True for one that does.
        // This is the cost of a platform face over an embedded one — the theme's
        // body text is now something only a person looking at the running
        // application can confirm.
        Assert.True(Application.Current!.TryGetResource("FontText", null, out var text));
        Assert.DoesNotContain("avares://", text!.ToString()!, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void NoThemeShipsAMonospaceFamilyOfItsOwn()
    {
        // The caption role is the text face, uppercase and tracked. The mono key
        // survives for the file-path lists in the error banners and nothing else,
        // and there the platform's own mono is the right answer — a whole family
        // embedded for two error banners would not be. Naming an avares:// mono
        // here again is how an unwanted face creeps back in.
        foreach (var design in new[] { DesignTheme.Cyrk66, DesignTheme.SageSlate })
        {
            Theme.ApplyDesign(design);

            Assert.True(Application.Current!.TryGetResource("FontMono", null, out var value));

            Assert.DoesNotContain(
                "avares://",
                ((FontFamily)value!).ToString(),
                StringComparison.Ordinal);
        }
    }

    [AvaloniaFact]
    public void TheRolesThatCarryNumbersAskForTabularFigures()
    {
        // Proportional figures reflow as they change — a due date or a count
        // shifts every neighbour each time it ticks. tnum asks the face for its
        // tabular set instead.
        //
        // Now that the text face is the platform's, whether the request is
        // honoured depends on the machine: the macOS system UI face carries tnum,
        // and so does Segoe UI, but Helvetica has no OpenType features at all and
        // would ignore it silently. The request costs nothing where it is not
        // understood, and this pins that it is still being made.
        //
        // This asserts the request rather than the result: the headless
        // renderer measures every glyph at one width, so a shaping feature has
        // no observable effect to measure. What it does catch is the feature
        // being dropped, or the string failing to parse into a feature at all.
        Theme.ApplyDesign(DesignTheme.Cyrk66);

        var window = new Window();
        var panel = new StackPanel();
        foreach (var role in new[] { "due", "meta" })
        {
            panel.Children.Add(new TextBlock { Classes = { role }, Text = "2026-10-31" });
        }

        window.Content = panel;
        window.Show();
        window.Measure(Size.Infinity);

        foreach (var block in panel.Children.Cast<TextBlock>())
        {
            var role = block.Classes.Single();

            Assert.True(
                block.FontFeatures?.Any(feature => feature.Tag == "tnum" && feature.Value == 1)
                    is true,
                $"the {role} role does not ask for tabular figures, so its numbers "
                    + "will change width as they change value.");
        }

        window.Close();
    }

    private static void Resolves(string key, FontStyle style, string expected)
    {
        Assert.True(
            Application.Current!.TryGetResource(key, null, out var value),
            $"{key} is not defined.");

        var asked = new Typeface((FontFamily)value!, style);

        Assert.True(
            FontManager.Current.TryGetGlyphTypeface(asked, out var got),
            $"{key} names {value}, which the font manager cannot load at all.");

        Assert.True(
            got!.FamilyName == expected,
            $"{key} was meant to be {expected} but resolved to {got.FamilyName} — "
                + "the font file is missing or the avares:// URI does not match it, "
                + "and the application has silently fallen back.");
    }
}
