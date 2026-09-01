namespace MightDo.Core.Domain;

/// <summary>
/// A stage a task can be in, named and ordered by the user, and rendered as a
/// column of the Kanban view.
/// </summary>
/// <param name="Order">Position among all statuses. Also the left-to-right column order.</param>
/// <param name="HiddenFromBoard">
/// Keeps this status off the Kanban view while leaving it an ordinary status
/// everywhere else. Exists so a <c>Backlog</c> holding hundreds of cards doesn't
/// swamp columns holding five.
/// </param>
public sealed record Status(
    string Id,
    string Name,
    StatusType Type,
    int Order,
    bool HiddenFromBoard = false);

/// <summary>
/// A user-defined grouping answering "what area of my life is this?".
/// A task has at most one.
/// </summary>
/// <param name="Color">
/// ARGB colour for the chip in list and board views. Unsigned deliberately: an
/// opaque colour exceeds <c>0x7FFFFFFF</c> and overflows a signed 32-bit int.
/// </param>
public sealed record Category(string Id, string Name, uint Color)
{
    /// <summary>
    /// The colours a category may be given, in the order they are offered:
    /// round the wheel, ending in a neutral.
    /// </summary>
    /// <remarks>
    /// Twelve, all at one saturation and lightness, so no single category
    /// shouts louder than the rest and the list reads as a spectrum rather than
    /// as a box of odds and ends. Muted rather than saturated, because the chip
    /// sits beside text in dense lists and is a dot, not a highlighter.
    /// <para>
    /// The six a workspace may already hold keep their exact values, and the
    /// hues of the other five are not evenly spaced round the wheel but placed
    /// where they buy the most perceptual distance: even hue steps put two
    /// greens closer together than a person can tell apart at dot size, because
    /// the eye does not divide the wheel evenly. The blue quarter carries three
    /// because it can. See <c>PaletteContrastTests</c>, which measures it.
    /// </para>
    /// <para>
    /// Each carries a name because that, not its eight hex digits, is how the
    /// user picks one, and two renderings because a colour legible on paper is
    /// not legible on night. The <see cref="CategoryColor.Value"/> is the one
    /// written to the workspace: a file read by something that has never heard
    /// of this palette still gets a real colour rather than a token it cannot
    /// resolve.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<CategoryColor> Palette { get; } =
    [
        new("Clay", 0xFF7A5C4F, 0xFFCCA899),
        new("Ochre", 0xFF7A6B4F, 0xFFCCBA99),
        new("Olive", 0xFF7A7A4F, 0xFFCCCC99),
        new("Moss", 0xFF5C7A4F, 0xFFA8CC99),
        new("Teal", 0xFF4F7A6D, 0xFF99CCBD),
        new("Slate", 0xFF4F6D7A, 0xFF99BDCC),
        new("Steel", 0xFF4F607A, 0xFF99ADCC),
        new("Denim", 0xFF4F537A, 0xFF999ECC),
        new("Plum", 0xFF6D4F7A, 0xFFBD99CC),
        new("Fig", 0xFF7A4F6D, 0xFFCC99BD),
        new("Rose", 0xFF7A4F5C, 0xFFCC99A8),
        new("Stone", 0xFF646464, 0xFFB2B2B2),
    ];

    /// <summary>
    /// The colour to hand out for the <paramref name="index"/>th category
    /// nobody chose one for.
    /// </summary>
    /// <remarks>
    /// Strides across the palette rather than walking it, because the palette
    /// is ordered by hue and an import creating six categories in a row would
    /// otherwise paint them six neighbouring greens. Five and twelve share no
    /// factor, so the stride still reaches every colour before repeating any.
    /// </remarks>
    public static uint ColorAt(int index) =>
        Palette[(index * Stride % Palette.Count + Palette.Count) % Palette.Count].Value;

    /// <summary>The palette entry stored as <paramref name="color"/>, or null if it is not one of them.</summary>
    /// <remarks>
    /// A workspace written before the palette was named — or edited by hand —
    /// may hold a colour the palette has never offered, and both the settings
    /// pane and the chip have to show that category as something.
    /// </remarks>
    public static CategoryColor? PaletteEntry(uint color) =>
        Palette.FirstOrDefault(entry => entry.Value == color);

    private const int Stride = 5;
}

/// <summary>
/// One colour a category may be given: the name it is picked by, and the two
/// renderings it has.
/// </summary>
/// <param name="Value">
/// The colour as stored, and as painted in a light scheme. Everything that
/// reads a workspace sees this one.
/// </param>
/// <param name="OnDark">
/// The same colour on a dark ground. Same hue, lifted and softened, because the
/// stored value is chosen to sit on paper and goes muddy against night.
/// </param>
public sealed record CategoryColor(string Name, uint Value, uint OnDark)
{
    /// <summary>Which of the two renderings a scheme calls for.</summary>
    public uint For(bool dark) => dark ? OnDark : Value;
}

/// <summary>A lightweight cross-cutting label. A task may carry several.</summary>
public sealed record Tag(string Id, string Name);
