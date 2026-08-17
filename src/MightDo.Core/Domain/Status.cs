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
public sealed record Category(string Id, string Name, uint Color);

/// <summary>A lightweight cross-cutting label. A task may carry several.</summary>
public sealed record Tag(string Id, string Name);
