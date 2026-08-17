// Runs the watcher scenarios against a real cloud-sync folder.
//
// ~/Library/CloudStorage/OneDrive-Personal is a macOS File Provider volume, not
// an ordinary filesystem: files can be dataless placeholders and the kernel
// events FSEvents reports come from an extension rather than a local disk. This
// is the environment ADR-0001 expects a workspace to live in, and the one where
// FileSystemWatcher is most often reported to fail — so the happy-path results
// from a /var/folders temp dir do not transfer. Hence a real test.
//
// Creates one temp subfolder, exercises it, deletes it.

using System.Collections.Concurrent;
using System.Diagnostics;

static class CloudSpike
{
    public static void Run(string basePath)
    {
        if (!Directory.Exists(basePath))
        {
            Console.WriteLine($"base path does not exist: {basePath}");
            return;
        }

        var root = Path.Combine(basePath, "mightdo-watcher-spike-" + Guid.NewGuid().ToString("N")[..8]);
        var tasks = Path.Combine(root, "tasks");

        Console.WriteLine($"base:  {basePath}");
        Console.WriteLine($"root:  {root}");
        Directory.CreateDirectory(tasks);

        try
        {
            Exercise(root, tasks);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
                Console.WriteLine();
                Console.WriteLine($"cleaned up: {!Directory.Exists(root)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CLEANUP FAILED, remove manually: {root} ({ex.Message})");
            }
        }
    }

    static void Exercise(string root, string tasks)
    {
        var events = new ConcurrentQueue<(string Scenario, string Kind, string Path, long Ticks)>();
        var errors = new ConcurrentQueue<string>();
        var scenario = "startup";

        using var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                         | NotifyFilters.DirectoryName
                         | NotifyFilters.LastWrite
                         | NotifyFilters.Size,
        };
        void Record(string kind, string full) =>
            events.Enqueue((scenario, kind, Path.GetRelativePath(root, full), Stopwatch.GetTimestamp()));

        watcher.Created += (_, e) => Record("Created", e.FullPath);
        watcher.Changed += (_, e) => Record("Changed", e.FullPath);
        watcher.Deleted += (_, e) => Record("Deleted", e.FullPath);
        watcher.Renamed += (_, e) => Record("Renamed", e.FullPath);
        watcher.Error += (_, e) => errors.Enqueue(
            e.GetException().GetType().Name + ": " + e.GetException().Message);
        watcher.EnableRaisingEvents = true;
        Thread.Sleep(1000); // File Provider volumes may need longer to attach

        var summary = new List<(string Name, int Count, string Kinds, double LatencyMs)>();

        void Run(string name, string detail, Action action, int settleMs = 1500)
        {
            while (events.TryDequeue(out _)) { }
            scenario = name;
            var start = Stopwatch.GetTimestamp();
            action();
            Thread.Sleep(settleMs);

            var seen = events.Where(e => e.Scenario == name).ToList();
            var latency = seen.Count == 0
                ? -1
                : (seen.Min(e => e.Ticks) - start) * 1000.0 / Stopwatch.Frequency;
            var kinds = seen.Count == 0
                ? "(none)"
                : string.Join(", ", seen.GroupBy(e => e.Kind).OrderBy(g => g.Key)
                                        .Select(g => $"{g.Key}x{g.Count()}"));
            summary.Add((name, seen.Count, kinds, latency));

            Console.WriteLine($"── {name}: {detail}");
            Console.WriteLine(seen.Count == 0
                ? "   NO EVENTS"
                : $"   {seen.Count} event(s), first after {latency:F0}ms: {kinds}");
            foreach (var e in seen.Take(5)) Console.WriteLine($"     {e.Kind,-8} {e.Path}");
            Console.WriteLine();
        }

        const string Ulid = "01m07z000000000000000000t1";
        var taskPath = Path.Combine(tasks, $"{Ulid}.json");

        Run("create", "create tasks/<ulid>.json",
            () => File.WriteAllText(taskPath, """{"summary":"one"}"""));

        Run("atomic-overwrite", "write .tmp then File.Move(overwrite: true)", () =>
        {
            var tmp = taskPath + ".tmp";
            File.WriteAllText(tmp, """{"summary":"two"}""");
            File.Move(tmp, taskPath, overwrite: true);
        });

        Run("modify-in-place", "rewrite contents",
            () => File.WriteAllText(taskPath, """{"summary":"three"}"""));

        Run("conflict-artefact", "create '<ulid>-LAPTOP.json'",
            () => File.WriteAllText(Path.Combine(tasks, $"{Ulid}-LAPTOP.json"), "{}"));

        Run("delete", "delete a task file", () => File.Delete(taskPath));

        Console.WriteLine(new string('═', 74));
        Console.WriteLine($"{"scenario",-20} {"events",7}  {"latency",8}  kinds");
        Console.WriteLine(new string('─', 74));
        foreach (var (name, count, kinds, latency) in summary)
        {
            var lat = latency < 0 ? "  n/a" : $"{latency:F0}ms";
            Console.WriteLine($"{name,-20} {count,7}  {lat,8}  {kinds}");
        }
        Console.WriteLine(new string('═', 74));

        var missed = summary.Where(s => s.Count == 0).Select(s => s.Name).ToList();
        Console.WriteLine(missed.Count == 0
            ? "VERDICT: every change was reported on this cloud volume."
            : $"VERDICT: MISSED {missed.Count} scenario(s): {string.Join(", ", missed)}");
        Console.WriteLine(errors.IsEmpty ? "No Error events." : $"Error events: {errors.Count}");
        foreach (var e in errors.Distinct()) Console.WriteLine($"  {e}");
    }
}
