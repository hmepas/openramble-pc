namespace OpenRamble.AppCore.Tests;

/// <summary>The work area from the compositor's reservations, in the client's units.</summary>
public sealed class CompositorWorkAreaTests
{
    // A 4K monitor at 1.25 with a 36-unit waybar on top, as Hyprland reports it.
    private static readonly CompositorMonitor Hyprland4K =
        new("HDMI-A-1", 3840, 2160, 1.25, 0, new ReservedInsets(0, 36, 0, 0));

    /// <summary>XWayland not scaling: the client sees hardware pixels, so 36 layout units are 45 px.</summary>
    [Fact]
    public void UnscaledXWaylandGetsTheBarInHardwarePixels()
    {
        OverlayFrame work = CompositorWorkArea.Inset(new OverlayFrame(0, 0, 3840, 2160), Hyprland4K);

        Assert.Equal(new OverlayFrame(0, 45, 3840, 2115), work);
    }

    /// <summary>XWayland scaled by the compositor: the client sees layout units, one for one.</summary>
    [Fact]
    public void ScaledXWaylandGetsTheBarInLayoutUnits()
    {
        OverlayFrame work = CompositorWorkArea.Inset(new OverlayFrame(0, 0, 3072, 1728), Hyprland4K);

        Assert.Equal(new OverlayFrame(0, 36, 3072, 1692), work);
    }

    [Fact]
    public void EveryEdgeAndTheScreenOriginCount()
    {
        var monitor = new CompositorMonitor("DP-1", 1920, 1080, 1, 0, new ReservedInsets(10, 20, 30, 40));

        OverlayFrame work = CompositorWorkArea.Inset(new OverlayFrame(1920, 0, 1920, 1080), monitor);

        Assert.Equal(new OverlayFrame(1930, 20, 1880, 1020), work);
    }

    [Fact]
    public void QuarterTurnedMonitorSwapsItsSides()
    {
        var portrait = new CompositorMonitor("DP-2", 1920, 1080, 1, 1, new ReservedInsets(0, 30, 0, 0));

        Assert.Same(portrait, CompositorWorkArea.Match([Hyprland4K, portrait], null, new OverlayFrame(0, 0, 1080, 1920), 2));
        Assert.Equal(new OverlayFrame(0, 30, 1080, 1890), CompositorWorkArea.Inset(new OverlayFrame(0, 0, 1080, 1920), portrait));
    }

    [Fact]
    public void MatchPrefersTheNameThenTheSizeThenTheOnlyMonitor()
    {
        var other = new CompositorMonitor("DP-1", 3840, 2160, 1, 0, default);
        var bounds = new OverlayFrame(0, 0, 3840, 2160);

        Assert.Same(other, CompositorWorkArea.Match([Hyprland4K, other], "DP-1", bounds, 2));
        // Two monitors of one size and no name: nothing to tell them apart by.
        Assert.Null(CompositorWorkArea.Match([Hyprland4K, other], null, bounds, 2));
        Assert.Same(Hyprland4K, CompositorWorkArea.Match([Hyprland4K, other], null, new OverlayFrame(0, 0, 3072, 1728), 2));
        Assert.Same(Hyprland4K, CompositorWorkArea.Match([Hyprland4K], "unknown", new OverlayFrame(0, 0, 1000, 700), 1));
        Assert.Null(CompositorWorkArea.Match([Hyprland4K], null, new OverlayFrame(0, 0, 1000, 700), 2));
    }
}
