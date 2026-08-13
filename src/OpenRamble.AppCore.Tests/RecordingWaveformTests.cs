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
}
