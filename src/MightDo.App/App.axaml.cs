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
            var viewModel = new MainViewModel(settings, new StorageFolderPicker(window));

            window.DataContext = viewModel;
            window.Closed += (_, _) => viewModel.Workspace?.Dispose();
            desktop.MainWindow = window;

            // Reopen the remembered workspace once the UI is up, so a slow disk
            // or an unmounted drive doesn't stall the window appearing.
            window.Opened += async (_, _) => await viewModel.InitialiseAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
