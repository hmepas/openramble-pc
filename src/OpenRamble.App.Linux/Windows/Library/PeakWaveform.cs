using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using OpenRamble.AppCore;

namespace OpenRamble.App.Linux;

/// <summary>
/// A whole recording's loudness from its <c>peaks.bin</c>, drawn as bars behind the transport
/// slider; what has been played is drawn darker.
/// </summary>
internal sealed class PeakWaveform : Control
{
    private const double BarWidth = 2;
    private const double Gap = 1;
    private static readonly IBrush Played = new SolidColorBrush(Color.FromArgb(0x99, 0x3E, 0x63, 0xDD));
    private static readonly IBrush Ahead = new SolidColorBrush(Color.FromArgb(0x40, 0x8B, 0x8D, 0x98));

    private IReadOnlyList<float[]> channels = [];
    private double progress;

    /// <summary>The peaks, one array per channel; empty draws nothing.</summary>
    public IReadOnlyList<float[]> Channels
    {
        get => channels;
        set
        {
            channels = value;
            InvalidateVisual();
        }
    }

    /// <summary>How much has been played, 0 to 1.</summary>
    public double Progress
    {
        get => progress;
        set
        {
            double clamped = Math.Clamp(value, 0, 1);
            if (Math.Abs(clamped - progress) < 0.0005) { return; }
            progress = clamped;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        Rect bounds = Bounds;
        int count = (int)(bounds.Width / (BarWidth + Gap));
        float[] columns = RecordingWaveformLayout.Columns(channels, count);
        if (columns.Length == 0) { return; }

        double step = bounds.Width / columns.Length;
        double width = Math.Max(1, step - Gap);
        double middle = bounds.Height / 2;
        for (int index = 0; index < columns.Length; index++)
        {
            double height = RecordingWaveformLayout.BarHeight(columns[index], bounds.Height);
            double x = index * step;
            IBrush brush = (x + (width / 2)) / bounds.Width < progress ? Played : Ahead;
            context.FillRectangle(brush, new Rect(x, middle - (height / 2), width, height));
        }
    }
}
