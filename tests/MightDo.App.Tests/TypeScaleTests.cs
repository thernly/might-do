using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MightDo.Platform;

namespace MightDo.App.Tests;

/// <summary>
/// The sizes of the type roles, relative to each other.
/// </summary>
/// <remarks>
/// A design system is easy to apply one role at a time and end up with a scale
/// nobody chose: a caption set small because captions are small, a control label
/// borrowing the caption role because it happened to look right on its own, and
/// a toolbar where two words the same distance from your eye are two different
/// sizes. None of that shows up in a screenshot of a single control — it only
/// shows up beside its neighbours, which is exactly what these assert.
/// </remarks>
public class TypeScaleTests : IDisposable
{
    /// <summary>
    /// Below this, tracked and uppercase and secondary-coloured, text stops
    /// reading as small and starts reading as unfinished. It is a floor rather
    /// than a size — a role is free to be larger.
    /// </summary>
    private const double CaptionFloor = 12;

    private static readonly DesignTheme[] Designs =
        [DesignTheme.Cyrk66, DesignTheme.SageSlate];

    private static readonly string[] TextRoles =
        ["section", "group", "label", "hint", "meta", "due", "control"];

    /// <summary>
    /// The roles that exist to sit under the prose: a field label, a hint, a
    /// count, a date. Not <c>section</c>, which is a heading, and not
    /// <c>control</c>, which is a peer of the buttons beside it — both are
    /// entitled to reach body size or pass it.
    /// </summary>
    private static readonly string[] SubordinateRoles =
        ["group", "label", "hint", "meta", "due"];

    public void Dispose()
    {
        Theme.ApplyDesign(DesignTheme.Cyrk66);
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact]
    public void NoRoleFallsBelowTheCaptionFloor()
    {
        foreach (var design in Designs)
        {
            foreach (var (role, size) in Sizes(design).Roles)
            {
                Assert.True(
                    size >= CaptionFloor,
                    $"in {design} the {role} role is {size}px, under the {CaptionFloor}px "
                        + "floor — it will read as undersized beside anything next to it.");
            }
        }
    }

    [AvaloniaFact]
    public void AControlLabelIsTheSizeOfTheToolbarItStandsIn()
    {
        // The view switcher's List and Board are words you press, sitting in a
        // strip beside real toolbar buttons. They are not captions: a caption
        // states something and is meant to recede, and a control that recedes
        // below its own neighbours looks like a mistake rather than a hierarchy.
        foreach (var design in Designs)
        {
            Theme.ApplyDesign(design);

            var window = new Window();
            var label = new TextBlock { Classes = { "control" }, Text = "Board" };
            var button = new Button { Classes = { "toolbar" }, Content = "Refresh" };
            window.Content = new StackPanel { Children = { label, button } };
            window.Show();
            window.Measure(Size.Infinity);

            Assert.True(
                label.FontSize == button.FontSize,
                $"in {design} a control label is {label.FontSize}px beside a "
                    + $"{button.FontSize}px toolbar button.");

            window.Close();
        }
    }

    [AvaloniaFact]
    public void TheSubordinateRolesStaySubordinate()
    {
        foreach (var design in Designs)
        {
            var (body, sizes) = Sizes(design);

            foreach (var role in SubordinateRoles)
            {
                var size = sizes[role];

                Assert.True(
                    size < body,
                    $"in {design} the {role} role is {size}px against {body}px body "
                        + "text — a subordinate role has caught up with what it is "
                        + "subordinate to.");
            }
        }
    }

    /// <summary>
    /// The body size and the resolved size of every text role, measured the way
    /// the application measures them: in a real window, wearing a real theme.
    /// </summary>
    private static (double Body, Dictionary<string, double> Roles) Sizes(DesignTheme design)
    {
        Theme.ApplyDesign(design);

        var window = new Window();
        var panel = new StackPanel();
        foreach (var role in TextRoles)
        {
            panel.Children.Add(new TextBlock { Classes = { role }, Text = "Sample" });
        }

        window.Content = panel;
        window.Show();
        window.Measure(Size.Infinity);

        var sizes = panel.Children
            .Cast<TextBlock>()
            .ToDictionary(block => block.Classes.Single(), block => block.FontSize);

        // The window's own size, resolved by the theme, is the body text every
        // other role is measured against — not TextBlock's 12px class default,
        // which is what an unthemed control would report.
        var body = window.FontSize;

        window.Close();
        return (body, sizes);
    }
}
