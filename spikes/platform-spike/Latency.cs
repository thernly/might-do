// Second half of the watcher spike: how fast do events arrive, how many fire
// for one logical save, and does the watcher survive its root disappearing?
//
// The last one models an unmounted drive or a OneDrive folder being moved —
// which, unlike the happy path, decides whether we need re-arming logic.

using System.Collections.Concurrent;
using System.Diagnostics;

static class Latency
{
    public static void Run(string[] args)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "mightdo-latency-" + Guid.NewGuid().ToString("N")[..8]);
        var tasks = Path.Combine(root, "tasks");
        Directory.CreateDirectory(tasks);

        var signal = new ConcurrentQueue<(string Path, long Ticks)>();
        var errors = new ConcurrentQueue<string>();

        using var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                         | NotifyFilters.DirectoryName
                         | NotifyFilters.LastWrite
                         | NotifyFilters.Size,
        };
        void On(object _, FileSystemEventArgs e) =>
            signal.Enqueue((e.FullPath, Stopwatch.GetTimestamp()));
        watcher.Created += On;
        watcher.Changed += On;
        watcher.Deleted += On;
        watcher.Renamed += On;
        watcher.Error += (_, e) => errors.Enqueue(e.GetException().Message);
        watcher.EnableRaisingEvents = true;
        Thread.Sleep(500);

        // ---- 1. Latency and event count for one atomic save -----------------
        Console.WriteLine("── latency of one atomic save (temp file + rename)");
        var latencies = new List<double>();
        var counts = new List<int>();

        for (var i = 0; i < 12; i++)
        {
            while (signal.TryDequeue(out _)) { }
            var target = Path.Combine(tasks, $"01m07z00000000000000000t{i:D2}.json");
            var tmp = target + ".tmp";

            var start = Stopwatch.GetTimestamp();
            File.WriteAllText(tmp, """{"summary":"save"}""");
            File.Move(tmp, target, overwrite: true);

            Thread.Sleep(600);
            var seen = signal.ToArray();
            if (seen.Length > 0)
            {
                var first = seen.Min(s => s.Ticks);
                latencies.Add((first - start) * 1000.0 / Stopwatch.Frequency);
            }
            counts.Add(seen.Length);
        }

        Console.WriteLine($"   samples with an event: {latencies.Count}/12");
        if (latencies.Count > 0)
        {
            Console.WriteLine($"   first-event latency: min {latencies.Min():F0}ms  " +
                              $"median {Median(latencies):F0}ms  max {latencies.Max():F0}ms");
        }
        Console.WriteLine($"   events per logical save: min {counts.Min()}  " +
                          $"median {Median(counts.Select(c => (double)c).ToList()):F0}  " +
                          $"max {counts.Max()}");
        Console.WriteLine("   (one save is one user action — anything above 1 is why");
        Console.WriteLine("    a debounce is mandatory, not an optimisation)");
        Console.WriteLine();

        // ---- 2. Root deleted, then recreated at the same path ---------------
        Console.WriteLine("── watched root deleted and recreated (unmounted drive / moved folder)");
        while (signal.TryDequeue(out _)) { }
        Directory.Delete(root, recursive: true);
        Thread.Sleep(800);
        Console.WriteLine($"   events during delete: {signal.Count}");
        Console.WriteLine($"   Error events: {errors.Count}" +
                          (errors.IsEmpty ? "" : " -> " + string.Join("; ", errors)));
        Console.WriteLine($"   EnableRaisingEvents still true: {watcher.EnableRaisingEvents}");

        Directory.CreateDirectory(tasks);
        Thread.Sleep(800);
        while (signal.TryDequeue(out _)) { }

        File.WriteAllText(Path.Combine(tasks, "01m07z000000000000000000zz.json"), "{}");
        Thread.Sleep(1000);
        var recovered = signal.Count;
        Console.WriteLine($"   events after recreating the root and writing: {recovered}");
        Console.WriteLine(recovered > 0
            ? "   VERDICT: watcher recovered on its own."
            : "   VERDICT: watcher is DEAD. Re-arming logic is required.");
        Console.WriteLine();

        // ---- 3. Does a fresh watcher on the recreated path work? -----------
        if (recovered == 0)
        {
            using var second = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            };
            var again = new ConcurrentQueue<string>();
            second.Created += (_, e) => again.Enqueue(e.FullPath);
            second.Changed += (_, e) => again.Enqueue(e.FullPath);
            second.EnableRaisingEvents = true;
            Thread.Sleep(500);
            File.WriteAllText(Path.Combine(tasks, "01m07z000000000000000000yy.json"), "{}");
            Thread.Sleep(1000);
            Console.WriteLine($"── a NEW watcher on the same path: {again.Count} event(s)");
            Console.WriteLine(again.IsEmpty
                ? "   Even a fresh watcher sees nothing."
                : "   A fresh watcher works, so recovery = dispose and recreate.");
            Console.WriteLine();
        }

        try { Directory.Delete(root, recursive: true); } catch { }
    }

    static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        return sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }
}
