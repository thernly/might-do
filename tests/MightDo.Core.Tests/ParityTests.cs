using System.Text.Json.Nodes;
using MightDo.Core.Domain;
using MightDo.Core.Query;
using MightDo.Core.Session;
using MightDo.Core.Storage;

namespace MightDo.Core.Tests;

/// <summary>
/// Behavioural parity with the Flutter implementation.
/// </summary>
/// <remarks>
/// The interop tests prove each implementation can read what the other wrote,
/// which is about the format. This is about behaviour: the same sequence of
/// operations, run through each, should leave workspaces that mean the same
/// thing.
/// <para>
/// The expectation is written by <c>test/format/parity_test.dart</c> and
/// committed, so this runs without the Flutter toolchain. Ids are ULIDs and
/// timestamps are real moments, so both are normalised away — what remains is
/// what a user would see: names, ordering, completion, board ranks.
/// </para>
/// </remarks>
public class ParityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mightdo-parity-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TheScenarioMatchesTheSharedExpectation()
    {
        using var session = await WorkspaceSession.OpenAsync(
            new TaskStore(new Core.Storage.Workspace(_root)));

        await RunScenarioAsync(session);

        var actual = Normalise(session);
        var expected = Fixtures.ReadNode("parity", "scenario.json");

        JsonAssert.SemanticallyEqual(expected, actual,
            "the two implementations disagree about what the scenario means");
    }

    /// <summary>
    /// The operations both implementations perform, in order. Must stay in step
    /// with <c>runScenario</c> in <c>test/format/parity_test.dart</c>.
    /// </summary>
    private static async Task RunScenarioAsync(WorkspaceSession session)
    {
        Status StatusNamed(string name) =>
            session.Snapshot.Config.Statuses.First(s => s.Name == name);

        MightDoTask Current(MightDoTask task) => session.Snapshot.TaskById(task.Id)!;

        var work = await session.AddCategoryAsync("Work", 0xFF2E7D32);
        var urgent = await session.AddTagAsync("urgent");
        await session.AddTagAsync("URGENT"); // same name, different case: reuses

        var alpha = await session.CreateTaskAsync(
            "Alpha",
            description: "The first one.",
            categoryId: work.Id,
            tagIds: [urgent.Id],
            estimateMinutes: 60);
        var beta = await session.CreateTaskAsync(
            "Beta",
            priority: Priority.High,
            dueDate: new CalendarDate(2026, 9, 1));
        var gamma = await session.CreateTaskAsync("Gamma");

        // Completion follows the status type, in all three directions.
        await session.MoveToStatusAsync(beta, StatusNamed("In Progress").Id);
        await session.MoveToStatusAsync(Current(beta), StatusNamed("Done").Id);
        await session.MoveToStatusAsync(Current(beta), StatusNamed("Blocked").Id);

        await session.AddNoteAsync(Current(alpha), "Made a start.");
        await session.AddStepAsync(Current(alpha), "Step one");
        await session.AddStepAsync(Current(alpha), "Step two");
        await session.SetStepDoneAsync(
            Current(alpha), Current(alpha).Steps[0].Id, true);

        // A manual board move: Gamma above Alpha in the default column.
        await session.ReorderOnBoardAsync(
            Current(gamma),
            session.Snapshot.Config.DefaultStatusId,
            above: null,
            below: Current(alpha));

        // Adding and removing a status renumbers the rest.
        var review = await session.AddStatusAsync("In Review", StatusType.Active);
        await session.DeleteStatusAsync(review.Id, StatusNamed("Blocked").Id);

        // Trashed tasks leave the working set without being destroyed.
        var delta = await session.CreateTaskAsync("Delta");
        await session.TrashTaskAsync(delta);
    }

    /// <summary>
    /// Reduces a workspace to what it means. Must stay in step with
    /// <c>normalise</c> on the Flutter side.
    /// </summary>
    private static JsonNode Normalise(WorkspaceSession session)
    {
        var snapshot = session.Snapshot;
        var config = snapshot.Config;

        // The enum names are the wire values with a capital letter, and the wire
        // values are what the Flutter side emits.
        static string Wire(object value) => value.ToString()!.ToLowerInvariant();

        JsonNode Task(MightDoTask task) => new JsonObject
        {
            ["summary"] = task.Summary,
            ["description"] = task.Description,
            ["status"] = config.StatusById(task.StatusId)?.Name ?? "<unknown>",
            ["category"] = config.CategoryById(task.CategoryId)?.Name,
            ["tags"] = new JsonArray(
                [.. config.TagsByIds(task.TagIds).Select(tag => JsonValue.Create(tag.Name))]),
            ["priority"] = Wire(task.Priority),
            ["dueDate"] = task.DueDate?.ToIso(),
            ["isComplete"] = task.IsComplete,
            ["estimateMinutes"] = task.EstimateMinutes,
            ["totalTimeMinutes"] = task.TotalTimeMinutes,
            ["boardRank"] = task.BoardRank,
            ["steps"] = new JsonArray([.. task.Steps.Select(step => (JsonNode)new JsonObject
            {
                ["text"] = step.Text,
                ["done"] = step.Done,
            })]),
            ["notes"] = new JsonArray([.. task.Notes.Select(note => (JsonNode)new JsonObject
            {
                ["body"] = note.Body,
            })]),
            ["reminders"] = new JsonArray(
                [.. task.Reminders.Select(reminder => (JsonNode)new JsonObject
                {
                    ["pending"] = reminder.IsPending,
                    ["outstanding"] = reminder.IsOutstanding,
                })]),
            ["attachments"] = new JsonArray(
                [.. task.Attachments.Select(attachment => (JsonNode)new JsonObject
                {
                    ["originalName"] = attachment.OriginalName,
                    ["sizeBytes"] = attachment.SizeBytes,
                })]),
        };

        return new JsonObject
        {
            ["config"] = new JsonObject
            {
                ["defaultStatus"] = config.StatusById(config.DefaultStatusId)?.Name,
                ["statuses"] = new JsonArray(
                    [.. config.Statuses.Select(status => (JsonNode)new JsonObject
                    {
                        ["name"] = status.Name,
                        ["type"] = Wire(status.Type),
                        ["order"] = status.Order,
                        ["hiddenFromBoard"] = status.HiddenFromBoard,
                    })]),
                ["categories"] = new JsonArray(
                    [.. config.Categories.Select(category => (JsonNode)new JsonObject
                    {
                        ["name"] = category.Name,
                        ["color"] = category.Color,
                    })]),
                ["tags"] = new JsonArray([.. config.Tags.Select(tag => (JsonNode)new JsonObject
                {
                    ["name"] = tag.Name,
                })]),
            },
            ["tasks"] = new JsonArray(
                [.. snapshot.Tasks
                    .OrderBy(task => task.Summary, StringComparer.Ordinal)
                    .Select(Task)]),
            ["trashedSummaries"] = new JsonArray([.. TrashedSummaries(session)]),
        };
    }

    private static IEnumerable<JsonNode?> TrashedSummaries(WorkspaceSession session)
    {
        var dir = session.Workspace.TrashTasksDir;
        if (!Directory.Exists(dir)) yield break;

        var summaries = new List<string>();
        foreach (var path in Directory.EnumerateFiles(dir))
        {
            if (!WorkspaceFiles.IsOwnTaskFile(Path.GetFileName(path))) continue;

            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node?["summary"]?.GetValue<string>() is { } summary) summaries.Add(summary);
        }

        foreach (var summary in summaries.Order(StringComparer.Ordinal))
        {
            yield return JsonValue.Create(summary);
        }
    }
}
