using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MightDo.App.ViewModels;

namespace MightDo.App.Views;

public partial class MainWindow : Window
{
    private SettingsWindow? _settings;

    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Opens settings, or brings the open one forward. The view model is built
    /// here because it needs the session, which only exists once a workspace is
    /// open.
    /// </summary>
    private void OnOpenSettings(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { Workspace: { } workspace }) return;

        if (_settings is not null)
        {
            _settings.Activate();
            return;
        }

        var viewModel = workspace.CreateSettingsViewModel();
        _settings = new SettingsWindow { DataContext = viewModel };
        _settings.Closed += (_, _) =>
        {
            viewModel.Dispose();
            _settings = null;
        };

        _settings.Show(this);
    }
}

/// <summary>
/// Asks for a folder using the platform's own picker.
/// </summary>
/// <remarks>
/// The view models take <see cref="IFolderPicker"/> rather than reaching for a
/// window, so everything above this line is testable without a UI thread.
/// </remarks>
public sealed class StorageFolderPicker(TopLevel topLevel) : IFolderPicker
{
    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title, AllowMultiple = false });

        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }
}

/// <summary>Asks for a file using the platform's own picker.</summary>
public sealed class StorageFilePicker(TopLevel topLevel) : IFilePicker
{
    public async Task<string?> PickFileAsync(string title)
    {
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions { Title = title, AllowMultiple = false });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}
