namespace DictationCore;

/// <summary>
/// State of the dictation session.
///
/// The order is strict: <c>idle → preparing → listening → transcribing → inserting → idle</c>.
/// Cancellation is possible from any state before <c>inserting</c> - starting from it the text
/// already goes into someone else's application, and there is nothing to rewind.
/// </summary>
public enum DictationState
{
    /// <summary>Nothing happens, the microphone is turned off, the recording indicator is off.</summary>
    Idle,

    /// <summary>The key is pressed, the sound engine is coming up. Takes tens of milliseconds.</summary>
    Preparing,

    /// <summary>Recording in progress.</summary>
    Listening,

    /// <summary>The key is released, we recognize what was recorded.</summary>
    Transcribing,

    /// <summary>Insert the finished text into the active application.</summary>
    Inserting,
}

/// <summary>
/// The Swift computed properties of <see cref="DictationState"/>, kept as
/// extension members so the call sites read exactly as they did.
/// </summary>
public static class DictationStateExtensions
{
    extension(DictationState state)
    {
        /// <summary>Is the session busy? You cannot start a new one at this time.</summary>
        public bool IsBusy => state != DictationState.Idle;

        /// <summary>
        /// Is the microphone listening right now.
        ///
        /// The integrity of the "light goes off when we are not listening" promise depends on this:
        /// the sound engine must be turned off in all other states.
        /// </summary>
        public bool IsCapturing => state == DictationState.Listening;
    }
}

/// <summary>
/// What to do with a key release that came before the session had time to start.
///
/// The user presses and releases faster than the sound engine comes up -
/// this is the norm for a short phrase. A release cannot be lost, otherwise dictation
/// "sticks" in recording mode.
/// </summary>
public enum DeferredStopDecision
{
    /// <summary>Normal stop: recording is already in progress.</summary>
    StopNow,

    /// <summary>Remember and stop as soon as recording starts.</summary>
    DeferUntilListening,

    /// <summary>Ignore: in hands-free mode, releasing the key does nothing.</summary>
    Ignore,

    /// <summary>There is no session - there is nothing to react to.</summary>
    NoSession,
}

public static class DictationStopPolicy
{
    /// <summary>Decide what to do with a release of the hotkey.</summary>
    public static DeferredStopDecision DecideStop(DictationState state, bool isHandsFree)
    {
        // In hands-free mode, recording stops with a second press,
        // and not with a release - otherwise it would break off immediately after the start.
        if (isHandsFree)
        {
            return DeferredStopDecision.Ignore;
        }

        return state switch
        {
            DictationState.Idle => DeferredStopDecision.NoSession,
            // The main case for which all this exists: the key was pressed and
            // released while the engine is coming up.
            DictationState.Preparing => DeferredStopDecision.DeferUntilListening,
            DictationState.Listening => DeferredStopDecision.StopNow,
            // Finalization is already underway - it cannot be run a second time.
            DictationState.Transcribing or DictationState.Inserting => DeferredStopDecision.Ignore,
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
    }

    /// <summary>Is it possible to start a new session.</summary>
    public static bool CanStart(DictationState state, bool isEnabled, bool isModelReady) =>
        isEnabled && isModelReady && state == DictationState.Idle;

    /// <summary>
    /// Is it still possible to cancel what is happening.
    ///
    /// Once the insertion has started, there is nothing to cancel: the keyboard event has already
    /// been sent to another application and does not respond.
    /// </summary>
    public static bool CanCancel(DictationState state) =>
        state switch
        {
            DictationState.Idle or DictationState.Inserting => false,
            DictationState.Preparing or DictationState.Listening or DictationState.Transcribing => true,
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
}

/// <summary>
/// Whether to continue bringing the session to insertion.
///
/// Checked after each wait in the completion chain: the user could
/// press cancel while recognition was in progress.
/// </summary>
public static class DictationFinalizationPolicy
{
    public static bool ShouldContinue(
        DictationState state,
        bool cancellationRequested,
        bool taskCancelled)
    {
        if (cancellationRequested || taskCancelled)
        {
            return false;
        }

        return state switch
        {
            DictationState.Transcribing or DictationState.Inserting => true,
            DictationState.Idle or DictationState.Preparing or DictationState.Listening => false,
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
    }
}

/// <summary>Limit the duration of one dictation.</summary>
public static class DictationDurationPolicy
{
    /// <summary>
    /// Below this there is nothing to recognize.
    ///
    /// The engine refuses to work with recordings shorter than 300 ms, but it is not only
    /// about the engine: a person who pressed and immediately released a key simply changed their mind.
    /// Showing them a recognition error means frightening them out of the blue.
    /// </summary>
    public static readonly TimeSpan Minimum = TimeSpan.FromSeconds(0.35);

    /// <summary>Is there anything to recognize.</summary>
    public static bool IsWorthTranscribing(TimeSpan duration) => duration >= Minimum;

    /// <summary>What to do with a recording that is too short to recognize.</summary>
    public enum ShortRecordingOutcome
    {
        /// <summary>Keep silent: the key was touched, the person changed their mind.</summary>
        DropSilently,

        /// <summary>Tell: the key was held down, and the microphone did not record anything.</summary>
        ReportSilentInput,
    }

    /// <summary>
    /// How long a hold must last before an empty recording stops being a change
    /// of mind and becomes a broken microphone.
    ///
    /// The recording starts counting after the engine has come up, so only the wait
    /// for the first frame - tens of milliseconds - falls into the gap between the
    /// hold and the recorded audio. One and a half seconds is four times the
    /// <see cref="Minimum"/>: no latency can eat that much, only an input that gives
    /// nothing - muted, dead or taken by another application.
    /// </summary>
    public static readonly TimeSpan MinimumHoldForSilentInput = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// The recording did not reach the <see cref="Minimum"/>. Was that the person or the device?
    ///
    /// Only the hold decides. Duration is not asked about here: a recording that
    /// did not reach the minimum after a long hold is a faulty input, whether
    /// there are zero frames in it or a third of a second.
    /// </summary>
    public static ShortRecordingOutcome OutcomeForShortRecording(TimeSpan held) =>
        held >= MinimumHoldForSilentInput
            ? ShortRecordingOutcome.ReportSilentInput
            : ShortRecordingOutcome.DropSilently;

    // A one-hour session cap lived here once, "for safety". It was the
    // product deciding for the person when their dictation is long enough -
    // removed, along with the repeating timer that policed it. WAV streams
    // to disk; the transcription deadline scales with the recording; there
    // is no engineering reason left to interrupt anyone mid-sentence.
}

/// <summary>
/// Whether a take that the model found no words in heard anything at all.
/// </summary>
/// <remarks>
/// Asked only after recognition came back empty, so quiet speech never gets here: upstream's
/// model wrote a spoken sentence out at peaks of 0.015, 0.01 and 0.005, while zeros, noise and a
/// lone click came back empty. A take that came back empty and never reached the floor heard
/// silence - a muted, dead or wrong microphone, or a person who said nothing - and keeping it
/// would fill History with rows of nothing.
/// </remarks>
public static class SilentTakePolicy
{
    /// <summary>The same floor the segmenter uses; reaching it counts as sound.</summary>
    public const float Floor = SpeechSegmenter.Parameters.DefaultSpeechPeak;

    /// <summary><c>null</c> or empty samples cannot prove silence, so the take is kept.</summary>
    public static bool HeardOnlySilence(float[]? samples)
    {
        if (samples is null || samples.Length == 0) { return false; }

        foreach (float sample in samples)
        {
            if (Math.Abs(sample) >= Floor) { return false; }
        }

        return true;
    }
}
