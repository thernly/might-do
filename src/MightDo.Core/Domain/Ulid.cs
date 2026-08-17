using System.Security.Cryptography;

namespace MightDo.Core.Domain;

/// <summary>
/// Identifiers for tasks and everything attached to them.
/// </summary>
/// <remarks>
/// ULIDs are used because they are collision-free across machines editing
/// offline and sort chronologically, so a directory listing is in creation
/// order — see <c>docs/adr/0001-file-per-task-json-storage.md</c>.
/// <para>
/// Written in lowercase to match the files already on disk. Readers are
/// case-insensitive, so this is tidiness rather than a requirement; a folder
/// edited by both implementations should not look like two applications wrote
/// it. Generating these here rather than taking a dependency keeps the casing
/// and the alphabet under the control of the format spec.
/// </para>
/// </remarks>
public static class Ulid
{
    /// <summary>Crockford base32: no <c>i</c>, <c>l</c>, <c>o</c> or <c>u</c>.</summary>
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    public const int Length = 26;

    /// <summary>A new identifier: 48 bits of timestamp, 80 bits of randomness.</summary>
    public static string New() => New(DateTimeOffset.UtcNow);

    public static string New(DateTimeOffset timestamp)
    {
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);

        Span<char> chars = stackalloc char[Length];
        var milliseconds = timestamp.ToUnixTimeMilliseconds();

        // The 48-bit timestamp occupies the first 10 characters, most
        // significant first, which is what makes the string sort by time.
        for (var i = 0; i < 10; i++)
        {
            chars[i] = Alphabet[(int)((milliseconds >> (45 - (i * 5))) & 31)];
        }

        // 80 bits of randomness is exactly 16 base32 characters, no padding.
        var bits = 0;
        var bitCount = 0;
        var index = 10;
        foreach (var b in random)
        {
            bits = (bits << 8) | b;
            bitCount += 8;
            while (bitCount >= 5)
            {
                bitCount -= 5;
                chars[index++] = Alphabet[(bits >> bitCount) & 31];
            }
        }

        return new string(chars);
    }

    /// <summary>
    /// Whether <paramref name="value"/> is a 26-character Crockford base32 ULID.
    /// Matched case-insensitively: a sync client or a case-insensitive
    /// filesystem may hand a name back in another case, and a task must never be
    /// mistaken for a foreign file over casing.
    /// </summary>
    public static bool IsUlid(ReadOnlySpan<char> value)
    {
        if (value.Length != Length) return false;

        foreach (var c in value)
        {
            if (!Alphabet.Contains(char.ToLowerInvariant(c))) return false;
        }

        return true;
    }
}
