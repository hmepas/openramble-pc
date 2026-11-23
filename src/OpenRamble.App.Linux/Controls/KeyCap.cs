using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace OpenRamble.App.Linux;

/// <summary>How big a key cap is drawn (upstream <c>KeyCap.Size</c>).</summary>
public enum KeyCapSize
{
    /// <summary>Inside a line of text or a button: 22 tall.</summary>
    Inline,

    /// <summary>A setting's current value: 64 tall.</summary>
    Large,

    /// <summary>The one thing on the screen, onboarding: 148 tall.</summary>
    Hero,
}

/// <summary>
/// A key, drawn as the thing a finger rests on (upstream <c>KeyCap</c>): a face lit from above, a
/// side that shows its travel, a shadow on the desk. While <see cref="IsPressed"/> is true the
/// face goes down by the travel and the side disappears under it.
/// </summary>
public sealed class KeyCap : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<KeyCap, string?>(nameof(Text));

    public static readonly StyledProperty<KeyCapSize> CapSizeProperty =
        AvaloniaProperty.Register<KeyCap, KeyCapSize>(nameof(CapSize));

    public static readonly StyledProperty<bool> IsPressedProperty =
        AvaloniaProperty.Register<KeyCap, bool>(nameof(IsPressed));

    /// <summary>Drawn on the ink of a primary button rather than on a surface: no side, no shadow.</summary>
    public static readonly StyledProperty<bool> OnInkProperty =
        AvaloniaProperty.Register<KeyCap, bool>(nameof(OnInk));

    /// <summary>0 at rest, 1 fully down; animated so the cap moves rather than jumps.</summary>
    private static readonly StyledProperty<double> DepthProperty =
        AvaloniaProperty.Register<KeyCap, double>("Depth");

    static KeyCap()
    {
        AffectsRender<KeyCap>(TextProperty, CapSizeProperty, OnInkProperty, DepthProperty);
        AffectsMeasure<KeyCap>(TextProperty, CapSizeProperty);
    }

    public KeyCap()
    {
        Transitions =
        [
            new DoubleTransition { Property = DepthProperty, Duration = TimeSpan.FromMilliseconds(140), Easing = new CubicEaseOut() },
        ];
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public KeyCapSize CapSize
    {
        get => GetValue(CapSizeProperty);
        set => SetValue(CapSizeProperty, value);
    }

    public bool IsPressed
    {
        get => GetValue(IsPressedProperty);
        set => SetValue(IsPressedProperty, value);
    }

    public bool OnInk
    {
        get => GetValue(OnInkProperty);
        set => SetValue(OnInkProperty, value);
    }

    private (double Height, double Radius, double Travel, double MinWidth, double Padding, double FontSize, FontWeight Weight, double Shadow) Metrics =>
        CapSize switch
        {
            KeyCapSize.Large => (64, 15, 4, 128, 22, 21, FontWeight.Medium, 10),
            KeyCapSize.Hero => (148, 30, 9, 280, 44, 40, FontWeight.Medium, 26),
            _ => (22, 6, 1.5, 22, 7, 12, FontWeight.SemiBold, 0),
        };

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsPressedProperty)
        {
            SetValue(DepthProperty, IsPressed ? 1.0 : 0.0);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var metrics = Metrics;
        double width = Math.Max(metrics.MinWidth, Label(Brushes.Black).Width + (2 * metrics.Padding));
        return new Size(width, metrics.Height + metrics.Travel);
    }

    public override void Render(DrawingContext context)
    {
        var metrics = Metrics;
        bool dark = ThemeTokens.IsDark(this);
        bool onInk = OnInk;
        double depth = GetValue(DepthProperty);
        double width = Bounds.Width;
        var rest = new Rect(0, 0, width, metrics.Height);

        if (!onInk)
        {
            // The side of the key: what shows below the cap at rest, gone when it is down.
            var side = new RoundedRect(rest.Translate(new Vector(0, metrics.Travel)), metrics.Radius);
            BoxShadows shadows = default;
            if (metrics.Shadow > 0)
            {
                double blur = metrics.Shadow * (1 - (0.65 * depth));
                double offset = metrics.Shadow * (0.55 - (0.35 * depth));
                shadows = new BoxShadows(new BoxShadow
                {
                    OffsetY = offset,
                    Blur = blur,
                    Color = ThemeTokens.ColorOf(this, ThemeTokens.KeyCapShadow),
                });
            }

            context.DrawRectangle(ThemeTokens.BrushOf(this, ThemeTokens.KeyCapSide), null, side, shadows);
        }

        Rect faceRect = rest.Translate(new Vector(0, metrics.Travel * depth));
        double edgeWidth = CapSize == KeyCapSize.Inline ? 0.75 : 1;
        var face = new RoundedRect(faceRect, metrics.Radius);
        var edge = new RoundedRect(faceRect.Deflate(edgeWidth / 2), Math.Max(0, metrics.Radius - (edgeWidth / 2)));
        (Color top, Color bottom, Color edgeColor) = onInk
            ? dark
                ? (Color.FromUInt32(0x0F000000), Color.FromUInt32(0x1A000000), Color.FromUInt32(0x4D000000))
                : (Color.FromUInt32(0x33FFFFFF), Color.FromUInt32(0x21FFFFFF), Color.FromUInt32(0x73FFFFFF))
            : (ThemeTokens.ColorOf(this, ThemeTokens.KeyCapFaceTop),
                ThemeTokens.ColorOf(this, ThemeTokens.KeyCapFaceBottom),
                ThemeTokens.ColorOf(this, ThemeTokens.KeyCapEdge));
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) },
        };
        context.DrawRectangle(gradient, null, face);
        context.DrawRectangle(null, new Pen(new ImmutableSolidColorBrush(edgeColor), edgeWidth), edge);

        IBrush ink = ThemeTokens.BrushOf(this, onInk ? ThemeTokens.InkForeground : ThemeTokens.Label);
        FormattedText label = Label(ink);
        context.DrawText(label, new Point(
            faceRect.X + ((faceRect.Width - label.Width) / 2),
            faceRect.Y + ((faceRect.Height - label.Height) / 2)));
    }

    private FormattedText Label(IBrush brush)
    {
        var metrics = Metrics;
        var typeface = new Typeface(GetValue(TextElement.FontFamilyProperty), FontStyle.Normal, metrics.Weight);
        return new FormattedText(Text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, metrics.FontSize, brush);
    }
}
