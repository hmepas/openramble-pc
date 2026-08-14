using DictationCore;

namespace OpenRamble.AppCore;

/// <summary>
/// Where the first-launch try-out has got to.
///
/// The step exists to answer one question - does the key reach OpenRamble - and the phases are
/// ordered by how much of the chain has been proved: nothing yet, the key arrives, the microphone
/// is live, the speech is being recognized, the words landed.
/// </summary>
public enum OnboardingTryPhase
{
    /// <summary>Nothing has happened yet. The step is waiting for the key.</summary>
    Waiting,

    /// <summary>
    /// Key tracking could not be installed at all, so no key will ever arrive. This is the one
    /// thing the step cannot find out by waiting, and the one thing that makes waiting pointless.
    /// </summary>
    TrackingUnavailable,

    /// <summary>The key is down and OpenRamble saw it. Dictation has not started recording yet.</summary>
    KeyDown,

    /// <summary>
    /// The on-screen button is held instead of the key. Proves the microphone, the recognition
    /// and the insertion - and deliberately proves nothing about the key.
    /// </summary>
    ButtonHeld,

    /// <summary>The key is down and the microphone is live.</summary>
    Recording,

    /// <summary>The key is back up and the words are being recognized.</summary>
    Working,

    /// <summary>
    /// The key was pressed and released and OpenRamble saw both, but no text arrived. The key
    /// works; something after it did not.
    /// </summary>
    KeyReached,

    /// <summary>Text was recognized and inserted. The whole chain has been proved.</summary>
    Succeeded,
}

/// <summary>
/// What the try-out step shows, live, while a person holds the key.
///
/// A separate type and not a branch inside the window, for the same reason as
/// <see cref="OnboardingGate"/>: this is where a new person finds out whether the product works
/// at all, and a step that shows nothing until a full dictation succeeds cannot tell
/// "the key never arrived" from "the key arrived and the recognition came back empty". Those
/// need different actions from the person and different words on screen, so the table is checked
/// without a screen.
/// </summary>
/// <remarks>
/// There is no macOS counterpart: the original's try-out shows a text field and waits for a
/// successful dictation. It is added here because the Windows key path has failure modes macOS
/// does not have - a low-level hook the system can refuse to install, and virtual-machine clients
/// that keep Right Ctrl for themselves - and all of them look identical from the outside.
/// </remarks>
public sealed record OnboardingTryOut
{
    public required OnboardingTryPhase Phase { get; init; }

    /// <summary>The standing instruction. It does not change while the person works through it.</summary>
    public required string Instruction { get; init; }

    /// <summary>The live line: what OpenRamble has seen so far.</summary>
    public required string Status { get; init; }

    /// <summary>Colour for the key badge and the status line; <c>null</c> - neutral, nothing seen yet.</summary>
    public required StatusColorRole? Tone { get; init; }

    /// <summary>Whether the microphone level belongs on screen: only while it is actually running.</summary>
    public required bool ShowsInputLevel { get; init; }

    /// <param name="isHotkeyHeld">Is the configured key physically down right now.</param>
    /// <param name="hotkeyEverPressed">Has it been down at least once since the step opened.</param>
    /// <param name="dictationState">What the dictation kernel is doing.</param>
    /// <param name="dictationSucceeded">Has a dictation been recognized and inserted since the step opened.</param>
    /// <param name="isButtonHeld">
    /// Is the on-screen "Hold to dictate" button being held. Kept apart from
    /// <paramref name="isHotkeyHeld"/> on purpose: the badge is the step's answer to "does the
    /// key reach OpenRamble", and lighting it from a mouse press would answer a question nobody
    /// asked with a fact that is not true.
    /// </param>
    /// <param name="trackingFailure">
    /// Why key tracking is not running, if it is not. Nothing else on the page can say it: every
    /// other symptom of a hook that never installed is identical to a person not pressing a key.
    /// </param>
    public static OnboardingTryOut Make(
        DictationHotkey hotkey,
        bool isHotkeyHeld,
        bool hotkeyEverPressed,
        DictationState dictationState,
        bool dictationSucceeded,
        bool isButtonHeld = false,
        string? trackingFailure = null)
    {
        string key = hotkey.Title;
        string instruction = $"Hold {key}, say a sentence, then let go. "
            + "You can also hold the button below with the mouse — it drives the same dictation.";

        // Success outranks everything: the words are already in the box, and a key that is down
        // again is the person trying a second time, not a reason to take the result away.
        if (dictationSucceeded)
        {
            return Make(
                OnboardingTryPhase.Succeeded,
                instruction,
                $"{key} works, and the words landed in the box. That is the whole product. You are set up.",
                StatusColorRole.Success);
        }

        // The button first, because while it is held it is the thing that is happening, and
        // because its copy must never claim the key did anything.
        if (isButtonHeld)
        {
            return dictationState switch
            {
                DictationState.Listening => Make(
                    OnboardingTryPhase.ButtonHeld,
                    instruction,
                    "Holding the button — recording. Say a sentence, then let go of the mouse.",
                    StatusColorRole.Recording,
                    showsInputLevel: true),

                _ => Make(
                    OnboardingTryPhase.ButtonHeld,
                    instruction,
                    "Holding the button — turning on the microphone…",
                    StatusColorRole.Processing),
            };
        }

        if (isHotkeyHeld)
        {
            return dictationState switch
            {
                DictationState.Listening => Make(
                    OnboardingTryPhase.Recording,
                    instruction,
                    $"{key} is down — recording. Say a sentence, then let go.",
                    StatusColorRole.Recording,
                    showsInputLevel: true),

                DictationState.Preparing => Make(
                    OnboardingTryPhase.KeyDown,
                    instruction,
                    $"{key} is down — turning on the microphone…",
                    StatusColorRole.Processing),

                // The key arrived but nothing started behind it. Saying so is the whole point of
                // the step: the person can stop wondering whether the keyboard is the problem.
                _ => Make(
                    OnboardingTryPhase.KeyDown,
                    instruction,
                    $"{key} is down — OpenRamble sees the key.",
                    StatusColorRole.Processing),
            };
        }

        if (dictationState != DictationState.Idle)
        {
            return Make(
                OnboardingTryPhase.Working,
                instruction,
                "Recognizing what you said…",
                StatusColorRole.Processing);
        }

        // Before every "nothing has arrived yet", because there is a difference between a key that
        // has not been pressed and a key that cannot be seen, and only one of them is worth
        // waiting for. Below the live states, because whatever is happening right now outranks a
        // reason recorded at launch. The button is the way through in the meantime.
        if (trackingFailure is string failure)
        {
            return Make(
                OnboardingTryPhase.TrackingUnavailable,
                instruction,
                failure,
                StatusColorRole.Attention);
        }

        if (hotkeyEverPressed)
        {
            return Make(
                OnboardingTryPhase.KeyReached,
                instruction,
                $"{key} reaches OpenRamble. No text arrived this time — hold it again and speak a "
                + "full sentence.",
                StatusColorRole.Attention);
        }

        return Make(
            OnboardingTryPhase.Waiting,
            instruction,
            $"Press and hold {key}. Nothing has reached OpenRamble yet.",
            tone: null);
    }

    private static OnboardingTryOut Make(
        OnboardingTryPhase phase,
        string instruction,
        string status,
        StatusColorRole? tone,
        bool showsInputLevel = false) =>
        new()
        {
            Phase = phase,
            Instruction = instruction,
            Status = status,
            Tone = tone,
            ShowsInputLevel = showsInputLevel,
        };
}
