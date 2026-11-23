using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using OpenRamble.AppCore;

namespace OpenRamble.App.Linux;

/// <summary>
/// The shape a state wears (upstream <c>StateGlyphView</c>): a ring to do, a disc listening, a
/// turning arc working, an orange bar waiting, a check done. Colour from
/// <see cref="StatusBrushes"/>; the ink states take the theme's label colours.
/// </summary>
public sealed class StateGlyph : Control
{
    public static readonly StyledProperty<StateGlyphKind> KindProperty =
        AvaloniaProperty.Register<StateGlyph, StateGlyphKind>(nameof(Kind));

    /// <summary>The side of the square the glyph is drawn in: 12 on the panel, 15 in a setup row.</summary>
    public static readonly StyledProperty<double> GlyphSizeProperty =
        AvaloniaProperty.Register<StateGlyph, double>(nameof(GlyphSize), 15);

    // Lucide (ISC) outlines on their 24-unit grid; see Theme/Icons.axaml for the notice.
    private static readonly Geometry Lock = Geometry.Parse("M5 11h14a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-7a2 2 0 0 1 2-2z M7 11V7a5 5 0 0 1 10 0v4");
    private static readonly Geometry Check = Geometry.Parse("M20 6 9 17l-5-5");
    private static readonly Geometry Copy = Geometry.Parse("M10 8h10a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H10a2 2 0 0 1-2-2V10a2 2 0 0 1 2-2z M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2");
    private static readonly Geometry Inbox = Geometry.Parse("M22 12h-6l-2 3h-4l-2-3H2 M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z");
    private static readonly Geometry Info = Geometry.Parse("M2 12a10 10 0 1 0 20 0a10 10 0 1 0-20 0z M12 16v-4 M12 8h.01");
    private static readonly Geometry Alert = Geometry.Parse("m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3z M12 9v4 M12 17h.01");

    /// <summary>One turn of the working arc.</summary>
    private static readonly TimeSpan Turn = TimeSpan.FromSeconds(1.1);
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    static StateGlyph()
    {
        AffectsRender<StateGlyph>(KindProperty, GlyphSizeProperty);
        AffectsMeasure<StateGlyph>(GlyphSizeProperty);
    }

    public StateGlyph()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public StateGlyphKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double GlyphSize
    {
        get => GetValue(GlyphSizeProperty);
        set => SetValue(GlyphSizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(GlyphSize, GlyphSize);

    public override void Render(DrawingContext context)
    {
        double size = GlyphSize;
        var box = new Rect((Bounds.Width - size) / 2, (Bounds.Height - size) / 2, size, size);
        StateGlyphKind kind = Kind;
        IBrush color = kind.ColorRole is { } role ? StatusBrushes.For(role) : ThemeTokens.BrushOf(this, ThemeTokens.Label);
        IBrush secondary = ThemeTokens.BrushOf(this, ThemeTokens.Label2);

        switch (kind)
        {
            case StateGlyphKind.Silent:
            {
                double stroke = size / 9;
                double radius = (size / 2) - (size / 14) - (stroke / 2);
                context.DrawEllipse(null, new Pen(secondary, stroke), box.Center, radius, radius);
                break;
            }
            case StateGlyphKind.Listening:
            {
                double radius = (size / 2) - (size / 14);
                context.DrawEllipse(color, null, box.Center, radius, radius);
                break;
            }
            case StateGlyphKind.Working:
                DrawWorking(context, box, color);
                break;
            case StateGlyphKind.Waiting:
            {
                var bar = new Rect(box.X, box.Y + (size / 4), size, size / 2);
                context.DrawRectangle(color, null, new RoundedRect(bar, size / 6));
                break;
            }
            case StateGlyphKind.Nothing:
            {
                double height = Math.Max(1.5, size / 8);
                var line = new Rect(box.X, box.Center.Y - (height / 2), size, height);
                context.DrawRectangle(secondary, null, new RoundedRect(line, height / 2));
                break;
            }
            default:
                DrawIcon(context, box, IconFor(kind), color);
                break;
        }
    }

    private static Geometry IconFor(StateGlyphKind kind) => kind switch
    {
        StateGlyphKind.HandsFree => Lock,
        StateGlyphKind.Copied => Copy,
        StateGlyphKind.Pasted => Check,
        StateGlyphKind.Kept => Inbox,
        StateGlyphKind.Info => Info,
        _ => Alert,
    };

    /// <summary>A Lucide outline scaled into the box, drawn a touch heavier than the grid's 2 units, as the original's bold symbols are.</summary>
    private static void DrawIcon(DrawingContext context, Rect box, Geometry icon, IBrush color)
    {
        double scale = box.Width / 24;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(box.X, box.Y)))
        {
            context.DrawGeometry(null, new Pen(color, 2.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), icon);
        }
    }

    /// <summary>
    /// An open ring, three quarters round (the original trims .12–.88), turning while it is shown.
    /// </summary>
    private void DrawWorking(DrawingContext context, Rect box, IBrush color)
    {
        double size = box.Width;
        double stroke = size / 7;
        double radius = (size / 2) - (size / 7);
        double turn = Clock.Elapsed.TotalMilliseconds % Turn.TotalMilliseconds / Turn.TotalMilliseconds;
        double start = (turn * 2 * Math.PI) + (0.12 * 2 * Math.PI);
        double end = start + (0.76 * 2 * Math.PI);
        Point center = box.Center;
        var geometry = new StreamGeometry();
        using (StreamGeometryContext path = geometry.Open())
        {
            path.BeginFigure(new Point(center.X + (radius * Math.Cos(start)), center.Y + (radius * Math.Sin(start))), false);
            path.ArcTo(
                new Point(center.X + (radius * Math.Cos(end)), center.Y + (radius * Math.Sin(end))),
                new Size(radius, radius),
                0,
                true,
                SweepDirection.Clockwise);
            path.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(color, stroke, lineCap: PenLineCap.Round), geometry);
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ => InvalidateVisual());
    }
}
