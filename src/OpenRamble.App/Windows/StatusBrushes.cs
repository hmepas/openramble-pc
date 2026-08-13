using System.Windows.Media;
using OpenRamble.AppCore;

namespace OpenRamble.App;

/// <summary>
/// The status vocabulary, in paint.
///
/// <see cref="StatusColorRole"/> says what each colour means; this is the only place that says
/// which colour it is. Same values as the tray dot in <see cref="TrayIconArt"/> - a person learns
/// each colour once, and a mismatch between the icon and the panel would be teaching them twice.
/// </summary>
/// <remarks>
/// Fixed values rather than system colours: the accent colour is the person's decoration and
/// changes on a whim, while "red means the microphone is live" is a promise. Each pair is picked
/// to keep contrast against both the light and the dark panel background, and to stay
/// distinguishable by luminance for a person who cannot separate the hues.
/// </remarks>
public static class StatusBrushes
{
    public static SolidColorBrush Recording { get; } = Frozen(0xE5, 0x48, 0x4D);

    public static SolidColorBrush Processing { get; } = Frozen(0x3E, 0x63, 0xDD);

    public static SolidColorBrush Success { get; } = Frozen(0x30, 0xA4, 0x6C);

    public static SolidColorBrush Attention { get; } = Frozen(0xF7, 0x6B, 0x15);

    public static SolidColorBrush Neutral { get; } = Frozen(0x8B, 0x8D, 0x98);

    public static SolidColorBrush For(StatusColorRole? role) => role switch
    {
        StatusColorRole.Recording => Recording,
        StatusColorRole.Processing => Processing,
        StatusColorRole.Success => Success,
        StatusColorRole.Attention => Attention,
        _ => Neutral,
    };

    /// <summary>
    /// Frozen so the brushes can be shared across windows and threads: an unfrozen brush belongs
    /// to the thread that made it.
    /// </summary>
    private static SolidColorBrush Frozen(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
