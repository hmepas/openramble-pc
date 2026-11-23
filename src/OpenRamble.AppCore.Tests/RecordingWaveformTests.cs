using OpenRamble.AppCore;

namespace OpenRamble.AppCore.Tests;

public sealed class RecordingWaveformTests
{
    [Fact]
    public void SilenceStaysVisibleAsAThinCenterLine()
    {
        Assert.Equal(1, RecordingWaveformLayout.BarHeight(0, 28), 3);
    }

    [Fact]
    public void VoiceUsesTheAvailableHeightWithoutEscapingIt()
    {
        Assert.Equal(14.5, RecordingWaveformLayout.BarHeight(0.25f, 28), 3);
        Assert.Equal(28, RecordingWaveformLayout.BarHeight(1, 28), 3);
        Assert.Equal(28, RecordingWaveformLayout.BarHeight(4, 28), 3);
    }

    /// <summary>
    /// The rolling signal fades toward the past: newest bar fully opaque, oldest still readable,
    /// strictly increasing in between.
    /// </summary>
    [Fact]
    public void BarOpacityRampsFromFadedPastToOpaquePresent()
    {
        const int count = 24;
        Assert.Equal(1.0, RecordingWaveformLayout.BarOpacity(count - 1, count), 4);
        Assert.Equal(0.35 + (0.65 / count), RecordingWaveformLayout.BarOpacity(0, count), 4);

        for (int index = 1; index < count; index++)
        {
            Assert.True(
                RecordingWaveformLayout.BarOpacity(index, count)
                    > RecordingWaveformLayout.BarOpacity(index - 1, count));
        }

        Assert.Equal(1.0, RecordingWaveformLayout.BarOpacity(0, 1));
    }

    /// <summary>
    /// The dictation meter's strip: 24 bars with 2-unit gaps fill the width exactly, the last bar
    /// ending on the right edge.
    /// </summary>
    [Fact]
    public void BarsShareTheWidthTheGapsLeave()
    {
        (double firstX, double width) = RecordingWaveformLayout.BarSlot(0, 24, 142, 2);
        (double lastX, double lastWidth) = RecordingWaveformLayout.BarSlot(23, 24, 142, 2);

        Assert.Equal(0, firstX, 6);
        Assert.Equal(4, width, 6);
        Assert.Equal(lastWidth, width, 6);
        Assert.Equal(142, lastX + lastWidth, 6);
    }

    [Fact]
    public void ANarrowStripKeepsEveryBarAtLeastOneUnitWide()
    {
        (double x, double width) = RecordingWaveformLayout.BarSlot(3, 24, 20, 2);

        Assert.Equal(1, width, 6);
        Assert.Equal(9, x, 6);
    }

    [Fact]
    public void NoBarsHaveNoSlot()
    {
        Assert.Equal((0d, 0d), RecordingWaveformLayout.BarSlot(0, 0, 100, 2));
    }
}
