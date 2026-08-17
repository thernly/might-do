using System.Text.Json.Serialization;

namespace MightDo.Core.Domain;

/// <summary>
/// The fixed classification every status belongs to.
/// </summary>
/// <remarks>
/// This set is closed and not user-editable — see
/// <c>docs/adr/0002-statuses-are-user-data-typed-by-a-closed-set.md</c>. Users
/// invent and rename statuses freely; the application reasons about types.
/// <para>
/// The wire values are part of the on-disk format and are pinned explicitly
/// rather than derived from the member names, so renaming a member here can
/// never silently change what is written to a user's folder.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<StatusType>))]
public enum StatusType
{
    /// <summary>Work not yet begun. Covers <c>Backlog</c>, <c>Ready</c>, <c>Not Started</c>.</summary>
    [JsonStringEnumMemberName("initial")]
    Initial,

    /// <summary>
    /// Work under way. Covers <c>In Progress</c>, <c>Blocked</c>, <c>In Review</c> —
    /// a blocked task is still active work.
    /// </summary>
    [JsonStringEnumMemberName("active")]
    Active,

    /// <summary>
    /// Work concluded, whether or not it was done. Covers <c>Done</c>,
    /// <c>Abandoned</c>. Entering any status of this type stamps the task's
    /// completion date.
    /// </summary>
    [JsonStringEnumMemberName("final")]
    Final,
}
