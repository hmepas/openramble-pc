using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace OpenRamble.App.Linux.Controls;

/// <summary>
/// Draws one of the Lucide geometries in Theme/Icons.axaml the way Lucide means it: a stroked
/// outline in a 24x24 box, stroke width 2, round caps and joins, scaled to the control's size.
/// PathIcon fills its geometry, which turns these outlines into blobs.
/// </summary>
/// <example><c>&lt;c:LucideIcon Data="{StaticResource IconCopy}" Width="16" Height="16" /&gt;</c></example>
public sealed class LucideIcon : Control
{
    const double ViewBox = 24;

    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<LucideIcon, Geometry?>(nameof(Data));

    /// <summary>The stroke colour; inherits the text colour around it unless set.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<LucideIcon>();

    /// <summary>Stroke width in the icon's own 24-unit space (Lucide's default is 2).</summary>
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<LucideIcon, double>(nameof(StrokeThickness), 2);

    static LucideIcon()
    {
        AffectsRender<LucideIcon>(DataProperty, ForegroundProperty, StrokeThicknessProperty);
        WidthProperty.OverrideDefaultValue<LucideIcon>(16);
        HeightProperty.OverrideDefaultValue<LucideIcon>(16);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Data is null || Foreground is null)
            return;

        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
            return;

        var scale = size / ViewBox;
        var offset = new Point((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);
        var pen = new Pen(Foreground, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
            context.DrawGeometry(null, pen, Data);
    }
}
