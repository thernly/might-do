using System.Diagnostics;
using System.Text;
using MightDo.Core.Domain;
using MightDo.Core.Reminders;

namespace MightDo.Platform;

/// <summary>
/// Chooses the best available OS notifier for this machine.
/// </summary>
/// <remarks>
/// Per ADR-0004 the in-app banner is the contract and an OS notification is
/// opportunistic, so returning <see cref="NullReminderNotifier"/> is a supported
/// outcome rather than a failure.
/// </remarks>
public static class ReminderNotifiers
{
    public static IReminderNotifier ForCurrentPlatform()
    {
        if (OperatingSystem.IsMacOS()) return new MacOsReminderNotifier();
        if (OperatingSystem.IsLinux()) return new LinuxReminderNotifier();

        // Windows toast needs a packaged app with an AppUserModelID, which is
        // the same signing-and-bundling work already deferred for macOS's native
        // API. The in-app panel covers it until that lands.
        return NullReminderNotifier.Instance;
    }
}

/// <summary>Builds the text a reminder shows.</summary>
public static class ReminderText
{
    /// <summary>
    /// The detail line: when it is due, else the first line of the description,
    /// else a bare word.
    /// </summary>
    public static string Body(MightDoTask task)
    {
        if (task.DueDate is { } due) return $"Due {due.ToIso()}";

        var firstLine = task.Description
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?
            .Trim();

        return string.IsNullOrEmpty(firstLine) ? "Reminder" : firstLine;
    }
}

/// <summary>
/// Raises a macOS notification by way of <c>osascript</c>.
/// </summary>
/// <remarks>
/// The native API, <c>UNUserNotificationCenter</c>, requires a signed and
/// bundled <c>.app</c>, and no maintained cross-platform package implements it —
/// see ADR-0004. This works from an unsigned, unbundled process, at the cost of
/// the banner being attributed to Script Editor rather than to might-do. That
/// goes away when the app is signed and bundled.
/// </remarks>
public sealed class MacOsReminderNotifier : IReminderNotifier
{
    public async Task NotifyAsync(
        MightDoTask task, Reminder reminder, CancellationToken cancellationToken)
    {
        var script =
            $"display notification \"{AppleScript.Quote(ReminderText.Body(task))}\" "
            + $"with title \"{AppleScript.Quote("might-do")}\" "
            + $"subtitle \"{AppleScript.Quote(task.Summary)}\"";

        await ShellNotifier.RunAsync("osascript", ["-e", script], cancellationToken);
    }
}

/// <summary>
/// Raises a Linux notification by way of <c>notify-send</c>, which talks to
/// whatever the desktop's D-Bus notification service is.
/// </summary>
/// <remarks>
/// Arguments go through <c>ArgumentList</c> rather than a shell, so no quoting
/// is needed and no summary can be interpreted as a command. Untested on this
/// machine — see ADR-0004 for why a missing notifier is not a broken feature.
/// </remarks>
public sealed class LinuxReminderNotifier : IReminderNotifier
{
    public async Task NotifyAsync(
        MightDoTask task, Reminder reminder, CancellationToken cancellationToken) =>
        await ShellNotifier.RunAsync(
            "notify-send",
            ["--app-name=might-do", task.Summary, ReminderText.Body(task)],
            cancellationToken);
}

/// <summary>
/// Escaping for values interpolated into an AppleScript source string.
/// </summary>
/// <remarks>
/// A task summary is arbitrary user text and goes straight into a script we
/// hand to <c>osascript</c>. A stray double quote would break the script, and a
/// crafted one could append statements to it — so this is the boundary that has
/// to be right, and it is kept separate from the process launch so it can be
/// tested without spawning anything.
/// </remarks>
public static class AppleScript
{
    public static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 8);

        foreach (var c in value)
        {
            switch (c)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                default:
                    // An AppleScript string literal cannot span lines, and a
                    // control character in a notification is meaningless anyway.
                    builder.Append(char.IsControl(c) ? ' ' : c);
                    break;
            }
        }

        return builder.ToString();
    }
}

internal static class ShellNotifier
{
    /// <summary>
    /// Runs a notification helper, treating any failure as "no notification".
    /// </summary>
    internal static async Task RunAsync(
        string fileName, string[] arguments, CancellationToken cancellationToken)
    {
        try
        {
            var info = new ProcessStartInfo(fileName)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            if (process is null) return;

            await process.WaitForExitAsync(cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The helper may not exist, or may be refused. ADR-0004 makes the OS
            // banner best-effort; the reminder is already in the in-app panel.
        }
    }
}
