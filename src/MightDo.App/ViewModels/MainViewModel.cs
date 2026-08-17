using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MightDo.Core.Storage;
using MightDo.Platform;

namespace MightDo.App.ViewModels;

/// <summary>Asks the user for a folder. Implemented by the view layer.</summary>
public interface IFolderPicker
{
    Task<string?> PickFolderAsync(string title);
}

/// <summary>
/// The application shell: either we have a workspace open, or we are asking for
/// one.
/// </summary>
/// <remarks>
/// "Loading" lives here rather than on the session, because a
/// <see cref="Core.Session.WorkspaceSession"/> exists only once its workspace is
/// loaded. Whether one has been chosen at all is a question about the app, not
/// about the workspace.
/// </remarks>
public sealed partial class MainViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly IFolderPicker _picker;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkspace))]
    private WorkspaceViewModel? _workspace;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isBusy;

    public MainViewModel(AppSettings settings, IFolderPicker picker)
    {
        _settings = settings;
        _picker = picker;
    }

    /// <summary>A parameterless constructor for the XAML designer.</summary>
    public MainViewModel()
        : this(AppSettings.Load(), new NoFolderPicker())
    {
    }

    public bool HasWorkspace => Workspace is not null;

    /// <summary>
    /// Reopens the remembered workspace, if it is still there.
    /// </summary>
    public async Task InitialiseAsync()
    {
        var remembered = _settings.RememberedWorkspacePath;
        if (remembered is null) return;

        if (_settings.WorkspacePath is null)
        {
            // Remembered but no longer resolvable: say so rather than silently
            // starting over. An unmounted drive comes back.
            Message = $"Couldn't find your workspace at {remembered}.";
            return;
        }

        await OpenAsync(remembered);
    }

    [RelayCommand]
    public async Task ChooseWorkspaceAsync()
    {
        var chosen = await _picker.PickFolderAsync("Choose a folder for your tasks");
        if (chosen is null) return;

        _settings.SetWorkspacePath(chosen);
        await OpenAsync(chosen);
    }

    [RelayCommand]
    public void CloseWorkspace()
    {
        Workspace?.Dispose();
        Workspace = null;
        _settings.ForgetWorkspace();
        Message = null;
    }

    public async Task OpenAsync(string path)
    {
        IsBusy = true;
        try
        {
            Workspace?.Dispose();
            var store = new TaskStore(new Core.Storage.Workspace(path));
            Workspace = await WorkspaceViewModel.OpenAsync(store, _settings);
            Message = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = $"Couldn't open {path}: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private sealed class NoFolderPicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    }
}
