// Spike: can an unsigned, unbundled .NET process raise a real macOS
// notification?
//
// The maintained-library answer is "no library does this on macOS" — the whole
// DesktopNotifications lineage is archived with macOS unimplemented. The native
// API (UNUserNotificationCenter) requires a signed, bundled .app, and code
// signing is deferred per README. So the question is what works *without* that.

using System.Diagnostics;

static class NotifySpike
{
    public static void Run()
    {
        Console.WriteLine($"ProcessPath: {Environment.ProcessPath}");
        Console.WriteLine($"Inside a .app bundle: " +
                          $"{Environment.ProcessPath?.Contains(".app/Contents/") == true}");
        Console.WriteLine($"Bundle id available: {Environment.GetEnvironmentVariable("__CFBundleIdentifier") ?? "(none)"}");
        Console.WriteLine();

        Try("osascript display notification",
            "osascript",
            ["-e", "display notification \"Reminder: Draft the quarterly plan\" " +
                   "with title \"might-do\" subtitle \"due today\""]);

        // terminal-notifier is the usual third-party route; check availability
        // rather than assume it, since it is not installed by default.
        Try("terminal-notifier present?", "which", ["terminal-notifier"]);
    }

    static void Try(string label, string exe, string[] args)
    {
        Console.WriteLine($"── {label}");
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd().Trim();
            var stderr = proc.StandardError.ReadToEnd().Trim();
            proc.WaitForExit(10_000);

            Console.WriteLine($"   exit code: {proc.ExitCode}");
            if (stdout.Length > 0) Console.WriteLine($"   stdout: {stdout}");
            if (stderr.Length > 0) Console.WriteLine($"   stderr: {stderr}");
            Console.WriteLine(proc.ExitCode == 0
                ? "   OK"
                : "   FAILED");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   threw {ex.GetType().Name}: {ex.Message}");
        }
        Console.WriteLine();
    }
}
