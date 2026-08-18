using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MightDo.App.ViewModels;
using MightDo.App.Views;
using MightDo.Platform;

namespace MightDo.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Composition root. The window is created first so the folder picker
            // has a top level to hang off, then the view model is given to it.
            var window = new MainWindow();
            var settings = AppSettings.Load();

            // Before the window is shown, so it does not appear in one scheme
            // and flip to the other.
            Theme.Apply(settings.Theme);

            var viewModel = new MainViewModel(
                settings,
                new StorageFolderPicker(window),
                new StorageFilePicker(window));

            window.DataContext = viewModel;
            window.Closed += (_, _) => viewModel.Workspace?.Dispose();

            // Quitting from the menu, or with Cmd+Q, is not the same path as
            // closing the window.
            desktop.ShutdownRequested += (_, _) => window.SaveSize();

            desktop.MainWindow = window;

            // Reopen the remembered workspace once the UI is up, so a slow disk
            // or an unmounted drive doesn't stall the window appearing.
            window.Opened += async (_, _) => await viewModel.InitialiseAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
