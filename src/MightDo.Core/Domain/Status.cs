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
    /// The colours offered when a category is created, in the order they are
    /// handed out.
    /// </summary>
    /// <remarks>
    /// Somewhere has to choose one when the user has not: the settings pane
    /// pre-fills the first, and an import creating categories the user never
    /// saw walks the list. Muted rather than saturated, because the chip sits
    /// beside text in both themes.
    /// </remarks>
    public static IReadOnlyList<uint> Palette { get; } =
    [
        0xFF4F6D7A, 0xFF7A5C4F, 0xFF5C7A4F, 0xFF6D4F7A, 0xFF7A4F5C, 0xFF4F7A6D,
    ];

    /// <summary>The palette colour at <paramref name="index"/>, wrapping round.</summary>
    public static uint ColorAt(int index) =>
        Palette[(index % Palette.Count + Palette.Count) % Palette.Count];
}

/// <summary>A lightweight cross-cutting label. A task may carry several.</summary>
public sealed record Tag(string Id, string Name);
