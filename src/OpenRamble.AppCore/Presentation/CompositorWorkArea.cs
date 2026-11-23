namespace OpenRamble.AppCore;

/// <summary>Space a compositor keeps for bars and panels on one monitor, in its layout units.</summary>
public readonly record struct ReservedInsets(double Left, double Top, double Right, double Bottom);

/// <summary>
/// One monitor as the compositor reports it: the mode in hardware pixels, its scale and rotation,
/// and what layer-shell bars reserved on it.
/// </summary>
public sealed record CompositorMonitor(
    string Name,
    double Width,
    double Height,
    double Scale,
    int Transform,
    ReservedInsets Reserved)
{
    /// <summary>Odd transforms turn the panel a quarter, swapping its sides.</summary>
    private bool IsQuarterTurned => Transform % 2 == 1;

    public double PixelWidth => IsQuarterTurned ? Height : Width;

    public double PixelHeight => IsQuarterTurned ? Width : Height;

    /// <summary>The size in the compositor's layout units, the ones the reservation is in.</summary>
    public double LayoutWidth => PixelWidth / Scale;

    public double LayoutHeight => PixelHeight / Scale;
}

/// <summary>
/// The work area a panel should stay inside: the screen minus what the compositor reserved.
/// </summary>
/// <remarks>
/// Needed because an XWayland client's own "working area" knows nothing of layer-shell bars - a
/// waybar at the top is simply screen to it, and a panel placed at the top lands on the bar. The
/// compositor knows; this turns its answer into the client's units. Those are the X server's
/// pixels, which are the hardware pixels when XWayland is told not to scale and the layout units
/// when it is not; the ratio of the screen's size as the client sees it to the monitor's layout
/// size covers both without knowing which.
/// </remarks>
public static class CompositorWorkArea
{
    /// <summary>
    /// The monitor behind a screen: by name, else the one monitor whose size matches, else the
    /// only monitor when there is only one screen. <c>null</c> when it cannot be told.
    /// </summary>
    public static CompositorMonitor? Match(
        IReadOnlyList<CompositorMonitor> monitors,
        string? screenName,
        OverlayFrame screenBounds,
        int screenCount)
    {
        if (screenName is { Length: > 0 })
        {
            foreach (CompositorMonitor monitor in monitors)
            {
                if (string.Equals(monitor.Name, screenName, StringComparison.Ordinal)) { return monitor; }
            }
        }

        CompositorMonitor? sized = null;
        int matches = 0;
        foreach (CompositorMonitor monitor in monitors)
        {
            if (SameSize(monitor.PixelWidth, monitor.PixelHeight, screenBounds)
                || SameSize(monitor.LayoutWidth, monitor.LayoutHeight, screenBounds))
            {
                sized = monitor;
                matches++;
            }
        }

        if (matches == 1) { return sized; }
        return monitors.Count == 1 && screenCount == 1 ? monitors[0] : null;
    }

    /// <summary>The screen's bounds with the monitor's reservations taken off, in the screen's units.</summary>
    public static OverlayFrame Inset(OverlayFrame screenBounds, CompositorMonitor monitor)
    {
        double horizontal = screenBounds.Width / monitor.LayoutWidth;
        double vertical = screenBounds.Height / monitor.LayoutHeight;
        double left = Math.Round(monitor.Reserved.Left * horizontal);
        double top = Math.Round(monitor.Reserved.Top * vertical);
        double right = Math.Round(monitor.Reserved.Right * horizontal);
        double bottom = Math.Round(monitor.Reserved.Bottom * vertical);
        return new OverlayFrame(
            screenBounds.X + left,
            screenBounds.Y + top,
            Math.Max(0, screenBounds.Width - left - right),
            Math.Max(0, screenBounds.Height - top - bottom));
    }

    private static bool SameSize(double width, double height, OverlayFrame bounds) =>
        Math.Abs(width - bounds.Width) < 1 && Math.Abs(height - bounds.Height) < 1;
}
