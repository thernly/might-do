// Spike: does .NET 10's FileSystemWatcher on macOS reliably report the changes
// might-do depends on? ADR-0001 says the design silently loses data without
// live reload, so this is load-bearing.
//
// Each scenario performs real filesystem work against a real workspace layout
// and reports which events arrived.

using System.Collections.Concurrent;
using System.Diagnostics;

if (args.Length > 0 && args[0] == "latency")
{
    Latency.Run(args);
    return;
}

if (args.Length > 0 && args[0] == "notify")
{
    NotifySpike.Run();
    return;
}

if (args.Length > 1 && args[0] == "cloud")
{
    CloudSpike.Run(args[1]);
    return;
}

var root = Path.Combine(
    Path.GetTempPath(),
    "mightdo-watcher-spike-" + Guid.NewGuid().ToString("N")[..8]);
var tasks = Path.Combine(root, "tasks");
Directory.CreateDirectory(tasks);
Directory.CreateDirectory(Path.Combine(root, "attachments"));

Console.WriteLine($"root: {root}");
Console.WriteLine($"GetTempPath resolves to: {Path.GetTempPath()}");
Console.WriteLine($"realpath of root: {ResolveSymlinks(root)}");
Console.WriteLine();

var events = new ConcurrentQueue<(string Scenario, string Kind, string Path)>();
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

void Record(string kind, string fullPath) =>
    events.Enqueue((scenario, kind, Path.GetRelativePath(root, fullPath)));

watcher.Created += (_, e) => Record("Created", e.FullPath);
watcher.Changed += (_, e) => Record("Changed", e.FullPath);
watcher.Deleted += (_, e) => Record("Deleted", e.FullPath);
watcher.Renamed += (_, e) => Record("Renamed", e.FullPath);
watcher.Error += (_, e) => errors.Enqueue(e.GetException().GetType().Name
                                          + ": " + e.GetException().Message);
watcher.EnableRaisingEvents = true;

Console.WriteLine($"InternalBufferSize: {watcher.InternalBufferSize} bytes");
Console.WriteLine();

Thread.Sleep(500); // let the watcher settle before doing anything

var results = new List<(string Scenario, string Detail, int Count, string Kinds)>();

void Run(string name, string detail, Action action, int settleMs = 700)
{
    while (events.TryDequeue(out _)) { }
    scenario = name;
    var sw = Stopwatch.StartNew();
    action();
    Thread.Sleep(settleMs);
    sw.Stop();

    var seen = events.Where(e => e.Scenario == name).ToList();
    var kinds = seen.Count == 0
        ? "(none)"
        : string.Join(", ", seen.GroupBy(e => e.Kind)
                                .OrderBy(g => g.Key)
                                .Select(g => $"{g.Key}x{g.Count()}"));
    results.Add((name, detail, seen.Count, kinds));

    Console.WriteLine($"── {name}: {detail}");
    Console.WriteLine($"   {seen.Count} event(s) in {sw.ElapsedMilliseconds}ms: {kinds}");
    foreach (var e in seen.Take(8))
        Console.WriteLine($"     {e.Kind,-8} {e.Path}");
    if (seen.Count > 8) Console.WriteLine($"     … +{seen.Count - 8} more");
    Console.WriteLine();
}

const string Ulid = "01m07z000000000000000000t1";
var taskPath = Path.Combine(tasks, $"{Ulid}.json");

// 1. A plain external create — a sync client dropping in a new task.
Run("create", "external process creates tasks/<ulid>.json",
    () => File.WriteAllText(taskPath, """{"summary":"one"}"""));

// 2. Our own write path: temp file, then rename over the target.
Run("atomic-overwrite", "write .tmp then File.Move(overwrite: true)", () =>
{
    var tmp = taskPath + ".tmp";
    File.WriteAllText(tmp, """{"summary":"two"}""");
    File.Move(tmp, taskPath, overwrite: true);
});

// 3. In-place modification, as a text editor would do.
Run("modify-in-place", "rewrite an existing file's contents",
    () => File.WriteAllText(taskPath, """{"summary":"three"}"""));

// 4. Delete.
Run("delete", "delete a task file", () => File.Delete(taskPath));

// 5. A sync conflict artefact, with spaces and parentheses in the name.
Run("conflict-artefact", "create '<ulid> (conflicted copy 2026-08-16).json'",
    () => File.WriteAllText(
        Path.Combine(tasks, $"{Ulid} (conflicted copy 2026-08-16).json"),
        "{}"));

// 6. Case sensitivity: does a differently-cased path still report?
Run("uppercase-name", "create a task file with an UPPERCASE ULID name",
    () => File.WriteAllText(
        Path.Combine(tasks, "01M07Z000000000000000000T2.json"), "{}"));

// 7. Burst — the buffer-overflow risk. A sync client landing a folder at once.
const int burst = 400;
Run("burst", $"create {burst} files as fast as possible", () =>
{
    for (var i = 0; i < burst; i++)
        File.WriteAllText(Path.Combine(tasks, $"burst-{i:D4}.json"), "{}");
}, settleMs: 2500);

// 8. Rename within the watched tree — the shape of a trash move.
Run("rename-within-tree", "move a file from tasks/ into .trash/tasks/", () =>
{
    var trash = Path.Combine(root, ".trash", "tasks");
    Directory.CreateDirectory(trash);
    var src = Path.Combine(tasks, "burst-0000.json");
    File.Move(src, Path.Combine(trash, "burst-0000.json"));
}, settleMs: 900);

// 9. The whole directory replaced underneath us — the worst sync case.
Run("directory-swap", "replace tasks/ wholesale with a fresh directory", () =>
{
    var stale = Path.Combine(root, "tasks-old");
    Directory.Move(tasks, stale);
    Directory.CreateDirectory(tasks);
    File.WriteAllText(Path.Combine(tasks, $"{Ulid}.json"), """{"summary":"new"}""");
}, settleMs: 1200);

// 10. Does the watcher still work after that swap?
Run("after-directory-swap", "create a file in the replaced tasks/",
    () => File.WriteAllText(
        Path.Combine(tasks, "01m07z000000000000000000t3.json"), "{}"));

Console.WriteLine(new string('═', 78));
Console.WriteLine("SUMMARY");
Console.WriteLine(new string('═', 78));
foreach (var (name, detail, count, kinds) in results)
{
    var verdict = count > 0 ? "OK  " : "MISS";
    Console.WriteLine($"{verdict} {name,-22} {count,4} event(s)  {kinds}");
}

Console.WriteLine();
Console.WriteLine(errors.IsEmpty
    ? "No Error events (no buffer overflow)."
    : $"Error events: {errors.Count}");
foreach (var e in errors.Distinct()) Console.WriteLine($"  {e}");

Directory.Delete(root, recursive: true);

static string ResolveSymlinks(string path)
{
    try { return Path.GetFullPath(new DirectoryInfo(path).LinkTarget ?? path); }
    catch { return path; }
}
