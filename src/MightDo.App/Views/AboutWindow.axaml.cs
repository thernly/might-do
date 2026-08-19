using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MightDo.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionLabel.Text = $"Version {ApplicationVersion()}";
    }

    internal static string ApplicationVersion()
    {
        var assembly = typeof(App).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        return informational?.Split('+')[0]
               ?? assembly.GetName().Version?.ToString(3)
               ?? "unknown";
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
