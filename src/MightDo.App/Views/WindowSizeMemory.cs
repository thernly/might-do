using Avalonia;
using Avalonia.Controls;
using MightDo.Platform;

namespace MightDo.App.Views;

/// <summary>
/// Keeps track of the size a window should reopen at.
/// </summary>
/// <remarks>
/// Which is not simply its current size. A maximized window's size is the
/// screen's, and reopening at that leaves nothing to unmaximize to, so what is
/// wanted is the last size it had while it was an ordinary window.
/// <para>
/// Split out from the window because none of this can be checked through one:
/// a headless window reports the same size maximized as it did before, so a
/// test driving the real thing would agree with any answer at all.
/// </para>
/// </remarks>
public sealed class WindowSizeMemory
{
    private Size? _restore;

    /// <summary>The size read back from the settings as the window opens.</summary>
    /// <remarks>
    /// Without this, a window opened maximized and closed maximized has never
    /// been seen at an ordinary size and would forget where to return to.
    /// </remarks>
    public void Remembered(Size size) => _restore = size;

    /// <summary>Called for every resize, including the ones that maximize.</summary>
    public void Resized(Size size, WindowState state)
    {
        if (state == WindowState.Normal) _restore = size;
    }

    /// <summary>
    /// What to record on closing. <paramref name="current"/> is the window's own
    /// size, used only when no ordinary size has ever been seen.
    /// </summary>
    public WindowPlacement Placement(Size current, WindowState state) =>
        new((_restore ?? current).Width,
            (_restore ?? current).Height,
            state == WindowState.Maximized);
}
