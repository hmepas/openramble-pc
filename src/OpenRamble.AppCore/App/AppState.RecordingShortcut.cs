using DictationCore;

namespace OpenRamble.AppCore;

/// <summary>
/// The global chord that starts and stops a recording from any app (upstream 0.27.4/0.41,
/// Control-Command-R). It never raises a window: the tray badge is the confirmation.
/// </summary>
public sealed partial class AppState
{
    private IShortcutMonitoring? recordingShortcutMonitor;
    private KeyCombination? defaultRecordingShortcut;
    private KeyCombination? recordingShortcut;

    /// <summary>The chord before anyone chooses one, and what the revert arrow gives back.</summary>
    public KeyCombination? DefaultRecordingShortcut => defaultRecordingShortcut;

    /// <summary>
    /// The shortcut that starts a recording (microphone and system audio) and stops the one
    /// running, or nothing. Clearable, like the paste-last-take chord, for the same reason.
    /// </summary>
    public KeyCombination? RecordingShortcut
    {
        get { lock (gate) { return recordingShortcut; } }
        set
        {
            if (!Set(ref recordingShortcut, value)) { return; }
            // Cleared is stored as empty, not null: null is "never chosen", which reads as the default.
            UpdateSettings(current => current with { RecordingShortcut = value?.RawValue ?? string.Empty });
            ApplyRecordingShortcut();
        }
    }

    /// <summary>Warning about the recording chord, if a key remapper rewrites it.</summary>
    public string? RecordingShortcutWarning =>
        RecordingShortcut is { } shortcut ? lastTakeShortcutRewrite(shortcut) : null;

    /// <summary>The chord as the menu and Settings print it, or <c>null</c> when there is none.</summary>
    public string? RecordingShortcutDisplay => RecordingShortcut?.DisplayString(shortcutKeymap);

    /// <summary>What a shortcut press has asked for and is not done yet. Under <c>gate</c>.</summary>
    private enum RecordingToggleIntent
    {
        None,
        Start,
        Stop,
    }

    private RecordingToggleIntent pendingToggle;

    /// <summary>The presses' starts and stops, run one after the other in press order.</summary>
    private Task toggleChain = Task.CompletedTask;

    /// <summary>
    /// The shortcut's press, decided against what the last press is already doing: idle starts a
    /// recording of both sides; a running, paused or still starting one stops (once started); a
    /// press while a stop is under way is dropped - it would start a recording nobody expects.
    /// </summary>
    /// <remarks>
    /// Off the caller's thread: the monitor calls on the keyboard reader, and starting a recording
    /// starts processes.
    /// </remarks>
    public void ToggleRecording()
    {
        lock (gate)
        {
            bool stopping = pendingToggle == RecordingToggleIntent.Stop || recordingState == MeetingRecordingState.Stopping;
            if (stopping) { return; }
            RecordingToggleIntent intent = pendingToggle == RecordingToggleIntent.None && recordingState == MeetingRecordingState.Idle
                ? RecordingToggleIntent.Start
                : RecordingToggleIntent.Stop;
            pendingToggle = intent;
            Task previous = toggleChain;
            toggleChain = Task.Run(() => RunToggle(intent, previous));
        }
    }

    private async Task RunToggle(RecordingToggleIntent intent, Task previous)
    {
        await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        try
        {
            await (intent == RecordingToggleIntent.Start
                ? StartRecording(includeSystemAudio: true)
                : StopRecording()).ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                if (pendingToggle == intent) { pendingToggle = RecordingToggleIntent.None; }
            }
        }
    }

    private void SetUpRecordingShortcut(AppEnvironment environment, AppSettings stored)
    {
        recordingShortcutMonitor = environment.RecordingShortcutMonitor;
        defaultRecordingShortcut = environment.DefaultRecordingShortcut;
        recordingShortcut = stored.RecordingShortcut is null
            ? defaultRecordingShortcut
            : KeyCombination.Parse(stored.RecordingShortcut);
        if (recordingShortcutMonitor is not null) { recordingShortcutMonitor.OnPress = ToggleRecording; }
    }

    /// <summary>Point the watcher at the chosen chord, or take it off the keyboard.</summary>
    private void ApplyRecordingShortcut()
    {
        if (recordingShortcutMonitor is null) { return; }
        KeyCombination? shortcut = RecordingShortcut;
        recordingShortcutMonitor.SetShortcut(shortcut);
        if (shortcut is null) { return; }
        if (!recordingShortcutMonitor.IsRunning) { recordingShortcutMonitor.Start(); }
    }
}
