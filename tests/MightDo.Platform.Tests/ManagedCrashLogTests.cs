using MightDo.Platform;

namespace MightDo.Platform.Tests;

public sealed class ManagedCrashLogTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "mightdo-crashlog-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void KeepsTheManagedExceptionAndItsApplicationContext()
    {
        var path = Path.Combine(_root, "logs", "managed-crashes.log");
        var log = new ManagedCrashLog(path);
        var error = Capture();

        log.Record(error, new ManagedCrashContext(
            "Avalonia.Dispatcher.UnhandledException",
            "1.0.0+abc123",
            "OnSummaryChanged",
            "01K4TASK"));

        var entry = File.ReadAllText(path);
        Assert.Contains("origin: Avalonia.Dispatcher.UnhandledException", entry);
        Assert.Contains("build: 1.0.0+abc123", entry);
        Assert.Contains("operation: OnSummaryChanged", entry);
        Assert.Contains("task-id: 01K4TASK", entry);
        Assert.Contains("System.InvalidOperationException: write exploded", entry);
        Assert.Contains(nameof(Capture), entry);
    }

    [Fact]
    public void ALogThatCannotBeWrittenNeverReplacesTheOriginalFailure()
    {
        var fileWhereDirectoryBelongs = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(fileWhereDirectoryBelongs, "occupied");
        var log = new ManagedCrashLog(Path.Combine(fileWhereDirectoryBelongs, "crash.log"));

        var thrown = Record.Exception(() => log.Record(
            new InvalidOperationException("original"),
            new ManagedCrashContext("test", "test-build")));

        Assert.Null(thrown);
    }

    private static Exception Capture()
    {
        try
        {
            throw new InvalidOperationException("write exploded");
        }
        catch (Exception error)
        {
            return error;
        }
    }
}
