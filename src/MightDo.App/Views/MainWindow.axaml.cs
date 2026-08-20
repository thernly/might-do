using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MightDo.App.ViewModels;
using MightDo.Platform;

namespace MightDo.App.Views;

public partial class MainWindow : Window
{
    private SettingsWindow? _settings;

    /// <summary>
    /// The shell this window is showing, kept so the workspace it is closing can
    /// be heard about.
    /// </summary>
    private MainViewModel? _shell;

    private readonly WindowSizeMemory _size = new();

    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The remembered size is applied here rather than on opening, so the window
    /// is never painted at one size and then jumped to another.
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_shell is not null) _shell.WorkspaceClosing -= OnWorkspaceClosing;
        _shell = DataContext as MainViewModel;
        if (_shell is not null) _shell.WorkspaceClosing += OnWorkspaceClosing;

        if (DataContext is not MainViewModel { WindowPlacement: { } placement }) return;

        // Clamped to the screen in front of the user, not the one the size was
        // chosen on: a window sized on a desk monitor must still fit when the
        // laptop is undocked.
        var (availableWidth, availableHeight) = AvailableSize();
        Width = Clamp(placement.Width, MinWidth, availableWidth);
        Height = Clamp(placement.Height, MinHeight, availableHeight);

        _size.Remembered(new Size(Width, Height));

        if (placement.Maximized) WindowState = WindowState.Maximized;
    }

    protected override void OnResized(WindowResizedEventArgs e)
    {
        base.OnResized(e);

        _size.Resized(e.ClientSize, WindowState);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        SaveSize();
    }

    /// <summary>
    /// Records the size to reopen at.
    /// </summary>
    /// <remarks>
    /// Public because closing the window is not the only way out: quitting from
    /// the menu or with Cmd+Q asks the application to shut down, and the
    /// composition root calls this from there too. Writing the same value twice
    /// costs nothing; not writing it at all would mean the size was only ever
    /// kept by users who close the window rather than quit.
    /// </remarks>
    public void SaveSize()
    {
        if (DataContext is not MainViewModel viewModel) return;

        viewModel.RememberWindow(_size.Placement(new Size(Width, Height), WindowState));
    }

    /// <summary>
    /// The usable area of the screen this window is on, in the same units as
    /// <see cref="Layoutable.Width"/>.
    /// </summary>
    private (double Width, double Height) AvailableSize()
    {
        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is not { } screen)
        {
            return (double.PositiveInfinity, double.PositiveInfinity);
        }

        var area = screen.WorkingArea;
        return (area.Width / screen.Scaling, area.Height / screen.Scaling);
    }

    /// <summary>
    /// Clamps, preferring the minimum. <see cref="Math.Clamp(double, double, double)"/>
    /// throws when the bounds cross, which they do on a screen smaller than the
    /// window's own minimum size.
    /// </summary>
    private static double Clamp(double value, double min, double max) =>
        Math.Max(min, Math.Min(value, max));

    /// <summary>
    /// Closes the workspace switcher once the choice inside it is made.
    /// </summary>
    /// <remarks>
    /// A flyout stays open when a button inside it is pressed, which is right
    /// for the rename box — it appears in the flyout and needs it to stay — and
    /// wrong for everything that finishes the job. Leaving it standing over the
    /// workspace the user just switched to hides the very thing they asked to
    /// see.
    /// <para>
    /// Posted rather than done here. A button raises Click before it runs its
    /// own Command, and closing the flyout takes the rows out of the tree with
    /// it — which leaves the row's command, bound by reaching up to the
    /// ItemsControl, resolving to nothing. Closing after this click has been
    /// dealt with lets the command run first.
    /// </para>
    /// </remarks>
    private void OnSwitcherFinished(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(
            () => WorkspaceSwitcher.Flyout?.Hide(), DispatcherPriority.Background);

    /// <summary>
    /// Closes settings when the workspace it belongs to is being closed.
    /// </summary>
    /// <remarks>
    /// The page is a view onto one workspace's session, so it cannot outlive
    /// it: left standing it would show the workspace the user has just left and
    /// send its edits to a disposed session, which it treats as shutdown and
    /// swallows. Closing runs the handler below, which disposes the view model
    /// and forgets the window, so the next press of Settings builds a fresh one
    /// on whichever workspace is open by then.
    /// </remarks>
    private void OnWorkspaceClosing(object? sender, EventArgs e) => _settings?.Close();

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
    public Task<string?> PickFileAsync(string title) => PickAsync(title, filter: null);

    public Task<string?> PickFileAsync(string title, string typeName, params string[] extensions) =>
        PickAsync(
            title,
            new FilePickerFileType(typeName)
            {
                Patterns = [.. extensions.Select(extension => $"*.{extension}")],
            });

    private async Task<string?> PickAsync(string title, FilePickerFileType? filter)
    {
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = filter is null ? null : [filter],
            });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}

/// <summary>Asks where to write a file, using the platform's own picker.</summary>
/// <remarks>
/// The counterpart to <see cref="StorageFilePicker"/>, and the only thing the
/// export needs from the view layer: everything above this line works in terms
/// of a path.
/// </remarks>
public sealed class StorageFileSaver(TopLevel topLevel) : IFileSaver
{
    public async Task<string?> PickSaveFileAsync(string title, string suggestedName)
    {
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName,
                DefaultExtension = "csv",
                FileTypeChoices = [new FilePickerFileType("CSV files") { Patterns = ["*.csv"] }],
            });

        return file?.TryGetLocalPath();
    }
}
