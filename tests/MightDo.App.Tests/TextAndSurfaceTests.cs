using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MightDo.App.ViewModels;
using MightDo.App.Views;
using MightDo.Core.Storage;
using MightDo.Platform;

namespace MightDo.App.Tests;

/// <summary>
/// Text colour and the lit edge on raised surfaces.
/// </summary>
/// <remarks>
/// Text colour is inherited rather than set per element, and inheritance is
/// exactly what a control template can quietly break, so these check the brush
/// arrives at a real TextBlock rather than checking the setter exists.
/// </remarks>
public class TextAndSurfaceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mightdo-text-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact]
    public async Task RowTextTakesTheApplicationsTextBrush()
    {
        // The row summary sits inside a ListBoxItem, whose Fluent template has
        // a Foreground of its own — if it wins, the window's setting never
        // reaches the text that covers most of the screen.
        var window = await OpenAsync("Read the summary");

        var summary = Descendants<TextBlock>(window)
            .First(block => block.Text == "Read the summary");

        Assert.Same(Resource("AppTextBrush"), summary.Foreground);
    }

    [AvaloniaFact]
    public async Task CardTextTakesItTooOnTheBoard()
    {
        // The board is the other half of the screen and reaches the brush by a
        // different path — inheritance through a plain ItemsControl rather than
        // past a ListBoxItem theme.
        var window = await OpenAsync("On a card");
        Workspace(window).ShowBoardCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        window.Measure(window.ClientSize);
        window.Arrange(new Rect(window.ClientSize));

        var summary = Descendants<TextBlock>(window)
            .First(block => block.Text == "On a card");

        Assert.Same(Resource("AppTextBrush"), summary.Foreground);
    }

    [AvaloniaFact]
    public async Task SecondaryTextIsADifferentColourRatherThanAFadedOne()
    {
        // Opacity fades text toward whatever is behind it, which is why hints
        // used to look muddy over a tinted chip. A brush is a brush anywhere.
        var window = await OpenAsync("Anything");

        var hint = Descendants<TextBlock>(window)
            .First(block => block.Classes.Contains("hint"));

        Assert.Same(Resource("AppTextSecondaryBrush"), hint.Foreground);
        Assert.Equal(1, hint.Opacity);
        Assert.NotSame(Resource("AppTextBrush"), hint.Foreground);
    }

    [AvaloniaFact]
    public async Task RaisedSurfacesCarryTheCardEdge()
    {
        var window = await OpenAsync("Anything");

        var edge = Resource("AppCardBorderBrush");
        var raised = Descendants<Border>(window)
            .Where(border => ReferenceEquals(border.BorderBrush, edge));

        // The list container and the detail pane are both raised; the pane is
        // only in the tree once something is selected, so one is enough here.
        Assert.NotEmpty(raised);
    }

    [AvaloniaFact]
    public void TheDarkCardEdgeIsLitAlongTheTop()
    {
        // The whole point of the gradient: it must start lighter than it ends,
        // or the border is a flat line wearing a gradient's clothes.
        Assert.True(Application.Current!.TryGetResource(
            "AppCardBorderBrush", ThemeVariant.Dark, out var value));

        var gradient = Assert.IsType<LinearGradientBrush>(value);
        var top = gradient.GradientStops.First().Color;
        var below = gradient.GradientStops.Last().Color;

        Assert.True(Luminance(top) > Luminance(below));
    }

    [AvaloniaFact]
    public void TheLightCardEdgeStaysFlat()
    {
        // A white card has nothing to lift its edge above, so light mode opts
        // out rather than drawing a gradient nobody can see.
        Assert.True(Application.Current!.TryGetResource(
            "AppCardBorderBrush", ThemeVariant.Light, out var value));

        Assert.IsAssignableFrom<ISolidColorBrush>(value);
    }

    [AvaloniaFact]
    public void DarkTextIsNotPureWhite()
    {
        Assert.True(Application.Current!.TryGetResource(
            "AppTextBrush", ThemeVariant.Dark, out var value));

        Assert.NotEqual(Colors.White, ((ISolidColorBrush)value!).Color);
    }

    private static WorkspaceViewModel Workspace(Window window) =>
        ((MainViewModel)window.DataContext!).Workspace!;

    private static double Luminance(Color color) =>
        (0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B);

    private static object? Resource(string key)
    {
        Application.Current!.TryGetResource(
            key, Application.Current.ActualThemeVariant, out var value);
        return value;
    }

    private static List<T> Descendants<T>(Visual root) where T : Visual =>
        [.. root.GetVisualDescendants().OfType<T>().Where(v => v.IsEffectivelyVisible)];

    private async Task<Window> OpenAsync(params string[] summaries)
    {
        var settings = AppSettings.Load(Path.Combine(_root, "settings.json"));
        var store = new TaskStore(new Core.Storage.Workspace(Path.Combine(_root, "ws")));
        var workspace = await WorkspaceViewModel.OpenAsync(store, settings, new NoPicker());
        _disposables.Add(workspace);

        var window = new MainWindow
        {
            DataContext = new MainViewModel(settings, new NoPicker(), new NoPicker())
            {
                Workspace = workspace,
            },
        };
        window.Show();

        foreach (var summary in summaries)
        {
            workspace.NewTaskSummary = summary;
            await workspace.CreateTaskCommand.ExecuteAsync(null!);
            Dispatcher.UIThread.RunJobs();
        }

        workspace.ShowListCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        window.Measure(window.ClientSize);
        window.Arrange(new Rect(window.ClientSize));
        Dispatcher.UIThread.RunJobs();

        return window;
    }

    private sealed class NoPicker : IFolderPicker, IFilePicker
    {
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title) => Task.FromResult<string?>(null);
    }
}
