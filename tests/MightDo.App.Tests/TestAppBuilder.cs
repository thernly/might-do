using Avalonia;
using Avalonia.Headless;
using MightDo.App;
using MightDo.Platform;

[assembly: AvaloniaTestApplication(typeof(MightDo.App.Tests.TestAppBuilder))]

namespace MightDo.App.Tests;

/// <summary>
/// Runs the real application — real XAML, real bindings, real visual tree —
/// without a display.
/// </summary>
/// <remarks>
/// The view-model tests prove the behaviour; these prove the views actually
/// load and bind to it. That is a distinct class of bug, and one nothing else
/// catches: a XAML file referencing a type that does not exist, or an
/// <c>x:DataType</c> that resolves a binding against the wrong object, compiles
/// or fails only when a window is shown.
/// </remarks>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        CrashDiagnostics.ConfigureLog(new ManagedCrashLog(Path.Combine(
            Path.GetTempPath(), $"might-do-headless-crashes-{Environment.ProcessId}.log")));

        return AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .WithInterFont();
    }
}
