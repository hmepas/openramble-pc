using System.Diagnostics;
using Avalonia;
using Avalonia.Platform;
using OpenRamble.AppCore;
using OpenRamble.LinuxInput;

namespace OpenRamble.App.Linux;

/// <summary>
/// The part of a screen a panel may use: the screen minus the compositor's bars.
/// </summary>
/// <remarks>
/// Under XWayland, <see cref="Screen.WorkingArea"/> is the whole screen - layer-shell bars such as
/// waybar are invisible to an X client - so a panel placed at the top lands on the bar. Hyprland
/// says what it reserved; elsewhere Avalonia's answer is all there is. The compositor is asked off
/// the UI thread, at most every few seconds (bars come and go, monitors are plugged), and the last
/// answer is used meanwhile; <see cref="Changed"/> lets an open panel move when the answer does.
/// </remarks>
internal static class WorkArea
{
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);

    private static IReadOnlyList<CompositorMonitor>? monitors;
    private static long askedAt;
    private static int asking;

    /// <summary>The compositor's answer moved. Raised off the UI thread.</summary>
    public static event Action? Changed;

    /// <summary>The work area of <paramref name="screen"/>, in the same pixels as its bounds.</summary>
    public static PixelRect For(Screen screen, int screenCount)
    {
        Refresh();
        IReadOnlyList<CompositorMonitor>? known = Volatile.Read(ref monitors);
        if (known is null) { return screen.WorkingArea; }

        PixelRect pixels = screen.Bounds;
        var bounds = new OverlayFrame(pixels.X, pixels.Y, pixels.Width, pixels.Height);
        if (CompositorWorkArea.Match(known, screen.DisplayName, bounds, screenCount) is not { } monitor)
        {
            return screen.WorkingArea;
        }

        OverlayFrame work = CompositorWorkArea.Inset(bounds, monitor);
        return new PixelRect((int)work.X, (int)work.Y, (int)work.Width, (int)work.Height);
    }

    /// <summary>Ask the compositor again if the last answer is stale. Never waits for it.</summary>
    public static void Refresh()
    {
        if (HyprlandIpc.Signature is null) { return; }
        long last = Interlocked.Read(ref askedAt);
        if (last != 0 && Stopwatch.GetElapsedTime(last) < Fresh) { return; }
        if (Interlocked.Exchange(ref asking, 1) == 1) { return; }

        Interlocked.Exchange(ref askedAt, Stopwatch.GetTimestamp());
        _ = Task.Run(() =>
        {
            try
            {
                if (HyprlandIpc.Monitors(Budget) is not { } answer) { return; }
                IReadOnlyList<CompositorMonitor>? previous = Interlocked.Exchange(ref monitors, answer);
                if (previous is null || !previous.SequenceEqual(answer)) { Changed?.Invoke(); }
            }
            finally
            {
                Volatile.Write(ref asking, 0);
            }
        });
    }
}
