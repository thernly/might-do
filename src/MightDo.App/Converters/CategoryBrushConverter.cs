using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using MightDo.Core.Domain;

namespace MightDo.App.Converters;

/// <summary>
/// Paints a category's stored colour the way the current scheme wants it.
/// </summary>
/// <remarks>
/// The workspace stores one colour per category, chosen to sit on paper. On a
/// dark ground that same value goes muddy, so a palette colour is swapped for
/// its dark rendering — same hue and the same name in settings, lifted enough
/// to read. A colour the palette does not know is painted exactly as stored:
/// somebody chose it deliberately, and second-guessing it would be worse than
/// leaving it alone.
/// <para>
/// It takes the theme variant as a second value rather than reading it from the
/// application, because a converter is not re-run when something it never
/// looked at changes. Bound to the control's own
/// <see cref="StyledElement.ActualThemeVariant"/>, the swap follows a theme
/// change immediately — which is the promise every other colour in these views
/// keeps by using a DynamicResource.
/// </para>
/// </remarks>
public sealed class CategoryBrushConverter : IMultiValueConverter
{
    public static readonly CategoryBrushConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2 || values[0] is not uint color) return AvaloniaProperty.UnsetValue;

        var dark = Equals(values[1], ThemeVariant.Dark);
        return new ImmutableSolidColorBrush(Category.PaletteEntry(color)?.For(dark) ?? color);
    }
}
