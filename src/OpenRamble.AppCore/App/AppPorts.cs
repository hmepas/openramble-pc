using DictationCore;

namespace OpenRamble.AppCore;

// The edges of the system the application is assembled from.
//
// Everything that cannot be touched in a test lives behind one of these: granted permissions,
// the keyboard of the whole machine, other people's windows, the clipboard, the shell, sleep and
// memory pressure. The application substitutes real implementations; a test substitutes fakes.

/// <summary>Reads the permission the running process actually has.</summary>
public interface IPermissionReading
{
    /// <remarks>
    /// Deliberately synchronous and safe to call off the UI thread: the poll reads it once a
    /// second, and a read that blocks the interface for a slow system answer is exactly the
    /// stall this port exists to keep away from the dictation panel.
    /// </remarks>
    bool MicrophoneGranted { get; }
}

/// <summary>
/// The global hotkey, as the application sees it.
///
/// One press, one release, one double tap - the gesture rules themselves are
/// <see cref="HotkeyGestureMachine"/>'s and are not repeated here.
/// </summary>
public interface IHotkeyMonitoring
{
    Action? OnPress { get; set; }

    Action? OnRelease { get; set; }

    Action? OnDoubleTap { get; set; }

    Action? OnSingleTapWhileHandsFree { get; set; }

    /// <summary>The hold turned out to be a shortcut over the hotkey, not a dictation.</summary>
    Action? OnAbortShortcut { get; set; }

    Action? OnEscape { get; set; }

    /// <summary>
    /// The configured key physically went down, before any gesture has been read out of it.
    /// </summary>
    /// <remarks>
    /// The callbacks above are the product; this pair is the proof that the key reaches the
    /// process at all. A press that starts no dictation - the engine is still loading, the
    /// microphone is off, a chord was in progress - is otherwise indistinguishable from a key
    /// that never arrived, and the first-launch try-out exists to tell those two apart. Raised
    /// once per physical press: auto-repeat is not a second press.
    /// </remarks>
    Action? OnHotkeyDown { get; set; }

    /// <summary>The configured key came back up. Pairs with <see cref="OnHotkeyDown"/>.</summary>
    Action? OnHotkeyUp { get; set; }

    /// <summary>
    /// Tracking could not be installed, with a sentence that says so.
    /// </summary>
    /// <remarks>
    /// Nothing else in the application can notice: every callback above simply stays quiet for
    /// the life of the process, which looks exactly like a person who is not pressing the key.
    /// This is the same shape of silent failure the tray registration already reports out loud.
    /// </remarks>
    Action<string>? OnTrackingFailure { get; set; }

    /// <summary>
    /// In hands-free mode a single press means "stop", not "start a new dictation". The monitor
    /// has to know, or the next press starts a second session over a live one.
    /// </summary>
    bool IsHandsFreeActive { get; set; }

    void SetHotkey(DictationHotkey hotkey);

    void Start();

    void Stop();
}

/// <summary>
/// Asking for the microphone and sending the person where they can grant it.
/// </summary>
/// <remarks>
/// macOS has an in-process consent prompt (<c>AVCaptureDevice.requestAccess</c>). A desktop
/// Windows app has none: the answer is discovered by trying to open the device, and the only
/// remedy is the Settings page. The shape stays the same so the flow - ask, then either come
/// back to our window or hand over to Settings - is one piece of logic on both platforms.
/// </remarks>
public interface IMicrophoneConsentFlow
{
    Task<bool> Request();

    void OpenSettings();

    /// <summary>
    /// Bring our own window back to the front. Opening Settings takes the focus, and onboarding
    /// has to continue where the person left it.
    /// </summary>
    void ActivateApplication();
}

/// <summary>
/// What happens to the computer outside of us.
///
/// Every one of these exists for the same reason: dictation must not stay on when there is
/// nothing left to listen to, and memory must not stay held when the machine needs it.
/// </summary>
public interface ISystemEventSource
{
    event Action? WillSleep;

    event Action? DidWake;

    event Action? AudioDeviceChanged;

    /// <summary>Our own window came to the front - a good moment to re-read the permission.</summary>
    event Action? ApplicationBecameActive;

    event Action<MemoryPressureTier>? MemoryPressureChanged;
}

/// <summary>
/// Start at login. An app without a window that does not come back after a reboot is
/// indistinguishable from a broken one: the key is silent and there is nobody to explain why.
/// </summary>
public interface ILaunchAtLoginService
{
    bool IsEnabled { get; }

    /// <summary>Throws if the change could not be made - a silent failure would be the bug.</summary>
    void SetEnabled(bool enabled);
}

/// <summary>
/// A clipboard write that stays on this machine.
///
/// See <see cref="ClipboardRestrictions"/>: a plain write hands the dictation to Clipboard
/// History and Cloud Clipboard, which outlive both the dictation and the application.
/// </summary>
public interface IHostOnlyClipboard
{
    void Copy(string text);
}

/// <summary>Show a folder to the person. The only shell integration the app has.</summary>
public interface IFolderOpening
{
    void Open(string path);
}

/// <summary>
/// Builds a capture for one dictation.
/// </summary>
/// <param name="takesDirectory">Where the WAV goes.</param>
/// <param name="onFailure">The audio stream died mid-sentence; the reason must be shown exactly.</param>
/// <param name="onSamples">Live samples for the recording waveform.</param>
public delegate IAudioCapturing CaptureFactory(
    string takesDirectory,
    Action<AudioCaptureError> onFailure,
    Action<float[]> onSamples);

/// <summary>
/// Builds the recognition call for one engine directory.
/// </summary>
/// <param name="languageHint">
/// Read at every call, not captured once: the person may change the language between dictations,
/// and fixing it at assembly time would mean ignoring the choice.
/// </param>
public delegate Func<string, CancellationToken, Task<ASRResult>> TranscribeFactory(
    string engineDirectory,
    Func<string?> languageHint);
