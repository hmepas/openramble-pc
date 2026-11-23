using DictationCore;
using OpenRamble.AppCore.Audio;

namespace OpenRamble.AppCore;

/// <summary>The settings that arrived with upstream 0.12–0.22, and the small rules behind each.</summary>
public sealed partial class AppState
{
    private bool copiesToClipboard;
    private KeyCombination? copyShortcut;
    private readonly KeyCombination? defaultLastTakeShortcut;

    /// <summary>
    /// The chord a person has before choosing one - the platform's, from the composition root -
    /// and what the revert arrow gives back.
    /// </summary>
    public KeyCombination? DefaultLastTakeShortcut => defaultLastTakeShortcut;
    private bool appendsTrailingSpace;
    private AppAppearance appearance;
    private string? inputDeviceUid;
    private bool detailedLogging;
    private bool stopsOnSilence;
    private DictationOverlayPlacement overlayPlacement;

    /// <summary>Tracks the quiet. Reset per take, never shared between them.</summary>
    private SilencePolicy silence;

    /// <summary>
    /// Also leave the finished text on the clipboard.
    ///
    /// Off by default, and deliberately: dictated text on the clipboard is dictated text handed
    /// to every clipboard manager on the machine, and the product's promise is that what you say
    /// stays here. Someone who wants it can ask, and then it is written host-only, the same as
    /// every other copy this app makes.
    /// </summary>
    public bool CopiesToClipboard
    {
        get { lock (gate) { return copiesToClipboard; } }
        set
        {
            if (!Set(ref copiesToClipboard, value)) { return; }
            UpdateSettings(current => current with { CopiesToClipboard = value });
        }
    }

    /// <summary>
    /// The shortcut that pastes the last take into the app in front, or nothing.
    ///
    /// Clearable because a global shortcut taken out of someone's keyboard is one that can
    /// collide with something they already use.
    /// </summary>
    /// <remarks>
    /// Stored under the old copy shortcut's key: a chord someone chose for "the last dictation"
    /// still means the last dictation.
    /// </remarks>
    public KeyCombination? LastTakeShortcut
    {
        get { lock (gate) { return copyShortcut; } }
        set
        {
            if (!Set(ref copyShortcut, value)) { return; }
            // Cleared is stored as empty, not null: null is "never chosen", which reads as the default.
            UpdateSettings(current => current with { CopyShortcut = value?.RawValue ?? string.Empty });
            ApplyLastTakeShortcut();
        }
    }

    /// <summary>
    /// End every dictation with a space.
    ///
    /// Off by default: most dictations land in the middle of writing, where a stray space at the
    /// end is noise. On, it is for people who dictate in runs - without it the next phrase
    /// arrives welded to the last word.
    /// </summary>
    public bool AppendsTrailingSpace
    {
        get { lock (gate) { return appendsTrailingSpace; } }
        set
        {
            if (!Set(ref appendsTrailingSpace, value)) { return; }
            UpdateSettings(current => current with { AppendsTrailingSpace = value });
        }
    }

    /// <summary>
    /// Light, dark, or whatever the machine is doing. Applying it is the window's business; the
    /// choice is kept here so every window agrees.
    /// </summary>
    public AppAppearance Appearance
    {
        get { lock (gate) { return appearance; } }
        set
        {
            if (!Set(ref appearance, value)) { return; }
            UpdateSettings(current => current with { Appearance = value.RawValue });
        }
    }

    /// <summary>
    /// Which microphone to record through, by its stable UID.
    ///
    /// <c>null</c> means whatever the system calls default, which is what almost everyone wants
    /// and what the app did before this existed. The UID rather than an index, because indexes
    /// are handed out per session and reused - a stored one can name a different microphone
    /// tomorrow.
    /// </summary>
    public string? InputDeviceUid
    {
        get { lock (gate) { return inputDeviceUid; } }
        set
        {
            string? normalized = string.IsNullOrEmpty(value) ? null : value;
            if (!Set(ref inputDeviceUid, normalized)) { return; }
            UpdateSettings(current => current with { InputDeviceUid = normalized });
            (capture as IPreferredInputDevice)?.PreferredDeviceId = normalized;
            Raise(nameof(InputDeviceNotice));
        }
    }

    /// <summary>The microphones this machine can hear through, right now.</summary>
    public IReadOnlyList<AudioInputDevice> AvailableInputDevices => inputDevices?.Available() ?? [];

    /// <summary>
    /// Set when the chosen microphone is not on this machine.
    ///
    /// Said out loud rather than swapped silently: someone who chose a headset and quietly got
    /// the laptop lid would only find out from the transcript.
    /// </summary>
    public string? InputDeviceNotice => AudioInputDevicePolicy.Notice(InputDeviceUid, AvailableInputDevices);

    /// <summary>
    /// Keep the local diagnostic journal: technical events for up to seven days, at most 5 MB.
    ///
    /// On by default; turning it off also clears it. It turns on no new measurement: every stage
    /// of every dictation is timed and reported either way. Nothing said is ever written to it.
    /// </summary>
    public bool DetailedLogging
    {
        get { lock (gate) { return detailedLogging; } }
        set
        {
            if (!Set(ref detailedLogging, value)) { return; }
            UpdateSettings(current => current with { DetailedLogging = value });
            journal.IsEnabled = value;
        }
    }

    /// <summary>
    /// Save the error report the person asked for to <paramref name="destination"/>, then show
    /// the folder it went to. Nothing is sent anywhere. <c>false</c> - it could not be written,
    /// and the person has been told.
    /// </summary>
    public async Task<bool> SaveErrorReport(string destination)
    {
        try
        {
            await Task.Run(() => ErrorReport.Save(destination, journal)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.EmitException("report.save.failed", error);
            Notify(new DictationNotice(
                DictationNoticeKind.Failure,
                "Couldn't save the report. Try another location with available disk space."));
            return false;
        }

        if (Path.GetDirectoryName(Path.GetFullPath(destination)) is { } folder) { folders?.Open(folder); }
        return true;
    }

    /// <summary>
    /// The process is about to end on an exception nobody caught. Kept, sanitized, in the
    /// journal's folder for the next error report - while the journal is on.
    /// </summary>
    public void RecordCrash(Exception error) => journal.RecordCrash(error);

    /// <summary>
    /// Finish a hands-free dictation when the talking stops.
    ///
    /// Hands-free only, and that is the whole design. While the key is held the person is
    /// holding it - they are saying with their hand that they have not finished, and a pause for
    /// thought is not an ending. Cutting a sentence off under a held key is what makes voice
    /// input feel hostile.
    /// </summary>
    public bool StopsOnSilence
    {
        get { lock (gate) { return stopsOnSilence; } }
        set
        {
            if (!Set(ref stopsOnSilence, value)) { return; }
            UpdateSettings(current => current with { StopsOnSilence = value });
        }
    }

    /// <summary>Where the dictation panel sits on the screen being used.</summary>
    public DictationOverlayPlacement OverlayPlacement
    {
        get { lock (gate) { return overlayPlacement; } }
        set
        {
            if (!Set(ref overlayPlacement, value)) { return; }
            UpdateSettings(current => current with { OverlayPlacement = value.RawValue });
            (overlay as IOverlayPlacementConfiguring)?.Placement = value;
        }
    }

    /// <summary>
    /// Point the second watcher at the chosen shortcut, or take it off the keyboard.
    /// </summary>
    /// <remarks>
    /// The macOS original starts it only once the dictation hotkey runs, because before that the
    /// app has no Accessibility permission and starting would fail silently. Here the hook needs
    /// no permission and starts once at assembly, so the one rule left is the monitor's own: a
    /// monitor with nothing to look for is a monitor that only costs.
    /// </remarks>
    private void ApplyLastTakeShortcut()
    {
        if (lastTakeShortcutMonitor is null) { return; }
        KeyCombination? shortcut = LastTakeShortcut;
        lastTakeShortcutMonitor.SetShortcut(shortcut);
        if (shortcut is null) { return; }
        if (!lastTakeShortcutMonitor.IsRunning) { lastTakeShortcutMonitor.Start(); }
    }

    /// <summary>
    /// One frame's peak reached the app: feed the silence rule, then the meter.
    /// </summary>
    /// <remarks>
    /// Only hands-free, and only while actually listening. A held key means the person has not
    /// finished; a state that is not listening has no recording for silence to end. The meter is
    /// behind a gate that keeps at most one update in flight: the meter used to compete with the
    /// dictation it was drawn for, and the newest peak always wins, so nothing visible is lost.
    /// </remarks>
    private void RegisterInputLevel(float peak)
    {
        bool stop = false;
        lock (gate)
        {
            takePeak = Math.Max(takePeak, peak);
            if (stopsOnSilence && isHandsFreeActive && dictationState == DictationState.Listening)
            {
                stop = silence.Observe(peak, monotonicNow());
                if (stop) { silence.Reset(); }
            }
            else
            {
                silence.Reset();
            }
        }

        if (stop)
        {
            DiagnosticLog.Emit("dictation.silence", ("session", DiagnosticLog.Session));
            StopCurrentRecording();
        }

        if (!levelGate.Take()) { return; }
        try
        {
            OnInputLevel?.Invoke(peak);
        }
        finally
        {
            levelGate.Release();
        }
    }
}
