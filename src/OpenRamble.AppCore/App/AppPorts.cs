using DictationCore;
using OpenRamble.AppCore.Audio;

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
/// <para>
/// macOS has an in-process consent prompt (<c>AVCaptureDevice.requestAccess</c>). A desktop
/// Windows app has none: the answer is discovered by trying to open the device, and the only
/// remedy is the Settings page. The shape stays the same so the flow - ask, then either come
/// back to our window or hand over to Settings - is one piece of logic on every platform.
/// </para>
/// <para>
/// <b>Linux has no consent store at all.</b> There is nothing to ask and nothing that remembers
/// an answer: whether the microphone can be opened is decided by the PipeWire/PulseAudio session
/// and by the <c>audio</c> group, and the only way to learn it is to open a device and see.
/// <see cref="Request"/> there is a probe - open, read a moment of silence, close - and the
/// honest result of a failed probe is a sentence naming the sound server, not a permission page.
/// <see cref="OpenSettings"/> has no single page to open either; the same is already true of the
/// hotkey advice, see <see cref="AppEnvironment.HotkeySettingsHint"/>. Documented here and not
/// implemented here: this port is the shape, and the shape does not change.
/// </para>
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
/// What "stays" costs is the platform's business and is written down per platform: on Windows
/// three registered formats, see <see cref="WindowsClipboardRestrictions"/>, without which the
/// dictation goes to Clipboard History and Cloud Clipboard; on Linux there is no such marker to
/// set, and the promise shrinks to what <see cref="LinuxClipboardRestrictions"/> can actually
/// keep. The port is the same either way - one string in, nothing kept.
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
/// <param name="onFailure">
/// The audio stream of the given session died mid-sentence; the reason must be shown exactly.
/// </param>
/// <param name="onSamples">Live samples for the recording waveform.</param>
/// <param name="preferredDeviceUid">
/// The microphone the person chose, or <c>null</c> for the system default. A later change
/// reaches the capture through <see cref="IPreferredInputDevice"/> when it implements it.
/// </param>
public delegate IAudioCapturing CaptureFactory(
    string takesDirectory,
    Action<int, AudioCaptureError> onFailure,
    Action<float[]> onSamples,
    string? preferredDeviceUid);

/// <summary>
/// A capture that can be pointed at a different microphone after it was built.
/// </summary>
/// <remarks>
/// Optional on purpose: the setting is applied at construction through the factory, and a
/// capture that also implements this gets the change without a relaunch. The UID is the
/// platform's stable name - see <see cref="AudioInputDevice"/>; an unknown one means the default.
/// </remarks>
public interface IPreferredInputDevice
{
    string? PreferredDeviceId { get; set; }
}

/// <summary>
/// An overlay that can say the model is being woken rather than that words are being transcribed.
/// </summary>
/// <remarks>
/// Optional: the panel is the only feedback channel during dictation, and a wait for a model
/// that is still loading is not the same event as transcription. Presenters that cannot say so
/// simply keep their previous message.
/// </remarks>
public interface IEngineWaitPresenting
{
    void SetWaitingForEngine(bool waiting);
}

/// <summary>
/// A panel that can hold words that could not land, with the one action that works now.
/// </summary>
/// <remarks>
/// Optional, and its absence is a decision rather than a gap: the application owns the waiting
/// words, and they outlive the take that produced them - but only where the person can see them.
/// Without a panel that shows them, the dictation key stays a dictation key and a failed paste is
/// a notice, as it was before (the Windows panel, for now).
/// </remarks>
public interface IWaitingWordsPresenting
{
    /// <summary>The panel's own action button: the same as a tap of the dictation key.</summary>
    Action? OnWaitingAction { get; set; }

    /// <summary>The panel's Later button: the words leave the panel and stay in History.</summary>
    Action? OnWaitingLater { get; set; }

    /// <summary>
    /// The words that wait, or <c>null</c> once they no longer do, and the dictation key the panel
    /// names - also told when nothing waits, so a key changed in Settings is the one shown later.
    /// </summary>
    void SetWaiting(WaitingWords? waiting, DictationHotkey key);

    /// <summary>Say how a waiting take ended, then leave.</summary>
    void ShowOutcome(TakeOutcome outcome);

    /// <summary>The dictation key went down over the waiting words, or came up.</summary>
    void SetKeyDown(bool down);
}

/// <summary>
/// An overlay that can be told where on the screen to sit.
/// </summary>
/// <remarks>
/// Optional: the placement is pure geometry (<see cref="OverlayPlacementPolicy"/>) applied by the
/// window that owns the panel, and a presenter with no screen - a test double - has nothing to
/// place.
/// </remarks>
public interface IOverlayPlacementConfiguring
{
    DictationOverlayPlacement Placement { set; }
}

/// <summary>
/// Builds the recognition call for one engine directory: give it the engine folder, get back the
/// call that recognizes one recording.
/// </summary>
public delegate Func<string, CancellationToken, Task<ASRResult>> TranscribeFactory(
    string engineDirectory);

/// <summary>
/// Watches the whole machine for one shortcut - the chord that pastes the last take.
/// </summary>
/// <remarks>
/// Its own monitor rather than another callback on <see cref="IHotkeyMonitoring"/>: that one
/// watches a bare modifier being held, this one watches a chord being pressed. Different
/// events, different machinery. Nothing is swallowed: a shortcut that happens to collide with
/// one in the front application still reaches it - eating other applications' keys from the
/// background is how a small utility becomes the thing that broke someone's editor.
/// </remarks>
public interface IShortcutMonitoring
{
    Action? OnPress { get; set; }

    bool IsRunning { get; }

    /// <summary><c>null</c> takes the shortcut off the keyboard entirely.</summary>
    void SetShortcut(KeyCombination? shortcut);

    void Start();

    void Stop();
}

/// <summary>The microphones this machine can hear through, right now.</summary>
public interface IAudioInputDeviceSource
{
    IReadOnlyList<AudioInputDevice> Available();
}
