namespace OpenRamble.AppCore;

/// <summary>
/// The shape each state wears. Shape and word carry the state; colour only confirms it, so every
/// state reads in grayscale and under any colour filter. Port of upstream <c>StateGlyph</c>
/// (UI/Blocks.swift); the drawing is the shell's.
/// </summary>
public enum StateGlyphKind
{
    /// <summary>A hollow gray ring: the key is down and no sound has arrived, or a setup step is still to do.</summary>
    Silent,

    /// <summary>A red disc: the microphone is capturing. Red means nothing else.</summary>
    Listening,

    /// <summary>A red lock: capturing with the key let go.</summary>
    HandsFree,

    /// <summary>An open blue ring: the words are being written down, or a setup step is under way.</summary>
    Working,

    /// <summary>An orange bar: words wait for the person. Never a dot, so it cannot pass for a microphone indicator.</summary>
    Waiting,

    /// <summary>The copy sign, in ink: the words are on the clipboard and still wait.</summary>
    Copied,

    /// <summary>A green check: the words landed, or a setup step is done.</summary>
    Pasted,

    /// <summary>An outlined tray, in ink: the words are kept in the Library.</summary>
    Kept,

    /// <summary>A flat line, in ink: nothing was heard.</summary>
    Nothing,

    /// <summary>The app has something to say that is not about a take.</summary>
    Info,

    /// <summary>Something needs the person, and their work is safe.</summary>
    Attention,
}

public static class StateGlyphKindInfo
{
    extension(StateGlyphKind kind)
    {
        /// <summary>The status colour the glyph is painted in; <c>null</c> - ink or secondary.</summary>
        public StatusColorRole? ColorRole => kind switch
        {
            StateGlyphKind.Listening or StateGlyphKind.HandsFree => StatusColorRole.Recording,
            StateGlyphKind.Working or StateGlyphKind.Info => StatusColorRole.Processing,
            StateGlyphKind.Waiting or StateGlyphKind.Attention => StatusColorRole.Attention,
            StateGlyphKind.Pasted => StatusColorRole.Success,
            _ => null,
        };
    }
}
