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
    /// <summary>Every workspace the user has added, in the order they added them.</summary>
    public IReadOnlyList<RememberedWorkspace> Workspaces { get; init; } = [];

    /// <summary>Which of them is open.</summary>
    public string? CurrentWorkspacePath { get; init; }

    /// <summary>
    /// The single workspace path older versions wrote, migrated on load.
    /// </summary>
    /// <remarks>
    /// Kept only so an existing installation does not open to the "choose a
    /// folder" screen having apparently forgotten everything. Read once, folded
    /// into <see cref="Workspaces"/>, and never written again.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkspacePath { get; init; }

    /// <summary>
    /// The view a newly added workspace starts in, which is the last one chosen
    /// anywhere. Each workspace then keeps its own in <see cref="WorkspaceViewState"/>.
    /// </summary>
    public ViewMode ViewMode { get; init; } = ViewMode.List;

    public double? WindowWidth { get; init; }

    public double? WindowHeight { get; init; }

    public bool WindowMaximized { get; init; }

    /// <summary>
    /// The same settings with any pre-list workspace path folded into the list.
    /// </summary>
    public AppSettingsData Migrated()
    {
        if (WorkspacePath is not { Length: > 0 } path || Workspaces.Count > 0) return this;

        return this with
        {
            Workspaces =
            [
                new RememberedWorkspace
                {
                    Path = path,
                    Name = RememberedWorkspace.NameFor(path),
                    View = new WorkspaceViewState { ViewMode = ViewMode },
                },
            ],
            CurrentWorkspacePath = path,
            WorkspacePath = null,
        };
    }
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
                if (data is not null) return new AppSettings(path, data.Migrated());
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall through to defaults.
        }

        return new AppSettings(path, new AppSettingsData());
    }

    // ------------------------------------------------------------- workspaces

    /// <summary>Every workspace the user has added, in the order they added them.</summary>
    public IReadOnlyList<RememberedWorkspace> Workspaces => _data.Workspaces;

    /// <summary>The workspace that is open, or null if none has been chosen.</summary>
    public RememberedWorkspace? CurrentWorkspace => Find(_data.CurrentWorkspacePath);

    /// <summary>
    /// The chosen workspace folder, or null if none has been picked or the
    /// folder has since gone — an unmounted drive, a moved OneDrive folder.
    /// </summary>
    public string? WorkspacePath =>
        CurrentWorkspace is { Exists: true } workspace ? workspace.Path : null;

    /// <summary>
    /// The stored path even if it no longer resolves, so the app can say
    /// "couldn't find your workspace at X" rather than silently starting over.
    /// </summary>
    public string? RememberedWorkspacePath => CurrentWorkspace?.Path;

    /// <summary>
    /// Adds a workspace if it is new, and makes it the current one either way.
    /// </summary>
    /// <remarks>
    /// Adding a folder that is already in the list switches to it rather than
    /// listing it twice. Picking the same folder from the folder picker a second
    /// time is a switch, however the user thought of it, and a duplicate entry
    /// would be two rows that cannot be told apart and one stale copy of the
    /// view state.
    /// </remarks>
    public RememberedWorkspace AddWorkspace(string path, string? name = null)
    {
        var full = System.IO.Path.GetFullPath(path);

        if (Find(full) is { } existing)
        {
            Save(_data with { CurrentWorkspacePath = existing.Path });
            return existing;
        }

        var added = new RememberedWorkspace
        {
            Path = full,
            Name = name is { Length: > 0 } given ? given : RememberedWorkspace.NameFor(full),
            View = new WorkspaceViewState { ViewMode = _data.ViewMode },
        };

        Save(_data with
        {
            Workspaces = [.. _data.Workspaces, added],
            CurrentWorkspacePath = added.Path,
        });

        return added;
    }

    /// <summary>Opens one of the remembered workspaces.</summary>
    public void SetCurrentWorkspace(string path)
    {
        if (Find(path) is not { } workspace)
        {
            throw new ArgumentException($"Not a remembered workspace: '{path}'", nameof(path));
        }

        Save(_data with { CurrentWorkspacePath = workspace.Path });
    }

    public void RenameWorkspace(string path, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return;

        Replace(path, workspace => workspace with { Name = trimmed });
    }

    /// <summary>
    /// Drops a workspace from the list. The folder and everything in it is left
    /// exactly where it is.
    /// </summary>
    /// <remarks>
    /// Forgetting the current workspace closes it and leaves nothing open,
    /// rather than jumping to a neighbour. Which workspace is on screen is the
    /// user's choice to make, and quietly loading a different set of tasks into
    /// a window they were reading is a worse surprise than an empty one.
    /// </remarks>
    public void ForgetWorkspace(string path)
    {
        if (Find(path) is not { } workspace) return;

        Save(_data with
        {
            Workspaces = [.. _data.Workspaces.Where(w => w.Path != workspace.Path)],
            CurrentWorkspacePath =
                _data.CurrentWorkspacePath == workspace.Path ? null : _data.CurrentWorkspacePath,
        });
    }

    /// <summary>Closes the current workspace without forgetting it.</summary>
    public void CloseWorkspace() => Save(_data with { CurrentWorkspacePath = null });

    // ------------------------------------------------------------- view state

    /// <summary>
    /// How the given workspace was left, or a default for one not in the list.
    /// </summary>
    /// <remarks>
    /// A workspace nobody has stored state for starts in whichever view was
    /// last used anywhere, so adding a second workspace does not throw a board
    /// user back to the list.
    /// </remarks>
    public WorkspaceViewState ViewStateFor(string path) =>
        Find(path)?.View ?? new WorkspaceViewState { ViewMode = _data.ViewMode };

    /// <summary>Records how a workspace has been left.</summary>
    public void SaveViewState(string path, WorkspaceViewState state)
    {
        if (Find(path) is not { } workspace) return;
        if (workspace.View.SameAs(state) && _data.ViewMode == state.ViewMode) return;

        Replace(path, w => w with { View = state }, _data with { ViewMode = state.ViewMode });
    }

    private RememberedWorkspace? Find(string? path)
    {
        if (path is not { Length: > 0 }) return null;

        // Compared as written, not case-folded: the app stores full paths it was
        // handed, and on a case-sensitive filesystem two spellings really are
        // two folders.
        return _data.Workspaces.FirstOrDefault(w => w.Path == path)
            ?? _data.Workspaces.FirstOrDefault(w => w.Path == System.IO.Path.GetFullPath(path));
    }

    private void Replace(
        string path, Func<RememberedWorkspace, RememberedWorkspace> edit, AppSettingsData? onto = null)
    {
        if (Find(path) is not { } workspace) return;

        var data = onto ?? _data;
        Save(data with
        {
            Workspaces = [.. data.Workspaces.Select(w => w.Path == workspace.Path ? edit(w) : w)],
        });
    }

    // ------------------------------------------------------------------ the rest

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

    /// <summary>Adds a workspace and opens it. See <see cref="AddWorkspace"/>.</summary>
    public void SetWorkspacePath(string path) => AddWorkspace(path);

    /// <summary>Drops the open workspace from the list, leaving nothing open.</summary>
    public void ForgetWorkspace()
    {
        if (_data.CurrentWorkspacePath is { } path) ForgetWorkspace(path);
    }

    /// <summary>
    /// Sets the view a newly added workspace will start in. An open workspace
    /// records its own through <see cref="SaveViewState"/>.
    /// </summary>
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
