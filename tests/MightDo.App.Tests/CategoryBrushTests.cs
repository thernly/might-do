using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using MightDo.App.Converters;
using MightDo.Core.Domain;

namespace MightDo.App.Tests;

/// <summary>
/// How a category's one stored colour becomes the two colours it is painted in.
/// </summary>
public class CategoryBrushTests
{
    private static object? Paint(object? colour, ThemeVariant variant) =>
        CategoryBrushConverter.Instance.Convert(
            [colour, variant], typeof(IBrush), null, CultureInfo.InvariantCulture);

    private static Color ColourOf(object? result) => ((ISolidColorBrush)result!).Color;

    [Fact]
    public void ALightSchemeGetsTheColourAsStored()
    {
        var moss = Category.Palette.Single(c => c.Name == "Moss");

        Assert.Equal(
            Color.FromUInt32(moss.Value), ColourOf(Paint(moss.Value, ThemeVariant.Light)));
    }

    [Fact]
    public void ADarkSchemeGetsTheColoursOtherRendering()
    {
        var moss = Category.Palette.Single(c => c.Name == "Moss");

        var painted = ColourOf(Paint(moss.Value, ThemeVariant.Dark));

        Assert.Equal(Color.FromUInt32(moss.OnDark), painted);
        Assert.NotEqual(Color.FromUInt32(moss.Value), painted);
    }

    /// <summary>
    /// A colour the palette never offered is somebody's deliberate choice, and
    /// there is no second rendering of it to reach for.
    /// </summary>
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void AColourOutsideThePaletteIsPaintedExactlyAsStored(string scheme)
    {
        var variant = scheme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

        Assert.Equal(
            Color.FromUInt32(0xFF123456), ColourOf(Paint(0xFF123456u, variant)));
    }

    /// <summary>
    /// A task with no category has no colour, and leaving the property unset
    /// lets the shape stay unpainted rather than painting it transparent black.
    /// </summary>
    [Fact]
    public void NoCategoryLeavesTheFillUnset() =>
        Assert.Equal(AvaloniaProperty.UnsetValue, Paint(null, ThemeVariant.Light));
}
