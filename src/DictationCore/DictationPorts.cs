namespace DictationCore;

// The boundaries between pure dictation logic and the system.
//
// Anything that requires the desktop shell, a microphone or other people's applications lives
// behind these interfaces. Thanks to them, the controller is tested with a plain unit-test run
// in milliseconds and does not touch either the sound engine or other people's windows.

// MARK: - Audio capture

/// <summary>Record from a microphone to a file.</summary>
public interface IAudioCapturing
{
    /// <summary>
    /// Start recording. Returns the path of the file it goes to.
    ///
    /// The implementation must bring the engine up synchronously: any delay here
    /// cuts off the first word.
    /// </summary>
    Task<string> StartRecording();

    /// <summary>
    /// Wait for the first actually recorded frame of sound.
    ///
    /// <see cref="StartRecording"/> returns at engine startup, not at the first
    /// heard sound: between these moments about a tenth of a second passes
    /// (0.13-0.14 s on M4 Pro, <c>docs/benchmarks.md</c>). A person
    /// who begins to speak at the sound signal loses the first word in it -
    /// so the signal waits for this method.
    ///
    /// <c>true</c> - the frame has arrived. <c>false</c> - there is no recording any more: the device is
    /// silent and the session is already closed. Promising "speak" where nothing is being written
    /// is not possible, so there will be no signal for <c>false</c>.
    ///
    /// Test doubles give frames up immediately - they have nothing to wait for. The default
    /// implementation is needed both for that and so that adding a wait
    /// did not require touching every implementation at once.
    /// </summary>
    Task<bool> WaitForFirstFrame() => Task.FromResult(true);

    /// <summary>
    /// Stop recording and finish the file on disk.
    ///
    /// Returns the path of the finished file and the duration of what was recorded.
    /// </summary>
    Task<(string Url, TimeSpan Duration)> StopRecording();

    /// <summary>
    /// Move the just-finished 16 kHz mono Float32 recording out of capture memory.
    ///
    /// A capture that can provide this lets recognition begin from the exact PCM
    /// that was written instead of opening the finished Int16 WAV and converting
    /// the whole recording back to Float32. The WAV remains the durable recovery
    /// copy. Returning <c>null</c> keeps file-only captures and test doubles compatible.
    /// </summary>
    Task<float[]?> TakeBufferedSamples() => Task.FromResult<float[]?>(null);

    /// <summary>Abort recording and delete the file - the user cancelled the dictation.</summary>
    Task AbortRecording();

    /// <summary>
    /// How long the microphone was coming up before the first frame. <c>null</c> - not measured.
    ///
    /// <c>null</c>, not zero: an edge that cannot measure its warm-up has no
    /// right to look instant.
    /// </summary>
    Task<TimeSpan?> StartupLatency() => Task.FromResult<TimeSpan?>(null);
}

/// <remarks>
/// The Swift original is an <c>Error</c> enum with associated values; here the cases
/// are nested exception types so that <c>throw</c> stays honest.
/// </remarks>
public abstract class AudioCaptureError : Exception
{
    private protected AudioCaptureError(string message)
        : base(message)
    {
    }

    public sealed class MicrophonePermissionDenied : AudioCaptureError
    {
        public MicrophonePermissionDenied()
            : base("microphone permission denied")
        {
        }
    }

    public sealed class EngineUnavailable : AudioCaptureError
    {
        public EngineUnavailable(string reason)
            : base(reason)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }

    public sealed class UnsupportedAudioFormat : AudioCaptureError
    {
        public UnsupportedAudioFormat(string reason)
            : base(reason)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }

    public sealed class DiskFull : AudioCaptureError
    {
        public DiskFull()
            : base("disk full")
        {
        }
    }

    public sealed class WriteFailed : AudioCaptureError
    {
        public WriteFailed(string reason)
            : base(reason)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }

    public sealed class NotRecording : AudioCaptureError
    {
        public NotRecording()
            : base("not recording")
        {
        }
    }
}

// MARK: - Insert text

/// <summary>Where to insert and how.</summary>
public interface ITextInserting
{
    /// <summary>Insert text where the cursor was when dictation began.</summary>
    Task Insert(string text, TargetApplication? target);

    /// <summary>Press Return - if the user finished the phrase with the command "send".</summary>
    Task PressReturn();

    /// <summary>
    /// The application that was active at the time the hotkey was pressed.
    ///
    /// Taken at the beginning of the session, and not at the end: while recognition is in progress, focus
    /// could leave, and the text should go where it was dictated.
    /// </summary>
    TargetApplication? FrontmostApplication();

    /// <summary>
    /// Insert and tell when exactly the text went into someone else's window.
    ///
    /// A separate method is needed because insertion returns only after a
    /// second of clipboard protection: from the outside the moment of insertion cannot be caught,
    /// from the inside it is trivial.
    ///
    /// The default is a normal insert without marks. Test doubles do not measure
    /// anything and should not pretend to measure anything.
    /// </summary>
    async Task<InsertionMarks> InsertReportingMarks(string text, TargetApplication? target)
    {
        await Insert(text, target).ConfigureAwait(false);
        return new InsertionMarks();
    }
}

/// <summary>Recipient application.</summary>
public sealed record TargetApplication(
    string? BundleIdentifier,
    int ProcessIdentifier,
    string? LocalizedName);

/// <remarks>
/// The Swift original is an <c>Error</c> enum; here the cases are nested exception
/// types so that <c>throw</c> stays honest.
/// </remarks>
public abstract class TextInsertionError : Exception
{
    private protected TextInsertionError(string message)
        : base(message)
    {
    }

    public sealed class AccessibilityPermissionDenied : TextInsertionError
    {
        public AccessibilityPermissionDenied()
            : base("accessibility permission denied")
        {
        }
    }

    /// <summary>
    /// Protected input is active - a password field or a terminal with keyboard protection.
    /// You cannot insert there, and this is not a failure, but a normal situation.
    /// </summary>
    public sealed class SecureInputActive : TextInsertionError
    {
        public SecureInputActive()
            : base("secure input is active")
        {
        }
    }

    public sealed class ClipboardWriteFailed : TextInsertionError
    {
        public ClipboardWriteFailed()
            : base("clipboard write failed")
        {
        }
    }

    /// <summary>Before pasting, the clipboard could not be returned; no text was inserted.</summary>
    public sealed class ClipboardRestoreFailed : TextInsertionError
    {
        public ClipboardRestoreFailed()
            : base("clipboard restore failed")
        {
        }
    }

    /// <summary>Paste has already been sent, but the old clipboard could not be returned.</summary>
    public sealed class InsertedButClipboardRestoreFailed : TextInsertionError
    {
        public InsertedButClipboardRestoreFailed()
            : base("inserted, but the clipboard could not be restored")
        {
        }
    }

    /// <summary>The user did not release the modifiers, and the press would turn into someone else's combination.</summary>
    public sealed class ModifiersStillHeld : TextInsertionError
    {
        public ModifiersStillHeld()
            : base("modifiers are still held")
        {
        }
    }

    public sealed class TargetUnavailable : TextInsertionError
    {
        public TargetUnavailable()
            : base("target is unavailable")
        {
        }
    }

    /// <summary>The active application has changed since the dictation target was captured.</summary>
    public sealed class TargetChanged : TextInsertionError
    {
        public TargetChanged()
            : base("target has changed")
        {
        }
    }
}

// MARK: - Feedback

/// <summary>Dictation status indicator.</summary>
public interface IOverlayPresenting
{
    /// <summary>Show status. Called on every transition.</summary>
    Task Present(DictationState state, TimeSpan elapsed);

    /// <summary>Remove the indicator.</summary>
    Task Dismiss();

    /// <summary>
    /// Show a message that cannot be skipped - for example, that the text
    /// failed to insert and remained available in memory via Copy/Retry.
    /// </summary>
    Task PresentNotice(DictationNotice notice);
}

public enum DictationNoticeKind
{
    Info,
    Warning,
    Failure,
}

/// <summary>Message to the user.</summary>
public sealed record DictationNotice
{
    public DictationNotice(
        DictationNoticeKind kind,
        string message,
        string? recoverableText = null,
        string? recoveryAudio = null,
        bool? wordsDidNotLand = null)
    {
        Kind = kind;
        Message = message;
        RecoverableText = recoverableText;
        RecoveryAudio = recoveryAudio;
        // Failures lose the words by definition; anything else says so
        // explicitly at the site that knows.
        WordsDidNotLand = wordsDidNotLand ?? (kind == DictationNoticeKind.Failure);
    }

    public DictationNoticeKind Kind { get; }

    public string Message { get; }

    /// <summary>Recognized text that could not be inserted. Process memory only.</summary>
    public string? RecoverableText { get; }

    /// <summary>Path of the local WAV saved after a technical failure.</summary>
    public string? RecoveryAudio { get; }

    /// <summary>
    /// Whether the words failed to land where the person expected them.
    ///
    /// This - not the kind - decides the attention sound. "The text was
    /// inserted, but pressing Return failed" is a warning whose words DID
    /// land; "nothing was recognized" is mere info whose words did NOT.
    /// A person watching their editor needs the ear only for the second.
    /// </summary>
    public bool WordsDidNotLand { get; }
}

/// <summary>
/// The one sound the product has: "look at the panel".
///
/// Not a start chime and not a stop chime. A working dictation already shows
/// itself - the panel while recording, the text at the cursor when it lands -
/// and a sound on every session teaches the ear to ignore sounds. This one
/// plays only when the app has something to say: the words did not reach the
/// field, nothing was recognized, something failed. Silence means it worked.
/// </summary>
public interface ISounding
{
    Task PlayAttention();
}
