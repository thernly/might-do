using System.Text.Json;
using System.Text.Json.Serialization;

namespace MightDo.Platform;

/// <summary>Which presentation of the tasks the user last had open.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ViewMode>))]
public enum ViewMode
{
    [JsonStringEnumMemberName("list")]
    List,

    [JsonStringEnumMemberName("board")]
    Board,
}

/// <summary>The settings file's shape. Machine-local, never synced.</summary>
public sealed record AppSettingsData
{
    public string? WorkspacePath { get; init; }

    public ViewMode ViewMode { get; init; } = ViewMode.List;
}

/// <summary>
/// Preferences that belong to this machine rather than to the workspace.
/// </summary>
/// <remarks>
/// The workspace path lives here and not in the workspace itself — the folder
/// sits at a different path on each machine, so storing it alongside the tasks
/// would mean syncing a value that is wrong everywhere but where it was written.
/// <para>
/// None of this is part of the on-disk workspace format, and nothing here
/// belongs in <c>MightDo.Core</c>.
/// </para>
/// </remarks>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _path;
    private AppSettingsData _data;

    public AppSettings(string path, AppSettingsData data)
    {
        _path = path;
        _data = data;
    }

    /// <summary>
    /// Where the settings live: the platform's application-data folder, unless
    /// <c>MIGHTDO_SETTINGS</c> names somewhere else.
    /// </summary>
    /// <remarks>
    /// The override exists so a development run cannot silently attach to the
    /// real workspace remembered from ordinary use — which would put a live
    /// watcher and reminder scheduler on someone's actual tasks.
    /// </remarks>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("MIGHTDO_SETTINGS") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ApplicationData,
                    Environment.SpecialFolderOption.Create),
                "might-do",
                "settings.json");

    /// <summary>
    /// Reads the settings, falling back to defaults.
    /// </summary>
    /// <remarks>
    /// A corrupt or unreadable file yields defaults rather than throwing. Losing
    /// a remembered folder path is a small annoyance; refusing to start over it
    /// would not be.
    /// </remarks>
    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;

        try
        {
            if (File.Exists(path))
            {
                var data = JsonSerializer.Deserialize<AppSettingsData>(
                    File.ReadAllText(path), Options);
                if (data is not null) return new AppSettings(path, data);
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall through to defaults.
        }

        return new AppSettings(path, new AppSettingsData());
    }

    /// <summary>
    /// The chosen workspace folder, or null if none has been picked or the
    /// folder has since gone — an unmounted drive, a moved OneDrive folder.
    /// </summary>
    public string? WorkspacePath =>
        RememberedWorkspacePath is { } remembered && Directory.Exists(remembered)
            ? remembered
            : null;

    /// <summary>
    /// The stored path even if it no longer resolves, so the app can say
    /// "couldn't find your workspace at X" rather than silently starting over.
    /// </summary>
    public string? RememberedWorkspacePath => _data.WorkspacePath;

    public ViewMode ViewMode => _data.ViewMode;

    public void SetWorkspacePath(string path) => Save(_data with { WorkspacePath = path });

    public void ForgetWorkspace() => Save(_data with { WorkspacePath = null });

    public void SetViewMode(ViewMode mode) => Save(_data with { ViewMode = mode });

    private void Save(AppSettingsData data)
    {
        _data = data;

        var directory = Path.GetDirectoryName(_path);
        if (directory is not null) Directory.CreateDirectory(directory);

        // Same temp-then-rename as the workspace: a half-written settings file
        // would be read as corrupt and silently reset the user's preferences.
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, Options));
        File.Move(temp, _path, overwrite: true);
    }
}
