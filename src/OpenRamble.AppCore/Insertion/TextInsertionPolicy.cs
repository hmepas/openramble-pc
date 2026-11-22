using DictationCore;

namespace OpenRamble.AppCore;

/// <summary>Why the insertion is not going ahead.</summary>
/// <remarks>
/// Each case maps onto exactly one <see cref="TextInsertionError"/> from the port boundary; the
/// adapter throws, the policy only decides. <see cref="ProtectedTarget"/> is the recast of
/// macOS's secure input: Windows has no <c>IsSecureEventInputEnabled</c>, but it has UIPI - a
/// window owned by a higher-integrity process discards synthetic input <b>silently</b>, which
/// is precisely the failure mode the never-silently-discard rule forbids. Detecting it and
/// saying so is the only honest option; it maps to
/// <see cref="TextInsertionError.SecureInputActive"/> because the meaning is identical: you
/// cannot insert there, and it is not a breakage but a normal situation.
/// </remarks>
public enum InsertionRefusal
{
    /// <summary>There is nowhere to insert: no target was captured, or it is gone.</summary>
    TargetUnavailable,

    /// <summary>The active application has changed since the dictation target was captured.</summary>
    TargetChanged,

    /// <summary>An elevated or otherwise protected window would swallow the keystrokes.</summary>
    ProtectedTarget,

    /// <summary>The person did not release the modifiers, and Ctrl+V would become someone else's combination.</summary>
    ModifiersStillHeld,
}

/// <summary>
/// What the system says about the moment of insertion.
/// </summary>
/// <param name="FrontmostProcessId">
/// The process that owns the foreground window right now. <c>0</c> - nothing readable.
/// </param>
/// <param name="TargetIsProtected">
/// Whether the foreground window belongs to a process this one cannot send input to (elevated,
/// or otherwise above us on the UIPI ladder).
/// </param>
public readonly record struct InsertionEnvironment(int FrontmostProcessId, bool TargetIsProtected);

/// <summary>
/// The decisions <c>TextInserter</c> makes, lifted out of the Win32 that carries them out.
///
/// The order is the product decision, not an implementation detail. Focus is returned and the
/// target validated <b>before</b> the clipboard is touched: if the application is gone there is
/// no point continuing, and at that moment the clipboard is still untouched - otherwise the
/// person would lose what they had copied to an insertion that never happened. The same checks
/// run again after the clipboard write, immediately before the irreversible Ctrl+V.
/// </summary>
public static class TextInsertionPolicy
{
    /// <summary>Nothing to insert - and nothing to report either.</summary>
    public static bool IsNoOp(string text) => text.Length == 0;

    /// <summary>
    /// The gate before the clipboard is touched.
    /// </summary>
    public static InsertionRefusal? BeforeClipboard(
        TargetApplication? target,
        bool targetIsAlive,
        InsertionEnvironment environment)
    {
        if (target is null)
        {
            return InsertionRefusal.TargetUnavailable;
        }

        // Protected input is not a failure, but a normal situation - and the clipboard must not
        // be touched for it.
        if (environment.TargetIsProtected)
        {
            return InsertionRefusal.ProtectedTarget;
        }

        // The application could have been closed while recognition was running. Inserting
        // "somewhere" is not an option: there is an arbitrary window in front now, up to
        // somebody else's password field, and what was dictated would go there.
        if (!targetIsAlive)
        {
            return InsertionRefusal.TargetUnavailable;
        }

        return Recheck(target, environment);
    }

    /// <summary>
    /// The last look, after the clipboard already holds the dictation and before the paste is
    /// dispatched.
    /// </summary>
    public static InsertionRefusal? Recheck(TargetApplication target, InsertionEnvironment environment)
    {
        if (environment.FrontmostProcessId != target.ProcessIdentifier)
        {
            return InsertionRefusal.TargetChanged;
        }

        if (environment.TargetIsProtected)
        {
            return InsertionRefusal.ProtectedTarget;
        }

        return null;
    }

    /// <summary>Turn a refusal into the error the dictation core understands.</summary>
    public static TextInsertionError ToError(InsertionRefusal refusal) => refusal switch
    {
        InsertionRefusal.TargetUnavailable => new TextInsertionError.TargetUnavailable(),
        InsertionRefusal.TargetChanged => new TextInsertionError.TargetChanged(),
        InsertionRefusal.ProtectedTarget => new TextInsertionError.SecureInputActive(),
        InsertionRefusal.ModifiersStillHeld => new TextInsertionError.ModifiersStillHeld(),
        _ => throw new ArgumentOutOfRangeException(nameof(refusal)),
    };
}

/// <summary>How the text gets into the other application's window.</summary>
public enum InsertionPath
{
    /// <summary>
    /// Synthesised characters. The clipboard is never touched, so there is nothing to leak into
    /// history and nothing to give back.
    /// </summary>
    SynthesizedKeystrokes,

    /// <summary>
    /// The clipboard, borrowed for a second, plus Ctrl+V. The only way to move a lot of text at
    /// once, and the only one that works in applications which ignore synthesised characters.
    /// </summary>
    ClipboardPaste,
}

/// <summary>
/// Which of the two ways to use.
///
/// The macOS build has no such choice: <c>CGEvent</c> cannot type arbitrary Unicode into an
/// arbitrary application, so everything goes through the clipboard. Windows can - a
/// <c>KEYEVENTF_UNICODE</c> event carries a UTF-16 unit directly - and the direct path is
/// strictly better where it works: the person's clipboard is never borrowed, so it can never be
/// lost, and the dictation never exists anywhere a clipboard manager could see it.
///
/// The clipboard remains for long text. Synthesised characters arrive one input event each, at
/// the destination's mercy: a few hundred of them interleave with real typing, take visible time
/// to land, and a partial injection would tear a sentence in half. Pasting a paragraph is one
/// event.
/// </summary>
public static class InsertionPathPolicy
{
    /// <summary>
    /// Above this many UTF-16 units the clipboard wins.
    ///
    /// Two ordinary spoken sentences. Chosen so that the common dictation - a chat reply, a
    /// commit message, a search query - never touches the clipboard at all.
    /// </summary>
    public const int SynthesizedKeystrokeLimit = 256;

    public static InsertionPath Choose(int characterCount) =>
        characterCount <= SynthesizedKeystrokeLimit
            ? InsertionPath.SynthesizedKeystrokes
            : InsertionPath.ClipboardPaste;

    /// <summary>
    /// The path for this text going to this app.
    ///
    /// A typed line break is Return, and Return in a chat or a form sends it - half a message
    /// gone because a pause became a paragraph. So text with a line break is pasted, which is how
    /// the macOS build puts every line break in, except in a terminal, where a line break is what
    /// Return means and Ctrl+V is not a paste.
    /// </summary>
    public static InsertionPath Choose(string text, TargetApplication? target) =>
        text.Contains('\n', StringComparison.Ordinal)
            && PromptModePolicy.Category(target) != ApplicationCategory.Terminal
            ? InsertionPath.ClipboardPaste
            : Choose(text.Length);
}

/// <summary>
/// Modifiers whose release is waited for before Ctrl+V.
///
/// Otherwise Ctrl+V mixes with the held modifier and turns into somebody else's combination -
/// Shift+Ctrl+V, say, which an application understands completely differently.
///
/// Every hotkey candidate that raises a chord modifier has to be in this mask, or the wait
/// would end while the key is still held. On macOS this was the Fn trap; here it is Right Ctrl
/// and Right Alt, and there is a test that says so.
/// </summary>
public static class ModifierReleasePolicy
{
    public const HotkeyModifiers Watched =
        HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Windows;

    /// <summary>How long to wait for release, and in what steps.</summary>
    public const int PollLimit = 50;

    public static TimeSpan PollInterval { get; } = TimeSpan.FromMilliseconds(10);

    /// <summary>Whether Ctrl+V can be sent now without picking up somebody else's modifier.</summary>
    public static bool IsClear(HotkeyModifiers held) => (held & Watched) == HotkeyModifiers.None;
}
