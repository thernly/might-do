namespace MightDo.Core.Domain;

/// <summary>
/// Moments at the precision the workspace format can actually store.
/// </summary>
/// <remarks>
/// A task file carries at most six fractional digits — see
/// <c>docs/format/workspace-v1.md</c> and <c>InstantConverter</c> — while
/// <see cref="DateTime.UtcNow"/> is granular to 100ns. Keeping that seventh
/// digit in memory means the task the session holds and the task on disk differ
/// by less than a microsecond: every rescan after a local write then finds a
/// workspace that has "changed" and announces it, which is the redraw-on-every-
/// sync-touch that ADR-0003's comparison exists to prevent.
/// <para>
/// It shows up on some machines and not others. macOS's clock never produces
/// that digit, so a value round-trips exactly there; Linux's does, routinely.
/// Normalising here rather than widening the file keeps the format as the
/// canonical corpus has it, and puts the whole question in one place.
/// </para>
/// <para>
/// Applied by the domain types themselves as values are set, so a task cannot
/// hold a moment its own file cannot record — including one handed in by a
/// caller, which is where a reminder's time comes from.
/// </para>
/// </remarks>
public static class Instants
{
    /// <summary>Now, storable.</summary>
    public static DateTime Now() => AtStoredPrecision(DateTime.UtcNow);

    /// <summary>Now on <paramref name="time"/>'s clock, storable.</summary>
    public static DateTime Now(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return AtStoredPrecision(time.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// <paramref name="value"/> with anything finer than a microsecond dropped.
    /// </summary>
    /// <remarks>
    /// Truncates rather than rounds: rounding could carry a moment past a
    /// boundary and make a reminder due a fraction before it was asked for.
    /// The kind is left alone, so a local time stays local.
    /// </remarks>
    public static DateTime AtStoredPrecision(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerMicrosecond, value.Kind);

    /// <inheritdoc cref="AtStoredPrecision(DateTime)"/>
    public static DateTime? AtStoredPrecision(DateTime? value) =>
        value is { } moment ? AtStoredPrecision(moment) : null;
}
