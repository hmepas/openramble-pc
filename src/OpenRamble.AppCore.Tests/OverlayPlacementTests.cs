namespace OpenRamble.AppCore.Tests;

/// <summary>
/// Where the panel goes. The frame is the same as the upstream test's; the Y answers are this
/// platform's, because the screen's origin is the top-left corner here and the bottom-left
/// on a Mac.
/// </summary>
public sealed class OverlayPlacementTests
{
    private static readonly OverlayFrame VisibleFrame = new(100, 40, 1200, 760);
    private static readonly OverlaySize PanelSize = new(244, 52);

    [Fact]
    public void TestTopCentersPanelBelowVisibleFrameEdge()
    {
        OverlayPoint origin = OverlayPlacementPolicy.Origin(DictationOverlayPlacement.Top, VisibleFrame, PanelSize);

        Assert.Equal(578, origin.X);
        Assert.Equal(64, origin.Y);
    }

    [Fact]
    public void TestBottomCentersPanelAboveVisibleFrameEdge()
    {
        OverlayPoint origin = OverlayPlacementPolicy.Origin(DictationOverlayPlacement.Bottom, VisibleFrame, PanelSize);

        Assert.Equal(578, origin.X);
        Assert.Equal(724, origin.Y);
    }

    /// <summary>The default is the top, and the stored spelling is the Mac's.</summary>
    [Fact]
    public void TestPlacementDefaultsToTopAndPersists()
    {
        Assert.Equal(DictationOverlayPlacement.Top, DictationOverlayPlacements.Parse(null));
        Assert.Equal(DictationOverlayPlacement.Top, DictationOverlayPlacements.Parse("sideways"));
        Assert.Equal(DictationOverlayPlacement.Bottom, DictationOverlayPlacements.Parse("bottom"));
        Assert.Equal("bottom", DictationOverlayPlacement.Bottom.RawValue);
        Assert.Equal("Bottom", DictationOverlayPlacement.Bottom.Title);
    }
}
