namespace OpenRamble.AppCore;

/// <summary>
/// Sample → bar arithmetic for the rolling view of the microphone signal.
///
/// Samples are drawn directly: there is no decorative loop or interpolation that could imply
/// sound the microphone did not receive.
/// </summary>
public static class RecordingWaveformLayout
{
    /// <summary>
    /// Bar height for a peak in 0…1. Silence stays visible as a one-unit centre line: an empty
    /// panel would read as a dead microphone.
    /// </summary>
    public static double BarHeight(float sample, double maximum)
    {
        double normalized = Math.Clamp(sample, 0f, 1f);
        return 1 + ((maximum - 1) * Math.Sqrt(normalized));
    }

    /// <summary>
    /// Oldest bars fade, the newest is fully opaque: the ramp gives the rolling signal a
    /// direction without animating anything of its own.
    /// </summary>
    public static double BarOpacity(int index, int count)
    {
        if (count <= 1)
        {
            return 1;
        }

        return 0.35 + (0.65 * (index + 1) / count);
    }
}
