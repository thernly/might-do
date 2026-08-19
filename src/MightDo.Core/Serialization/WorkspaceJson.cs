using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace MightDo.Core.Serialization;

/// <summary>
/// The JSON settings the workspace format is written with.
/// </summary>
/// <remarks>
/// See <c>docs/format/workspace-v1.md</c>. Compatibility is semantic — any
/// implementation that reads the same values and writes values that survive the
/// trip back is correct — but these options keep our output looking like the
/// canonical corpus, so a folder edited by more than one implementation stays
/// greppable and produces one-line diffs for the sync client to resolve.
/// </remarks>
public static class WorkspaceJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
            IndentSize = 2,

            // System.Text.Json escapes non-ASCII *and* HTML-sensitive characters
            // (< > & ') by default, which turns "Café — 日本語" into a wall of
            // \uXXXX. That is valid JSON and reads back correctly, but it
            // defeats the plain-text greppability ADR-0001 is built on.
            // "Unsafe" here means unsafe to embed in HTML without escaping,
            // which a file in the user's own folder never is.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

            // Nullable fields are written as explicit nulls, matching what is on
            // disk today. Readers must not depend on that — absent keys are
            // legal and every optional field has a documented default.
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,

            // A task file written by a future version may carry keys we do not
            // know. Dropping them silently is the documented behaviour of the
            // format; throwing would make a newer sibling machine's file
            // unreadable rather than merely lossy.
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,

            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        };

        options.Converters.Add(new CalendarDateConverter());
        options.Converters.Add(new InstantConverter());

        return options;
    }

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options);
}
