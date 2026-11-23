namespace OpenRamble.AppCore;

/// <summary>
/// What the Linux input path's two setup rows say - the dictation key reaching the app, and the
/// tools that put the words at the cursor - written once for onboarding and Settings → Setup.
/// </summary>
public static class InputPathRows
{
    /// <summary>
    /// The key arrives through /dev/input (the input group, or keyd) or a compositor keybind
    /// feeding the control socket. Only an edge proves either, so until one is heard the row asks
    /// for a press.
    /// </summary>
    public static (StateGlyphKind Glyph, string Detail) Key(
        string keyTitle, string? trackingFailure, string? warning, bool heard) =>
        trackingFailure is not null ? (StateGlyphKind.Attention, trackingFailure)
        : warning is not null ? (StateGlyphKind.Attention, warning)
        : heard ? (StateGlyphKind.Pasted, $"OpenRamble hears {keyTitle}.")
        : (StateGlyphKind.Silent,
            $"Press {keyTitle} to check that it reaches OpenRamble — through the input group, keyd or a compositor keybind.");

    /// <summary>wl-copy and a typing tool (wtype or ydotool) on PATH, or what to install.</summary>
    public static (StateGlyphKind Glyph, string Detail) Typing(bool toolsInstalled) =>
        toolsInstalled
            ? (StateGlyphKind.Pasted, "wl-copy and a typing tool are installed, so the words land at your cursor.")
            : (StateGlyphKind.Attention, "Install wl-clipboard and wtype (or ydotool) so the words can land at your cursor.");
}
