using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace OpenRamble.App.Linux;

/// <summary>
/// The design tokens the foundation controls paint with, looked up by name in the theme's
/// resources (Theme/Tokens.axaml) for the element's light or dark variant.
/// </summary>
/// <remarks>
/// Each key also carries the spec's own value as a fallback, so a control drawn where the
/// dictionary is missing still reads right in both variants. The dictionary is the authority: a
/// value found there wins, whether it is stored as a brush or as a colour.
/// </remarks>
internal static class ThemeTokens
{
    public const string Label = "Label";
    public const string Label2 = "Label2";
    public const string Label3 = "Label3";
    public const string Fill = "Fill";
    public const string Hairline = "Hairline";
    public const string Accent = "Accent";
    public const string InkForeground = "InkForeground";
    public const string KeyCapFaceTop = "KeyCapFaceTop";
    public const string KeyCapFaceBottom = "KeyCapFaceBottom";
    public const string KeyCapSide = "KeyCapSide";
    public const string KeyCapEdge = "KeyCapEdge";
    public const string KeyCapShadow = "KeyCapShadow";

    private static readonly Dictionary<string, (uint Light, uint Dark)> Fallbacks = new()
    {
        [Label] = (0xD9000000, 0xEBFFFFFF),
        [Label2] = (0x80000000, 0x8CFFFFFF),
        [Label3] = (0x47000000, 0x4DFFFFFF),
        [Fill] = (0x1A747480, 0x3D767680),
        [Hairline] = (0x1F000000, 0x1FFFFFFF),
        [Accent] = (0xFF0A84FF, 0xFF0A84FF),
        [InkForeground] = (0xFFFFFFFF, 0xFF000000),
        [KeyCapFaceTop] = (0xFFFFFFFF, 0xFF4D4D4D),
        [KeyCapFaceBottom] = (0xFFF4F4F4, 0xFF383838),
        [KeyCapSide] = (0xFFC7C7C7, 0xFF171717),
        [KeyCapEdge] = (0x1A000000, 0x33FFFFFF),
        [KeyCapShadow] = (0x29000000, 0x80000000),
    };

    public static bool IsDark(StyledElement element) => element.ActualThemeVariant == ThemeVariant.Dark;

    /// <summary>The token's colour for the element's current variant.</summary>
    public static Color ColorOf(Control element, string key)
    {
        if (element.TryFindResource(key, element.ActualThemeVariant, out object? value))
        {
            switch (value)
            {
                case Color color: return color;
                case ISolidColorBrush brush: return brush.Color;
            }
        }

        return Fallback(key, IsDark(element));
    }

    /// <summary>The token's brush for the element's current variant.</summary>
    public static IBrush BrushOf(Control element, string key)
    {
        if (element.TryFindResource(key, element.ActualThemeVariant, out object? value) && value is IBrush brush)
        {
            return brush;
        }

        return new ImmutableSolidColorBrush(ColorOf(element, key));
    }

    /// <summary>
    /// Binds <paramref name="property"/> of <paramref name="target"/> to the token, following the
    /// theme variant as it changes.
    /// </summary>
    public static void Bind(Control target, AvaloniaProperty property, string key) =>
        target.Bind(property, target.GetResourceObservable(key, value => value switch
        {
            IBrush brush => brush,
            Color color => new ImmutableSolidColorBrush(color),
            // Not found yet - typically a control built in code and not attached to a window,
            // where no theme dictionary is in reach. Leave the property unset: the observable
            // fires again with the real value once the control joins the tree.
            _ => Fallbacks.ContainsKey(key)
                ? new ImmutableSolidColorBrush(Fallback(key, IsDark(target)))
                : AvaloniaProperty.UnsetValue,
        }));

    /// <summary>
    /// The spec's value for the foundation controls' own keys; any other key has no fallback and
    /// draws transparent until the theme dictionary answers - never an exception from a lookup.
    /// </summary>
    private static Color Fallback(string key, bool dark) =>
        Fallbacks.TryGetValue(key, out (uint Light, uint Dark) value)
            ? Color.FromUInt32(dark ? value.Dark : value.Light)
            : Colors.Transparent;
}
