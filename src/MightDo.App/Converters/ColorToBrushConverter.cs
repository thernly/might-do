using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace MightDo.App.Converters;

/// <summary>
/// Turns a stored ARGB colour into the brush a folder icon or swatch is drawn
/// with.
/// </summary>
/// <remarks>
/// A <c>uint?</c> with no value converts to <see cref="AvaloniaProperty.UnsetValue"/>
/// rather than to a brush, so binding it straight to <c>Foreground</c> leaves
/// that property unset — letting the icon fall back to whatever it inherits —
/// instead of the view model having to know what that fallback is.
/// </remarks>
public sealed class ColorToBrushConverter : IValueConverter
{
    public static readonly ColorToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            uint color => new ImmutableSolidColorBrush(color),
            _ => AvaloniaProperty.UnsetValue,
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
