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

/// <summary>
/// How big the main window was left, and whether it was maximized.
/// </summary>
/// <remarks>
/// The size is the one to come back to, so for a maximized window it is the
/// size it would return to rather than the size of the screen it filled.
/// <para>
/// Position is deliberately not remembered. A remembered position outlives the
/// screen it was chosen on — undock a laptop, or unplug the monitor the window
/// was on, and it reopens somewhere you cannot reach it. Size has no such
/// failure mode once it is clamped to the screen that is actually there.
/// </para>
/// </remarks>
public sealed record WindowPlacement(double Width, double Height, bool Maximized);

/// <summary>The settings file's shape. Machine-local, never synced.</summary>
public sealed record AppSettingsData
{
    public string? WorkspacePath { get; init; }

    public ViewMode ViewMode { get; init; } = ViewMode.List;

    public double? WindowWidth { get; init; }

    public double? WindowHeight { get; init; }

    public bool WindowMaximized { get; init; }
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

    /// <summary>
    /// The size the window was left at, or null if it has never been recorded
    /// or what was recorded is not a size.
    /// </summary>
    /// <remarks>
    /// A hand-edited or half-written file can hold anything, including a
    /// negative or infinite number. Nothing here knows what a sensible window
    /// is — that is the window's own business, and it clamps — but a value that
    /// is not a positive, finite length is not a size at all, and is refused
    /// rather than passed on.
    /// </remarks>
    public WindowPlacement? WindowPlacement =>
        IsALength(_data.WindowWidth) && IsALength(_data.WindowHeight)
            ? new WindowPlacement(
                _data.WindowWidth!.Value, _data.WindowHeight!.Value, _data.WindowMaximized)
            : null;

    private static bool IsALength(double? value) =>
        value is { } length && double.IsFinite(length) && length > 0;

    public void SetWorkspacePath(string path) => Save(_data with { WorkspacePath = path });

    public void ForgetWorkspace() => Save(_data with { WorkspacePath = null });

    public void SetViewMode(ViewMode mode) => Save(_data with { ViewMode = mode });

    public void SetWindowPlacement(WindowPlacement placement) => Save(_data with
    {
        WindowWidth = placement.Width,
        WindowHeight = placement.Height,
        WindowMaximized = placement.Maximized,
    });

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
