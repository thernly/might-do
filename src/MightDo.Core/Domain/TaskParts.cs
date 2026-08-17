using System.Text.Json.Serialization;

namespace MightDo.Core.Domain;

/// <summary>
/// One tickable line in a task's ordered breakdown.
/// </summary>
/// <remarks>
/// Deliberately not a task: no status, no dates, no board presence. Ticking
/// every step off does nothing automatically — it just shows <c>4/6</c> on the
/// card.
/// </remarks>
public sealed record Step(string Id, string Text, bool Done = false)
{
    public static Step Create(string text) => new(Ulid.New(), text);
}

/// <summary>
/// A dated entry in a task's running commentary, written while work proceeds.
/// </summary>
/// <remarks>
/// Distinct from the description, which is written once up front. Notes are
/// never rewritten to reflect a later understanding.
/// </remarks>
public sealed record Note(string Id, DateTime CreatedAt, string Body)
{
    public static Note Create(string body) => new(Ulid.New(), DateTime.UtcNow, body);
}

/// <summary>
/// A file copied into might-do's own storage and bound to a task.
/// </summary>
/// <remarks>
/// The copy is authoritative: moving or deleting the user's original has no
/// effect on it.
/// </remarks>
/// <param name="OriginalName">The name the file had when attached, shown in the UI.</param>
/// <param name="StoredName">
/// Name on disk inside the attachments folder, prefixed with the id so two files
/// called <c>contract.pdf</c> can't collide.
/// </param>
public sealed record Attachment(
    string Id,
    string OriginalName,
    string StoredName,
    long SizeBytes,
    DateTime AddedAt);

/// <summary>
/// A request to be notified about a task at a given moment.
/// </summary>
/// <remarks>
/// Carries its own date <i>and</i> time, set independently of the task's due
/// date — due dates are days, and a notification needs an instant. A task may
/// have several.
/// </remarks>
/// <param name="FiredAt">Set once shown, so it fires exactly once.</param>
/// <param name="DismissedAt">Set when acknowledged, which removes it from the overdue panel.</param>
public sealed record Reminder(
    string Id,
    DateTime RemindAt,
    DateTime? FiredAt = null,
    DateTime? DismissedAt = null)
{
    public static Reminder Create(DateTime remindAt) =>
        new(Ulid.New(), remindAt.ToUniversalTime());

    [JsonIgnore]
    public bool IsPending => FiredAt is null && DismissedAt is null;

    [JsonIgnore]
    public bool IsOutstanding => DismissedAt is null;
}
