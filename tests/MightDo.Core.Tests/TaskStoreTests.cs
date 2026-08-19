using MightDo.Core.Domain;
using MightDo.Core.Storage;

namespace MightDo.Core.Tests;

public class TaskStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mightdo-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Copies the canonical fixture workspace somewhere writable.</summary>
    private Workspace CopyFixtureWorkspace()
    {
        CopyDirectory(Fixtures.Path("workspace-v1"), _root);
        return new Workspace(_root);
    }

    [Fact]
    public async Task LoadsTheFixtureWorkspace()
    {
        var store = new TaskStore(CopyFixtureWorkspace());

        var loaded = await store.LoadAsync();

        Assert.Empty(loaded.Failures);
        Assert.Equal(5, loaded.Tasks.Count);
        Assert.Equal(6, loaded.Config.Statuses.Count);
    }

    [Fact]
    public async Task ReportsConflictArtefactsInsteadOfLoadingOrIgnoringThem()
    {
        var store = new TaskStore(CopyFixtureWorkspace());
        using var vector = Fixtures.ReadDocument("vectors", "conflicts.json");

        var loaded = await store.LoadAsync();

        var expected = vector.RootElement.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(expected.Count, loaded.Conflicts.Count);

        foreach (var entry in expected)
        {
            var fileName = entry.GetProperty("fileName").GetString()!;
            var taskIdProperty = entry.GetProperty("taskId");
            var expectedTaskId = taskIdProperty.ValueKind == System.Text.Json.JsonValueKind.Null
                ? null
                : taskIdProperty.GetString();

            var conflict = Assert.Single(loaded.Conflicts, c => c.FileName == fileName);
            Assert.Equal(expectedTaskId, conflict.TaskId);
        }
    }

    [Fact]
    public async Task KeepsTrashedTasksOutOfTheOrdinaryLoad()
    {
        var store = new TaskStore(CopyFixtureWorkspace());

        var loaded = await store.LoadAsync();
        var trashed = await store.LoadTrashAsync();

        Assert.DoesNotContain(loaded.Tasks, t => t.Id == "01m07z000000000000000000t6");
        Assert.Equal("01m07z000000000000000000t6", Assert.Single(trashed).Id);
    }

    [Fact]
    public async Task SeedsAFreshWorkspace()
    {
        var workspace = new Workspace(Path.Combine(_root, "fresh"));
        var store = new TaskStore(workspace);

        var config = await store.InitialiseAsync();

        Assert.True(workspace.IsInitialised);
        Assert.True(Directory.Exists(workspace.TasksDir));
        Assert.True(Directory.Exists(workspace.TrashTasksDir));
        Assert.Equal(StatusType.Initial, config.StatusById(config.DefaultStatusId)!.Type);

        // Initialising again must not re-seed over the user's edits.
        var renamed = config with
        {
            Statuses = [.. config.Statuses.Select(s =>
                s.Id == config.DefaultStatusId ? s with { Name = "Renamed" } : s)],
        };
        await store.SaveConfigAsync(renamed);

        var reloaded = await store.InitialiseAsync();
        Assert.Equal("Renamed", reloaded.StatusById(reloaded.DefaultStatusId)!.Name);
    }

    [Fact]
    public async Task SavesAndReloadsATaskWithoutLosingAnything()
    {
        var store = new TaskStore(new Workspace(_root));
        await store.InitialiseAsync();

        var original = MightDoTask.Create(
            summary: "Draft the quarterly plan 🎉",
            statusId: "01m07z000000000000000000s3",
            boardRank: Rank.First,
            description: "Ampersands & <angle brackets>",
            dueDate: new CalendarDate(2026, 8, 21),
            estimateMinutes: 240) with
        {
            Notes = [Note.Create("First note")],
            Reminders = [Reminder.Create(new DateTime(2026, 8, 20, 8, 0, 0, DateTimeKind.Utc))],
        };

        await store.SaveTaskAsync(original);
        var reloaded = await store.LoadTaskAsync(original.Id);

        Assert.NotNull(reloaded);
        Assert.Equal(original.Summary, reloaded.Summary);
        Assert.Equal(original.Description, reloaded.Description);
        Assert.Equal(original.DueDate, reloaded.DueDate);
        Assert.Equal(original.BoardRank, reloaded.BoardRank);
        Assert.Equal(original.Notes.Single().Body, reloaded.Notes.Single().Body);
        Assert.Equal(original.Reminders.Single().RemindAt, reloaded.Reminders.Single().RemindAt);
    }

    [Fact]
    public async Task WritesTheTaskFileUnderItsUlidAndLeavesNoTempBehind()
    {
        var store = new TaskStore(new Workspace(_root));
        await store.InitialiseAsync();
        var task = MightDoTask.Create("Buy milk", "s", Rank.First);

        await store.SaveTaskAsync(task);

        var files = Directory.GetFiles(store.Workspace.TasksDir).Select(Path.GetFileName).ToList();
        Assert.Equal([$"{task.Id}.json"], files);
        Assert.True(WorkspaceFiles.IsOwnTaskFile($"{task.Id}.json"));
    }

    [Fact]
    public async Task TrashingMovesTheTaskAndItsAttachmentsRatherThanDeleting()
    {
        var store = new TaskStore(new Workspace(_root));
        await store.InitialiseAsync();

        const string storedName = "01m07z000000000000000000a1-plan.pdf";
        await File.WriteAllTextAsync(
            store.Workspace.AttachmentFile(storedName), "attachment bytes");

        var task = MightDoTask.Create("Has an attachment", "s", Rank.First) with
        {
            Attachments =
            [
                new Attachment(Ulid.New(), "plan.pdf", storedName, 15, DateTime.UtcNow),
            ],
        };
        await store.SaveTaskAsync(task);

        await store.TrashTaskAsync(task);

        Assert.False(File.Exists(store.Workspace.TaskFile(task.Id)));
        Assert.False(File.Exists(store.Workspace.AttachmentFile(storedName)));
        Assert.True(File.Exists(store.Workspace.TrashedTaskFile(task.Id)));
        Assert.True(File.Exists(
            Path.Combine(store.Workspace.TrashAttachmentsDir, storedName)));

        // Nothing is destroyed, so it can come back.
        var restored = await store.RestoreTaskAsync(task.Id);
        Assert.Equal(task.Id, restored!.Id);
        Assert.True(File.Exists(store.Workspace.TaskFile(task.Id)));

        // The attachment travels both ways: trashing took it, restoring must
        // bring it back, or the restored task points at nothing.
        Assert.True(File.Exists(store.Workspace.AttachmentFile(storedName)));
        Assert.False(File.Exists(
            Path.Combine(store.Workspace.TrashAttachmentsDir, storedName)));
    }

    [Fact]
    public async Task TrashingTwiceDoesNotClobberTheFirstCopy()
    {
        var store = new TaskStore(new Workspace(_root));
        await store.InitialiseAsync();
        var task = MightDoTask.Create("Recreated after trashing", "s", Rank.First);

        await store.SaveTaskAsync(task);
        await store.TrashTaskAsync(task);
        await store.SaveTaskAsync(task with { Summary = "Second version" });
        await store.TrashTaskAsync(task);

        var trashed = Directory.GetFiles(store.Workspace.TrashTasksDir);
        Assert.Equal(2, trashed.Length);
    }

    [Fact]
    public async Task ReportsABrokenTaskFileRatherThanSkippingIt()
    {
        var store = new TaskStore(new Workspace(_root));
        await store.InitialiseAsync();
        var brokenId = Ulid.New();
        await File.WriteAllTextAsync(
            store.Workspace.TaskFile(brokenId), "{ this is not json");

        var loaded = await store.LoadAsync();

        var failure = Assert.Single(loaded.Failures);
        Assert.Equal($"{brokenId}.json", failure.FileName);
    }

    [Fact]
    public async Task ReadsAnEmptyFileAsAbsentRatherThanBroken()
    {
        // A sync client that has created a file but not yet filled it is a
        // transient state, not corruption.
        var store = new TaskStore(new Workspace(_root));
        await store.InitialiseAsync();
        await File.WriteAllTextAsync(store.Workspace.TaskFile(Ulid.New()), "");

        var loaded = await store.LoadAsync();

        Assert.Empty(loaded.Failures);
        Assert.Empty(loaded.Tasks);
    }

    // A workspace is a folder the user (and a sync client, and anything else
    // with write access) can edit by hand, so every persisted name that becomes
    // a path is hostile input until checked.

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../../escaped")]
    [InlineData("..\\..\\escaped")]
    [InlineData("sub/dir")]
    [InlineData("")]
    [InlineData("01m07z000000000000000000zz")] // a ULID, but not this file's
    public async Task RefusesATaskFileWhoseIdWouldWriteSomewhereElse(string craftedId)
    {
        var store = new TaskStore(new Workspace(_root));
        await store.InitialiseAsync();

        // The filename is a perfectly good ULID; only the id inside is crafted.
        var fileName = $"{Ulid.New()}.json";
        await File.WriteAllTextAsync(
            Path.Combine(store.Workspace.TasksDir, fileName),
            $$"""
              {"schemaVersion":1,"id":{{System.Text.Json.JsonSerializer.Serialize(craftedId)}},
               "summary":"Crafted","statusId":"s","boardRank":"n","createdAt":"2026-08-18T00:00:00Z",
               "updatedAt":"2026-08-18T00:00:00Z"}
              """);

        var loaded = await store.LoadAsync();

        Assert.Empty(loaded.Tasks);
        var failure = Assert.Single(loaded.Failures);
        Assert.Equal(fileName, failure.FileName);
        Assert.IsType<UnsafeWorkspaceNameException>(failure.Error);
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../../../secrets.txt")]
    [InlineData("01m07z000000000000000000a1-../../escaped")]
    [InlineData("01m07z000000000000000000a1-..\\..\\escaped")]
    [InlineData("01m07z000000000000000000a1-..")]
    [InlineData("plan.pdf")] // no id prefix
    public async Task RefusesAnAttachmentNameThatWouldReachOutsideTheWorkspace(string storedName)
    {
        var store = new TaskStore(new Workspace(_root));
        await store.InitialiseAsync();

        var id = Ulid.New();
        await File.WriteAllTextAsync(
            Path.Combine(store.Workspace.TasksDir, $"{id}.json"),
            $$"""
              {"schemaVersion":1,"id":"{{id}}","summary":"Crafted","statusId":"s",
               "boardRank":"n","createdAt":"2026-08-18T00:00:00Z",
               "updatedAt":"2026-08-18T00:00:00Z",
               "attachments":[{"id":"01m07z000000000000000000a1","originalName":"plan.pdf",
                 "storedName":{{System.Text.Json.JsonSerializer.Serialize(storedName)}},
                 "sizeBytes":1,"addedAt":"2026-08-18T00:00:00Z"}]}
              """);

        var loaded = await store.LoadAsync();

        Assert.Empty(loaded.Tasks);
        Assert.IsType<UnsafeWorkspaceNameException>(Assert.Single(loaded.Failures).Error);
        Assert.Throws<UnsafeWorkspaceNameException>(() => store.DeleteAttachment(storedName));
    }

    [Fact]
    public async Task DoesNotDeleteAFileOutsideTheWorkspaceOnAttachmentDelete()
    {
        var store = new TaskStore(new Workspace(_root));
        await store.InitialiseAsync();

        var outsider = Path.Combine(Path.GetTempPath(), $"mightdo-outsider-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(outsider, "must survive");
        try
        {
            Assert.Throws<UnsafeWorkspaceNameException>(
                () => store.DeleteAttachment($"../../{Path.GetFileName(outsider)}"));
            Assert.Throws<UnsafeWorkspaceNameException>(() => store.DeleteAttachment(outsider));
            Assert.True(File.Exists(outsider));
        }
        finally
        {
            File.Delete(outsider);
        }
    }

    [Fact]
    public async Task RefusesToTrashATaskWithACraftedAttachmentBeforeMovingAnything()
    {
        var store = new TaskStore(new Workspace(_root));
        await store.InitialiseAsync();

        const string safe = "01m07z000000000000000000a1-plan.pdf";
        await File.WriteAllTextAsync(store.Workspace.AttachmentFile(safe), "attachment bytes");

        var task = MightDoTask.Create("Two attachments", "s", Rank.First) with
        {
            Attachments =
            [
                new Attachment("01m07z000000000000000000a1", "plan.pdf", safe, 15, DateTime.UtcNow),
                new Attachment("01m07z000000000000000000a2", "evil", "../../evil", 1, DateTime.UtcNow),
            ],
        };
        await store.SaveTaskAsync(task);

        await Assert.ThrowsAsync<UnsafeWorkspaceNameException>(
            () => store.TrashTaskAsync(task));

        // Nothing was moved: the check happens before the first rename, so the
        // task is not left half in the trash.
        Assert.True(File.Exists(store.Workspace.AttachmentFile(safe)));
        Assert.True(File.Exists(store.Workspace.TaskFile(task.Id)));
    }

    [Fact]
    public async Task RefusesToSaveATaskWhoseIdIsNotAUlid()
    {
        var store = new TaskStore(new Workspace(_root));
        await store.InitialiseAsync();
        var task = MightDoTask.Create("Crafted", "s", Rank.First) with { Id = "../../escaped" };

        await Assert.ThrowsAsync<UnsafeWorkspaceNameException>(() => store.SaveTaskAsync(task));

        Assert.Empty(Directory.GetFiles(store.Workspace.TasksDir));
    }

    [Fact]
    public async Task ReadsAnImpossibleTaskIdAsAbsentRatherThanThrowing()
    {
        // Looking something up is allowed to come back empty; only writing under
        // a crafted id is an error.
        var store = new TaskStore(new Workspace(_root));
        await store.InitialiseAsync();

        Assert.Null(await store.LoadTaskAsync("../../etc/passwd"));
        Assert.Null(await store.RestoreTaskAsync("../../etc/passwd"));
    }

    [Fact]
    public async Task EachPreservedConflictGetsItsOwnNameEvenInTheSameSecond()
    {
        // The conflict name carries a timestamp, and two overwrites within one
        // second would otherwise resolve to the same file — losing the very
        // edit being kept.
        var store = new TaskStore(new Workspace(_root));
        await store.InitialiseAsync();
        var task = MightDoTask.Create("Ours", "s", Rank.First);
        await store.SaveTaskAsync(task);

        foreach (var summary in (string[])["First theirs", "Second theirs"])
        {
            await WorkspaceFiles.WriteJsonAtomicAsync(
                store.Workspace.TaskFile(task.Id), task with { Summary = summary });
            await store.SaveTaskAsync(task);
        }

        var loaded = await store.LoadAsync();
        Assert.Equal(2, loaded.Conflicts.Count);
        Assert.Equal(2, loaded.Conflicts.Select(c => c.FileName).Distinct().Count());
    }

    [Fact]
    public void KnowsWhenTheWorkspaceFolderHasGone()
    {
        // Deleting a watched root produces no filesystem events (ADR-0003), so
        // this has to be asked rather than waited for.
        var workspace = new Workspace(_root);
        workspace.EnsureLayout();
        Assert.True(workspace.Exists);

        Directory.Delete(_root, recursive: true);

        Assert.False(workspace.Exists);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(from, to));
        }

        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(from, to), overwrite: true);
        }
    }
}
