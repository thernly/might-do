namespace MightDo.Core.Domain;

/// <summary>
/// Fractional index ranks for manual board ordering.
/// </summary>
/// <remarks>
/// A rank is a string that sorts lexicographically. To drop a card between two
/// others you generate a rank between their two ranks — which rewrites exactly
/// one task file, rather than renumbering every card in the column. That matters
/// here because task files sync individually, so a reorder that touched every
/// file would be the worst possible conflict shape.
/// <para>
/// Ranks are base-36 (<c>0-9a-z</c>), which sorts correctly under <b>ordinal</b>
/// string comparison. Use <see cref="StringComparer.Ordinal"/> and never the
/// default comparer: <see cref="string.CompareTo(string)"/> and
/// <c>Comparer&lt;string&gt;.Default</c> are culture-sensitive, and a
/// culture-sensitive sort silently produces the wrong board order.
/// </para>
/// <para>
/// No rank ever ends in <c>0</c>; that invariant is what guarantees there is
/// always room to insert before any existing rank.
/// </para>
/// </remarks>
public static class Rank
{
    private const string Digits = "0123456789abcdefghijklmnopqrstuvwxyz";
    private const int Base = 36;

    /// <summary>The rank a column's first card gets.</summary>
    public static string First => Between("", "");

    /// <summary>
    /// Returns a rank that sorts strictly between <paramref name="before"/> and
    /// <paramref name="after"/>.
    /// </summary>
    /// <param name="before">Empty to insert at the very top.</param>
    /// <param name="after">Empty to append at the very bottom.</param>
    public static string Between(string before, string after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        if (before.Length > 0 && !IsValid(before))
            throw new ArgumentException($"Not a valid rank: '{before}'", nameof(before));
        if (after.Length > 0 && !IsValid(after))
            throw new ArgumentException($"Not a valid rank: '{after}'", nameof(after));
        if (before.Length > 0 && after.Length > 0
            && string.CompareOrdinal(before, after) >= 0)
        {
            throw new ArgumentException(
                $"before ('{before}') must sort before after ('{after}')", nameof(before));
        }

        var buffer = new System.Text.StringBuilder();
        var boundedAbove = after.Length > 0;

        for (var i = 0; ; i++)
        {
            // -1 stands for "below the smallest digit"; Base for "above the largest".
            var low = i < before.Length ? Digits.IndexOf(before[i]) : -1;
            var high = boundedAbove && i < after.Length ? Digits.IndexOf(after[i]) : Base;

            if (low + 1 < high)
            {
                var mid = (low + 1 + high) / 2;
                if (mid == 0)
                {
                    // Emitting a terminal '0' would break the no-trailing-zero
                    // invariant, so descend a level instead. '0' is strictly
                    // below `high` here, so `after` no longer constrains us.
                    buffer.Append(Digits[0]);
                    boundedAbove = false;
                    continue;
                }

                buffer.Append(Digits[mid]);
                return buffer.ToString();
            }

            // No gap at this position: copy the lower bound's digit, go deeper.
            buffer.Append(low >= 0 ? Digits[low] : Digits[0]);
            if (low >= 0 && low < high) boundedAbove = false;
        }
    }

    /// <summary>Ranks for <paramref name="count"/> items in order, for seeding a fresh column.</summary>
    public static IReadOnlyList<string> Initial(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var ranks = new List<string>(count);
        var previous = "";
        for (var i = 0; i < count; i++)
        {
            previous = Between(previous, "");
            ranks.Add(previous);
        }

        return ranks;
    }

    public static bool IsValid(string rank)
    {
        if (string.IsNullOrEmpty(rank)) return false;
        if (rank[^1] == Digits[0]) return false;
        foreach (var c in rank)
        {
            if (!Digits.Contains(c)) return false;
        }

        return true;
    }
}
