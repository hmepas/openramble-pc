using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using OpenRamble.AppCore;

namespace OpenRamble.App.Linux;

/// <summary>
/// The microphone meter (upstream <c>RecordingWaveform</c>): one rounded bar per recent peak,
/// oldest first, 2 units apart. Height grows with the square root of the level so speech fills
/// the strip and silence stays a thin line; older bars fade. The maths is
/// <see cref="RecordingWaveformLayout"/>'s.
/// </summary>
/// <remarks>Set a new list to redraw: the same instance mutated in place is not noticed.</remarks>
public sealed class LevelWaveform : Control
{
    public static readonly StyledProperty<IReadOnlyList<float>?> SamplesProperty =
        AvaloniaProperty.Register<LevelWaveform, IReadOnlyList<float>?>(nameof(Samples));

    /// <summary>The bars' colour: the glyph's role. <c>null</c> - the theme's secondary label.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<LevelWaveform, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<LevelWaveform, double>(nameof(Spacing), 2);

    static LevelWaveform()
    {
        AffectsRender<LevelWaveform>(SamplesProperty, ForegroundProperty, SpacingProperty);
    }

    public LevelWaveform()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>Recent peaks in 0…1, oldest first.</summary>
    public IReadOnlyList<float>? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        IReadOnlyList<float>? samples = Samples;
        Rect bounds = Bounds;
        if (samples is null || samples.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0) { return; }

        IBrush brush = Foreground ?? ThemeTokens.BrushOf(this, ThemeTokens.Label2);
        int count = samples.Count;
        for (int index = 0; index < count; index++)
        {
            (double x, double width) = RecordingWaveformLayout.BarSlot(index, count, bounds.Width, Spacing);
            double height = RecordingWaveformLayout.BarHeight(samples[index], bounds.Height);
            var bar = new Rect(x, (bounds.Height - height) / 2, width, height);
            using (context.PushOpacity(RecordingWaveformLayout.BarOpacity(index, count)))
            {
                context.DrawRectangle(brush, null, new RoundedRect(bar, Math.Min(width, height) / 2));
            }
        }
    }
}
