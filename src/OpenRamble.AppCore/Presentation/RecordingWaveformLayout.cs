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

    /// <summary>
    /// Where bar <paramref name="index"/> of <paramref name="count"/> sits in a strip
    /// <paramref name="width"/> wide with <paramref name="spacing"/> between bars: the bars share
    /// what the gaps leave, never thinner than one unit.
    /// </summary>
    public static (double X, double Width) BarSlot(int index, int count, double width, double spacing)
    {
        if (count <= 0) { return (0, 0); }
        double available = Math.Max(0, width - (spacing * (count - 1)));
        double barWidth = Math.Max(1, available / count);
        return (index * (barWidth + spacing), barWidth);
    }

    /// <summary>
    /// A whole recording's peaks (one array per channel, one value per bucket) squeezed into
    /// <paramref name="count"/> columns: each the loudest bucket of any channel in its stretch.
    /// Fewer buckets than columns leaves the columns at one bucket each.
    /// </summary>
    public static float[] Columns(IReadOnlyList<float[]> channels, int count)
    {
        int buckets = channels.Count == 0 ? 0 : channels.Min(channel => channel.Length);
        if (buckets == 0 || count <= 0) { return []; }
        int columns = Math.Min(count, buckets);
        var result = new float[columns];
        for (int column = 0; column < columns; column++)
        {
            int from = (int)((long)column * buckets / columns);
            int to = Math.Max(from + 1, (int)((long)(column + 1) * buckets / columns));
            float loudest = 0;
            foreach (float[] channel in channels)
            {
                for (int bucket = from; bucket < to; bucket++) { loudest = Math.Max(loudest, channel[bucket]); }
            }

            result[column] = loudest;
        }

        return result;
    }
}
