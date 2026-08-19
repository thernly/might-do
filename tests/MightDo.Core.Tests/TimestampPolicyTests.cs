using Microsoft.Extensions.Time.Testing;
using MightDo.Core.Domain;
using MightDo.Core.Session;
using MightDo.Core.Storage;

namespace MightDo.Core.Tests;

/// <summary>
/// What <see cref="MightDoTask.UpdatedAt"/> means, command by command.
/// </summary>
/// <remarks>
/// The list offers "Recently updated" as a sort, so the field has to mean one
/// thing across every way a task can change. It was stamped by one command out
/// of a dozen, which made the sort quietly wrong: a task whose notes, steps,
/// status and attachments had all changed that morning sorted below one whose
/// summary was retyped last week.
/// </remarks>
public class TimestampPolicyTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "mightdo-stamps-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    private readonly FakeTimeProvider _time =
        new(new DateTimeOffset(2026, 8, 19, 9, 0, 0, TimeSpan.Zero));

    private WorkspaceSession _session = null!;

    public async ValueTask InitializeAsync() =>
        _session = await WorkspaceSession.OpenAsync(
            new TaskStore(new Core.Storage.Workspace(_root)), _time);

    public ValueTask DisposeAsync()
    {
        _session.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    public static TheoryData<string> UserEdits =>
        ["summary", "note", "step", "tags", "status", "board", "reminder", "attachment"];

    [Theory]
    [MemberData(nameof(UserEdits))]
    public async Task EveryChangeTheUserMakesToATaskCountsAsAnUpdate(string command)
    {
        var task = await Seeded();
        var before = task.UpdatedAt;
        _time.Advance(TimeSpan.FromHours(1));

        await Apply(command, task);

        var after = _session.Snapshot.TaskById(task.Id)!;
        Assert.True(
            after.UpdatedAt > before,
            $"'{command}' left updatedAt at {after.UpdatedAt:o}, so a task the user "
            + "has just changed does not count as recently updated.");
        Assert.Equal(_time.GetUtcNow().UtcDateTime, after.UpdatedAt);
    }

    [Fact]
    public async Task AReminderFiringIsNotSomethingTheUserDid()
    {
        // It happens on a timer, to a task nobody has touched. Counting it
        // would reorder the list by which reminders happened to come due.
        var task = await Seeded();
        await _session.AddReminderAsync(task, _time.GetUtcNow().UtcDateTime.AddMinutes(1));

        var withReminder = _session.Snapshot.TaskById(task.Id)!;
        var before = withReminder.UpdatedAt;
        _time.Advance(TimeSpan.FromHours(1));

        var ids = withReminder.Reminders.Select(r => r.Id).ToHashSet();
        await _session.MarkRemindersFiredAsync(withReminder, ids);
        await _session.DismissRemindersAsync(_session.Snapshot.TaskById(task.Id)!, ids);

        Assert.Equal(before, _session.Snapshot.TaskById(task.Id)!.UpdatedAt);
    }

    [Fact]
    public async Task DeletingATagDoesNotMarkEveryTaskThatUsedItAsUpdated()
    {
        // A settings change that rewrites half the workspace is not half the
        // workspace being worked on.
        var tag = await _session.AddTagAsync("errand");
        var task = await Seeded();
        await _session.SetTagsAsync(task, [tag.Id]);

        var before = _session.Snapshot.TaskById(task.Id)!.UpdatedAt;
        _time.Advance(TimeSpan.FromHours(1));

        await _session.DeleteTagAsync(tag.Id);

        Assert.Equal(before, _session.Snapshot.TaskById(task.Id)!.UpdatedAt);
    }

    [Fact]
    public async Task ATaskIsCreatedAndUpdatedOnTheSessionsClock()
    {
        // Not the machine's: a session that was handed a clock and then read
        // DateTime.UtcNow anyway produces one operation stamped from two.
        var task = await _session.CreateTaskAsync("Made at nine");

        Assert.Equal(_time.GetUtcNow().UtcDateTime, task.CreatedAt);
        Assert.Equal(task.CreatedAt, task.UpdatedAt);
    }

    [Fact]
    public async Task ANoteAndACompletionAreDatedByTheSessionsClockToo()
    {
        var task = await Seeded();
        _time.Advance(TimeSpan.FromHours(2));

        await _session.AddNoteAsync(task, "Rang them back");
        var final = _session.Snapshot.Config.Statuses.First(s => s.Type == StatusType.Final);
        await _session.MoveToStatusAsync(_session.Snapshot.TaskById(task.Id)!, final.Id);

        var after = _session.Snapshot.TaskById(task.Id)!;
        Assert.Equal(_time.GetUtcNow().UtcDateTime, Assert.Single(after.Notes).CreatedAt);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, after.CompletedAt);
    }

    private async Task<MightDoTask> Seeded()
    {
        var task = await _session.CreateTaskAsync("Ring the dentist");
        return _session.Snapshot.TaskById(task.Id)!;
    }

    private async Task Apply(string command, MightDoTask task)
    {
        var active = _session.Snapshot.Config.Statuses.First(s => s.Type == StatusType.Active);

        switch (command)
        {
            case "summary":
                await _session.EditTaskAsync(task, t => t with { Summary = "Ring the vet" });
                break;
            case "note":
                await _session.AddNoteAsync(task, "Left a message");
                break;
            case "step":
                await _session.AddStepAsync(task, "Find the number");
                break;
            case "tags":
                var tag = await _session.AddTagAsync("errand");
                await _session.SetTagsAsync(_session.Snapshot.TaskById(task.Id)!, [tag.Id]);
                break;
            case "status":
                await _session.MoveToStatusAsync(task, active.Id);
                break;
            case "board":
                // Below another card rather than back where it was: dropping a
                // card in the place it came from is deliberately not an edit.
                var neighbour = await _session.CreateTaskAsync("Already on the board");
                await _session.ReorderOnBoardAsync(
                    _session.Snapshot.TaskById(task.Id)!, task.StatusId, above: neighbour);
                break;
            case "reminder":
                await _session.AddReminderAsync(
                    task, _time.GetUtcNow().UtcDateTime.AddDays(1));
                break;
            case "attachment":
                var source = Path.Combine(_root, "letter.txt");
                await File.WriteAllTextAsync(source, "dear dentist");
                await _session.AttachFileAsync(task, source);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, "no such command");
        }
    }
}
