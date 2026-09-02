using System.Runtime.InteropServices;
using System.Text;

namespace MightDo.Platform;

/// <summary>What the application knew when an exception escaped its normal boundary.</summary>
public sealed record ManagedCrashContext(
    string Origin,
    string Build,
    string? Operation = null,
    string? TaskId = null);

/// <summary>
/// Appends managed exception details to a local file without ever becoming a
/// second reason for the process to fail.
/// </summary>
/// <remarks>
/// Apple's crash report can show that CoreCLR terminated over an unhandled
/// exception, but JIT-compiled frames have no image for Crash Reporter to
/// symbolicate. This log keeps the exception type, message and managed stack
/// that are otherwise gone by the time the report is written.
/// </remarks>
public sealed class ManagedCrashLog(string path)
{
    private readonly Lock _gate = new();

    /// <summary>Beside settings.json, in the application's private data folder.</summary>
    public static string DefaultPath
    {
        get
        {
            var settings = System.IO.Path.GetFullPath(AppSettings.DefaultPath);
            return System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(settings)!, "managed-crashes.log");
        }
    }

    public string Path { get; } = path;

    /// <summary>
    /// Records one exception. Failure to create or append the log is ignored:
    /// this method is called while another exception is already escaping.
    /// </summary>
    public void Record(Exception exception, ManagedCrashContext context)
    {
        try
        {
            var entry = Format(exception, context);

            lock (_gate)
            {
                var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
                if (directory is not null) Directory.CreateDirectory(directory);
                File.AppendAllText(Path, entry, Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must not replace the exception they were trying to keep.
        }
    }

    internal static string Format(Exception exception, ManagedCrashContext context)
    {
        var entry = new StringBuilder();
        entry.AppendLine("--- managed exception ---");
        entry.AppendLine($"utc: {DateTimeOffset.UtcNow:O}");
        entry.AppendLine($"origin: {context.Origin}");
        entry.AppendLine($"build: {context.Build}");
        entry.AppendLine($"process: {Environment.ProcessId}");
        entry.AppendLine($"managed-thread: {Environment.CurrentManagedThreadId}");
        entry.AppendLine($"runtime: {RuntimeInformation.FrameworkDescription}");
        entry.AppendLine($"os: {RuntimeInformation.OSDescription}");
        entry.AppendLine($"architecture: {RuntimeInformation.ProcessArchitecture}");

        if (!string.IsNullOrWhiteSpace(context.Operation))
        {
            entry.AppendLine($"operation: {context.Operation}");
        }

        if (!string.IsNullOrWhiteSpace(context.TaskId))
        {
            entry.AppendLine($"task-id: {context.TaskId}");
        }

        entry.AppendLine("exception:");
        entry.AppendLine(exception.ToString());
        entry.AppendLine();
        return entry.ToString();
    }
}
