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
