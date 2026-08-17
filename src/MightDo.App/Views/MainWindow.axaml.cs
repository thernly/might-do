using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MightDo.App.ViewModels;

namespace MightDo.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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
