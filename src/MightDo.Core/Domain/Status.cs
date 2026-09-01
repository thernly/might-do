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
    /// The colours a category may be given, in the order they are offered.
    /// </summary>
    /// <remarks>
    /// Somewhere has to choose one when the user has not: the settings pane
    /// pre-fills the first, and an import creating categories the user never
    /// saw walks the list. Muted rather than saturated, because the chip sits
    /// beside text in both themes. Each carries a name because that, not its
    /// eight hex digits, is how the user picks one.
    /// </remarks>
    public static IReadOnlyList<CategoryColor> Palette { get; } =
    [
        new("Slate", 0xFF4F6D7A),
        new("Clay", 0xFF7A5C4F),
        new("Moss", 0xFF5C7A4F),
        new("Plum", 0xFF6D4F7A),
        new("Rose", 0xFF7A4F5C),
        new("Teal", 0xFF4F7A6D),
    ];

    /// <summary>The palette colour at <paramref name="index"/>, wrapping round.</summary>
    public static uint ColorAt(int index) =>
        Palette[(index % Palette.Count + Palette.Count) % Palette.Count].Value;

    /// <summary>The palette entry for <paramref name="color"/>, or null if it is not one of them.</summary>
    /// <remarks>
    /// A workspace written before the palette was named — or edited by hand —
    /// may hold a colour the palette has never offered, and the settings pane
    /// has to show that category as something.
    /// </remarks>
    public static CategoryColor? PaletteEntry(uint color) =>
        Palette.FirstOrDefault(entry => entry.Value == color);
}

/// <summary>One colour a category may be given, and the name it is picked by.</summary>
/// <param name="Value">The ARGB colour itself.</param>
public sealed record CategoryColor(string Name, uint Value);

/// <summary>A lightweight cross-cutting label. A task may carry several.</summary>
public sealed record Tag(string Id, string Name);
