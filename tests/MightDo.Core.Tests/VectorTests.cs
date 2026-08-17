using MightDo.Core.Domain;

namespace MightDo.Core.Tests;

/// <summary>
/// Conformance against <c>fixtures/vectors/</c> — the expected outputs of the
/// pure functions, generated from the Flutter implementation. These are the
/// cheapest possible proof that the port behaves identically.
/// </summary>
public class RankTests
{
    [Fact]
    public void MatchesTheVectors()
    {
        using var vector = Fixtures.ReadDocument("vectors", "ranks.json");

        foreach (var testCase in vector.RootElement.GetProperty("between").EnumerateArray())
        {
            var before = testCase.GetProperty("before").GetString()!;
            var after = testCase.GetProperty("after").GetString()!;
            var expected = testCase.GetProperty("result").GetString()!;

            var result = Rank.Between(before, after);

            Assert.Equal(expected, result);

            // The property the vector exists to protect.
            if (before.Length > 0) Assert.True(string.CompareOrdinal(before, result) < 0);
            if (after.Length > 0) Assert.True(string.CompareOrdinal(result, after) < 0);
            Assert.False(result.EndsWith('0'));
        }
    }

    [Fact]
    public void InitialRanksMatchTheVector()
    {
        using var vector = Fixtures.ReadDocument("vectors", "ranks.json");
        var initial = vector.RootElement.GetProperty("initialRanks");

        var expected = initial.GetProperty("result")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();

        var actual = Rank.Initial(initial.GetProperty("count").GetInt32());

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RejectsWhatTheVectorSaysItShould()
    {
        using var vector = Fixtures.ReadDocument("vectors", "ranks.json");

        foreach (var testCase in vector.RootElement.GetProperty("rejected").EnumerateArray())
        {
            var before = testCase.GetProperty("before").GetString()!;
            var after = testCase.GetProperty("after").GetString()!;
            var why = testCase.GetProperty("why").GetString()!;

            var thrown = Record.Exception(() => Rank.Between(before, after));

            Assert.True(thrown is ArgumentException,
                $"expected rankBetween('{before}', '{after}') to be rejected: {why}");
        }
    }

    [Fact]
    public void OrdinalComparisonIsWhatSorts()
    {
        // Guards the trap called out in docs/format/workspace-v1.md: the default
        // string comparer in .NET is culture-sensitive, and a culture-sensitive
        // sort silently produces the wrong board order.
        var ranks = new[] { "hzzzzy", "i", "g", "h" };

        var sorted = ranks.OrderBy(r => r, StringComparer.Ordinal).ToArray();

        Assert.Equal(["g", "h", "hzzzzy", "i"], sorted);
    }
}

public class CalendarDateTests
{
    [Fact]
    public void MatchesTheVectors()
    {
        using var vector = Fixtures.ReadDocument("vectors", "calendar-dates.json");

        foreach (var testCase in vector.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = testCase.GetProperty("input").GetString();
            var parsedProperty = testCase.GetProperty("parsed");
            var expected = parsedProperty.ValueKind == System.Text.Json.JsonValueKind.Null
                ? null
                : parsedProperty.GetString();

            var actual = CalendarDate.TryParse(input)?.ToIso();

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void IsADayNotAnInstant()
    {
        // The bug this type exists to prevent: a due date rendered in another
        // zone must not move to the previous day.
        var due = new CalendarDate(2026, 8, 21);

        Assert.Equal("2026-08-21", due.ToIso());
        Assert.Equal(due, CalendarDate.Parse(due.ToIso()));
    }

    [Fact]
    public void ComparesChronologically()
    {
        Assert.True(new CalendarDate(2026, 8, 21) < new CalendarDate(2026, 9, 1));
        Assert.True(new CalendarDate(2026, 12, 31) < new CalendarDate(2027, 1, 1));
        Assert.Equal(11, new CalendarDate(2026, 8, 21).DaysUntil(new CalendarDate(2026, 9, 1)));
    }
}

public class UlidTests
{
    [Fact]
    public void ClassifiesTheFilenameVectors()
    {
        using var vector = Fixtures.ReadDocument("vectors", "filenames.json");

        foreach (var testCase in vector.RootElement.GetProperty("cases").EnumerateArray())
        {
            var fileName = testCase.GetProperty("fileName").GetString()!;
            var expected = testCase.GetProperty("isOwnTaskFile").GetBoolean();

            var stem = Path.GetFileNameWithoutExtension(fileName);
            var actual = Path.GetExtension(fileName)
                             .Equals(".json", StringComparison.OrdinalIgnoreCase)
                         && Ulid.IsUlid(stem);

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void GeneratesLowercaseSortableIdentifiers()
    {
        var earlier = Ulid.New(DateTimeOffset.UnixEpoch.AddMilliseconds(1_000_000));
        var later = Ulid.New(DateTimeOffset.UnixEpoch.AddMilliseconds(2_000_000));

        Assert.Equal(26, earlier.Length);
        Assert.Equal(earlier, earlier.ToLowerInvariant());
        Assert.True(Ulid.IsUlid(earlier));

        // Timestamp first means a directory listing is in creation order.
        Assert.True(string.CompareOrdinal(earlier, later) < 0);
    }

    [Fact]
    public void RejectsTheAmbiguousCrockfordLetters()
    {
        // i, l, o and u are excluded from the alphabet so an id can never be
        // misread by a human retyping a filename.
        Assert.False(Ulid.IsUlid("01m07z000000000000000000i1"));
        Assert.False(Ulid.IsUlid("01m07z000000000000000000u1"));
        Assert.True(Ulid.IsUlid("01m07z000000000000000000t1"));
    }

    [Fact]
    public void IsUnique()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => Ulid.New()).ToHashSet();

        Assert.Equal(1000, ids.Count);
    }
}
