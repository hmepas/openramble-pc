using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenRamble.App.Linux.Controls;

namespace OpenRamble.App.Linux;

/// <summary>
/// The Library's pieces built in code from the theme (Theme/Tokens.axaml, Icons.axaml): icons, the
/// serif, the round disc in front of a row, an icon-and-label button.
/// </summary>
internal static class LibraryLook
{
    /// <summary>The person's words: the bundled serif (falls back to the system serif).</summary>
    public static FontFamily Serif =>
        Application.Current?.TryGetResource("SerifFont", null, out object? value) == true && value is FontFamily family
            ? family
            : new FontFamily("Noto Serif, Liberation Serif, serif");

    /// <summary>A Lucide outline by its resource key (IconMic, IconCopy, ...).</summary>
    public static Geometry? Geometry(string key) =>
        Application.Current?.TryGetResource(key, null, out object? value) == true ? value as Geometry : null;

    public static LucideIcon Icon(string key, double size = 16) => new()
    {
        Data = Geometry(key),
        Width = size,
        Height = size,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>The 30 circle before a row: what kind of thing it is.</summary>
    public static Control RowDisc(string iconKey)
    {
        var disc = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            VerticalAlignment = VerticalAlignment.Top,
            Child = Icon(iconKey, 12),
        };
        ThemeTokens.Bind(disc, Border.BackgroundProperty, "ContentFill");
        ThemeTokens.Bind(disc.Child, LucideIcon.ForegroundProperty, ThemeTokens.Label2);
        return disc;
    }

    /// <summary>An icon then a label, as a button's content.</summary>
    public static StackPanel Labelled(string iconKey, string label, out LucideIcon icon, out TextBlock text)
    {
        icon = Icon(iconKey, 14);
        text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { icon, text } };
    }

    /// <summary>The 32 circle toolbar button holding one icon.</summary>
    public static Button IconButton(string iconKey, string tip)
    {
        var button = new Button { Classes = { "icon" }, Content = Icon(iconKey) };
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, tip);
        return button;
    }
}
