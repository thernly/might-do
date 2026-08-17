// Writes fixtures/interop/dotnet-written/ — a workspace produced by
// MightDo.Core rather than by the Flutter implementation.
//
// The rest of the conformance suite proves one direction: Flutter wrote
// fixtures/workspace-v1/, and .NET reads it and writes it back without losing a
// value. This closes the other direction. The Dart test
// test/format/interop_test.dart reads what this writes and asserts it
// normalises to the canonical form, so "the two implementations can share a
// folder" is verified rather than assumed.
//
//   dotnet run --project tools/MightDo.FixtureWriter
//
// The output is committed. Regenerate it whenever the .NET serialization
// changes; the Dart test will fail loudly if it drifts.

using MightDo.Core.Domain;
using MightDo.Core.Serialization;
using MightDo.Core.Storage;

var repoRoot = FindRepoRoot();
var source = new Workspace(Path.Combine(repoRoot, "fixtures", "workspace-v1"));
var destination = Path.Combine(repoRoot, "fixtures", "interop", "dotnet-written");

if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);

var target = new Workspace(destination);
target.EnsureLayout();

// Read the canonical corpus through MightDo.Core, then write it back out with
// MightDo.Core's own serializer. Every value the Flutter implementation wrote
// makes the round trip through .NET types on the way.
var config = await WorkspaceFiles.ReadJsonAsync<WorkspaceConfig>(source.ConfigFile)
             ?? throw new InvalidOperationException($"no config at {source.ConfigFile}");
await WorkspaceFiles.WriteJsonAtomicAsync(target.ConfigFile, config);

var written = 0;
foreach (var path in Directory.EnumerateFiles(source.TasksDir).Order(StringComparer.Ordinal))
{
    var name = Path.GetFileName(path);
    if (!WorkspaceFiles.IsOwnTaskFile(name)) continue;

    var task = await WorkspaceFiles.ReadJsonAsync<MightDoTask>(path)
               ?? throw new InvalidOperationException($"empty task file: {name}");
    await WorkspaceFiles.WriteJsonAtomicAsync(target.TaskFile(task.Id), task);
    written++;
}

// The trashed task too — same shape, different folder, and worth proving.
foreach (var path in Directory.EnumerateFiles(source.TrashTasksDir).Order(StringComparer.Ordinal))
{
    if (!WorkspaceFiles.IsOwnTaskFile(Path.GetFileName(path))) continue;

    var task = await WorkspaceFiles.ReadJsonAsync<MightDoTask>(path)
               ?? throw new InvalidOperationException($"empty trashed task: {path}");
    await WorkspaceFiles.WriteJsonAtomicAsync(target.TrashedTaskFile(task.Id), task);
}

// A task this app created from scratch, rather than one it merely re-serialised.
// Ids and timestamps are fixed so the committed output is stable across runs.
var native = new MightDoTask
{
    Id = "01m07z000000000000000000n1",
    Summary = "Written by MightDo.Core 🎉",
    Description = "Created by the .NET implementation, not round-tripped.\n"
                  + "Ampersands & <angle brackets> and a café.",
    StatusId = config.DefaultStatusId,
    CategoryId = config.Categories[0].Id,
    Priority = Priority.High,
    DueDate = new CalendarDate(2026, 9, 1),
    EstimateMinutes = 45,
    BoardRank = Rank.Between("h", "i"),
    Steps = [new Step("01m07z000000000000000000n2", "A step", Done: true)],
    Notes =
    [
        new Note(
            "01m07z000000000000000000n3",
            new DateTime(2026, 8, 17, 9, 30, 0, DateTimeKind.Utc),
            "A note written by .NET."),
    ],
    Reminders =
    [
        new Reminder(
            "01m07z000000000000000000n4",
            new DateTime(2026, 8, 20, 8, 0, 0, DateTimeKind.Utc)),
    ],
    CreatedAt = new DateTime(2026, 8, 17, 9, 0, 0, DateTimeKind.Utc),
    UpdatedAt = new DateTime(2026, 8, 17, 9, 30, 0, DateTimeKind.Utc),
}.WithTags([config.Tags[0].Id, config.Tags[1].Id]);
await WorkspaceFiles.WriteJsonAtomicAsync(target.TaskFile(native.Id), native);
written++;

Console.WriteLine($"wrote {written} task files to {destination}");

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "fixtures"))) return dir.FullName;
        dir = dir.Parent;
    }

    throw new DirectoryNotFoundException("no 'fixtures' directory above the executable");
}
