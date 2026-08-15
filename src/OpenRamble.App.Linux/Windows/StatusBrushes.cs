using Avalonia.Media;
using Avalonia.Media.Immutable;
using OpenRamble.AppCore;

namespace OpenRamble.App.Linux;

/// <summary>
/// The status vocabulary, in paint.
///
/// <see cref="StatusColorRole"/> says what each colour means; this is the only place that says
/// which colour it is. Same values as the Windows shell's <c>StatusBrushes</c> and as the tray dot
/// in <see cref="TrayIconArt"/> - a person learns each colour once, and two platforms that
/// disagreed about red would be teaching them twice.
/// </summary>
/// <remarks>
/// Fixed values rather than the desktop's accent colour: the accent is the person's decoration and
/// changes on a whim, while "red means the microphone is live" is a promise. Each pair is picked to
/// keep contrast against both the light and the dark panel background, and to stay distinguishable
/// by luminance for a person who cannot separate the hues.
/// </remarks>
public static class StatusBrushes
{
    public static ImmutableSolidColorBrush Recording { get; } = Of(0xE5, 0x48, 0x4D);

    public static ImmutableSolidColorBrush Processing { get; } = Of(0x3E, 0x63, 0xDD);

    public static ImmutableSolidColorBrush Success { get; } = Of(0x30, 0xA4, 0x6C);

    public static ImmutableSolidColorBrush Attention { get; } = Of(0xF7, 0x6B, 0x15);

    public static ImmutableSolidColorBrush Neutral { get; } = Of(0x8B, 0x8D, 0x98);

    public static ImmutableSolidColorBrush For(StatusColorRole? role) => role switch
    {
        StatusColorRole.Recording => Recording,
        StatusColorRole.Processing => Processing,
        StatusColorRole.Success => Success,
        StatusColorRole.Attention => Attention,
        _ => Neutral,
    };

    /// <summary>
    /// Immutable, which is Avalonia's word for what WPF calls frozen: the brushes are shared
    /// between the panel, the setup window and every button that is rebuilt on a render.
    /// </summary>
    private static ImmutableSolidColorBrush Of(byte red, byte green, byte blue) =>
        new(Color.FromRgb(red, green, blue));
}
