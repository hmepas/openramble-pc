using DictationCore;

namespace OpenRamble.AppCore;

/// <summary>Why words that could not land are waiting.</summary>
/// <remarks>
/// Upstream also names the application that holds macOS's secure input and has a fourth cause,
/// "typing works again", found by polling that system-wide flag while the words wait. Neither has
/// a source here: Windows protects one window at a time (UIPI) and says nothing about who, and
/// Linux has no such flag at all. So protected typing is said without a name, and the key copies
/// until the words leave.
/// </remarks>
public enum WaitingCause
{
    /// <summary>
    /// Typing is protected and our paste cannot get through - on Windows, a window running with
    /// more rights than this app. The person's own Ctrl+V gets through.
    /// </summary>
    TypingProtected,

    /// <summary>Ctrl+V was sent and nothing read it: focus is not in a text field.</summary>
    NoTextField,

    /// <summary>Anything else. Trying the paste again is the whole answer.</summary>
    NotPasted,
}

/// <summary>What a tap of the dictation key does while words wait.</summary>
public enum WaitingAction
{
    /// <summary>The person's own Ctrl+V gets through whatever stopped ours, so the key copies.</summary>
    Copy,
    Paste,
}

/// <summary>
/// Words that could not land, waiting where the person works (upstream 0.37.0).
/// </summary>
/// <remarks>
/// A paste can fail for reasons the person can fix in a second: another window protects typing, or
/// focus is not in a text field. The words stay on the dictation panel, in full, with the reason in
/// plain words and one action bound to the dictation key. Nothing sends the person to the tray.
///
/// A pure type: what the panel says and what the key does are decided here and checked by a state
/// table, not by looking at the screen.
/// </remarks>
/// <param name="Words">The words, as they would have been pasted.</param>
/// <param name="IsCopied">The words are on the clipboard and wait for the person's own Ctrl+V.</param>
/// <param name="IsKeptNowhereElse">
/// History could not keep this take, so the panel is the only place the words are. Said on the
/// panel, and Later does not claim otherwise.
/// </param>
public sealed record WaitingWords(
    string Words,
    WaitingCause Cause,
    bool IsCopied = false,
    bool IsKeptNowhereElse = false)
{
    /// <summary>The cause, from what the paste reported. A paste that timed out reports nothing.</summary>
    public static WaitingCause CauseOf(TextInsertionError? failure) => failure switch
    {
        TextInsertionError.SecureInputActive => WaitingCause.TypingProtected,
        TextInsertionError.PasteNotRead => WaitingCause.NoTextField,
        _ => WaitingCause.NotPasted,
    };

    /// <summary>What else the person must know, or <c>null</c>.</summary>
    public string? Note => IsKeptNowhereElse ? "This dictation couldn't be saved to History." : null;

    public WaitingAction Action => Cause == WaitingCause.TypingProtected ? WaitingAction.Copy : WaitingAction.Paste;

    /// <summary>Why, in about eight words. <paramref name="key"/> is the dictation key's name.</summary>
    public string Reason(string key)
    {
        if (IsCopied) { return "Copied. Press Ctrl+V to paste."; }
        return Cause switch
        {
            WaitingCause.TypingProtected => "Didn't paste while typing is protected.",
            WaitingCause.NoTextField => $"No text field. Click one, then tap {key}.",
            WaitingCause.NotPasted => "Not pasted.",
            _ => throw new ArgumentOutOfRangeException(nameof(Cause)),
        };
    }

    /// <summary>What the key does, as the word beside its name: "copies", "pastes".</summary>
    public string ActionVerb => Action == WaitingAction.Copy ? "copies" : "pastes";

    /// <summary>
    /// The whole panel, for a screen reader: state, reason, then what the key does. Never the words
    /// themselves: they would be read aloud to the room.
    /// </summary>
    public string AccessibilityLabel(string key)
    {
        if (IsCopied) { return $"Words waiting. {Reason(key)}"; }
        return $"Words waiting. {Reason(key)} {key} {ActionVerb}.";
    }

    /// <summary>
    /// Never the words: whatever can be printed is eventually logged, and a record's generated
    /// <c>ToString</c> would print every one of them.
    /// </summary>
    public override string ToString() =>
        $"WaitingWords {{ Chars = {Words.Length}, Cause = {Cause}, IsCopied = {IsCopied}, IsKeptNowhereElse = {IsKeptNowhereElse} }}";
}

/// <summary>How a waiting take ended, said once on the panel before it leaves.</summary>
public abstract record TakeOutcome
{
    private TakeOutcome()
    {
    }

    /// <summary>The words landed after waiting. <paramref name="Into"/> is where, when known.</summary>
    public sealed record Pasted(string? Into) : TakeOutcome;

    /// <summary>
    /// The person chose Later, or pressed Escape. <paramref name="Chord"/> is the paste-last
    /// shortcut, when one is set. <paramref name="InHistory"/> is false for the rare take History
    /// could not keep: then the chord is the only way back.
    /// </summary>
    public sealed record Kept(bool InHistory, string? Chord) : TakeOutcome;

    public string Message => this switch
    {
        Pasted { Into: string app } => $"Pasted into {app}.",
        Pasted => "Pasted.",
        Kept { InHistory: true, Chord: string chord } => $"Kept in History. {chord} pastes it.",
        Kept { InHistory: true } => "Kept in History.",
        Kept { Chord: string chord } => $"{chord} pastes it until you quit.",
        Kept => "The menu pastes it until you quit.",
        _ => throw new InvalidOperationException("an outcome is pasted or kept"),
    };

    /// <summary>Long enough to read, short enough not to be in the way.</summary>
    public TimeSpan Duration => this is Pasted ? TimeSpan.FromMilliseconds(700) : TimeSpan.FromSeconds(3);
}
