using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MightDo.App.ViewModels;
using MightDo.App.Views;
using MightDo.Core.Session;
using MightDo.Core.Storage;
using MightDo.Platform;

namespace MightDo.App.Tests;

/// <summary>
/// The trash section of the settings window, driven through the real view.
/// </summary>
/// <remarks>
/// The Restore button reaches its command through a $parent[ItemsControl]
/// cast binding, which no compiler checks — the view-model tests would pass
/// with the button wired to nothing.
/// </remarks>
public class TrashSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mightdo-trashui-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact]
    public async Task TheRestoreButtonIsWiredToItsRow()
    {
        var session = await WorkspaceSession.OpenAsync(
            new TaskStore(new Core.Storage.Workspace(_root)));
        _disposables.Add(session);

        var task = await session.CreateTaskAsync("Take me back");
        await session.TrashTaskAsync(task);

        var vm = new SettingsViewModel(
            session, AppSettings.Load(Path.Combine(_root, "settings.json")));
        _disposables.Add(vm);
        await vm.RefreshTrashCommand.ExecuteAsync(null!);

        var window = new SettingsWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.Measure(window.ClientSize);
        window.Arrange(new Rect(window.ClientSize));

        var restore = window.GetVisualDescendants().OfType<Button>()
            .Single(button => button.IsEffectivelyVisible
                && button.GetVisualDescendants().OfType<TextBlock>()
                    .Any(block => block.Text == "Restore"));

        Assert.NotNull(restore.Command);
        restore.Command!.Execute(restore.CommandParameter);
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(50); // the command is async behind the ICommand facade
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(session.Snapshot.TaskById(task.Id));
        window.Close();
    }
}
