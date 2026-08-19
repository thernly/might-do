using System.Text.Json;
using System.Text.Json.Nodes;

namespace MightDo.Core.Tests;

/// <summary>
/// Locates <c>fixtures/</c> — the portable definition of the on-disk format,
/// shared with the original implementation and described by
/// <c>docs/format/workspace-v1.md</c>.
/// </summary>
public static class Fixtures
{
    public static string Root { get; } = Locate();

    public static string Path(params string[] parts) =>
        System.IO.Path.Combine([Root, .. parts]);

    public static string ReadText(params string[] parts) =>
        File.ReadAllText(Path(parts));

    public static JsonNode ReadNode(params string[] parts) =>
        JsonNode.Parse(ReadText(parts))
        ?? throw new InvalidOperationException($"empty fixture: {Path(parts)}");

    public static JsonDocument ReadDocument(params string[] parts) =>
        JsonDocument.Parse(ReadText(parts));

    private static string Locate()
    {
        // Walk up from the test assembly rather than hardcoding a relative
        // path, so the tests work from the IDE, `dotnet test`, and the repo root
        // alike.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "fixtures");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"No 'fixtures' directory above {AppContext.BaseDirectory}");
    }
}

public static class JsonAssert
{
    /// <summary>
    /// Asserts two JSON documents hold the same values, ignoring formatting and
    /// property order. Compatibility is semantic, not byte-identical — see
    /// <c>docs/format/workspace-v1.md</c>.
    /// </summary>
    public static void SemanticallyEqual(JsonNode? expected, JsonNode? actual, string because)
    {
        if (JsonNode.DeepEquals(expected, actual)) return;

        var options = new JsonSerializerOptions { WriteIndented = true };
        Assert.Fail($"""
            {because}

            expected:
            {expected?.ToJsonString(options)}

            actual:
            {actual?.ToJsonString(options)}
            """);
    }
}
