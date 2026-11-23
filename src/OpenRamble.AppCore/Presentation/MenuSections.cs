using DictationCore;

namespace OpenRamble.AppCore;

/// <summary>
/// A row the menu can offer. <see cref="SetupHints"/> expands to several controls in the view;
/// as policy it is one unit that appears and disappears whole.
/// </summary>
public enum MenuRow
{
    StatusLine,
    StopAndInsert,
    CancelDictation,
    SetupHints,
    FinishSetup,
    InsertLastDictation,
    /// <summary>
    /// The newest takes of this session, each as its own words; a click pastes it into the app
    /// in front. Expands to up to <see cref="MenuSections.TakeLimit"/> rows, the last take first.
    /// </summary>
    Takes,

    /// <summary>
    /// The same takes in a submenu, where a click copies. Upstream reads a held Option key off
    /// the click; a tray menu here is drawn by the panel and never sees a modifier.
    /// </summary>
    CopyTakes,

    /// <summary>A readout: "Recording — 12:34", or what the recorder is busy doing.</summary>
    RecordingLine,

    /// <summary>Record both sides: the microphone and what the computer plays, where it can.</summary>
    StartRecording,

    /// <summary>Record the microphone only. Offered only where "Record" also takes system audio.</summary>
    StartVoiceNote,
    PauseRecording,
    ResumeRecording,
    StopRecording,
    Settings,
    Quit,
}

/// <summary>
/// Which rows the menu offers in each state - as data, so the whole matrix is testable without
/// rendering a menu.
///
/// The shape of the answer is sections: the view joins them with separators. Design rules the
/// matrix encodes:
/// <list type="bullet">
/// <item><description>While a session runs, the menu is about the session. Nothing else can be
/// acted on anyway, and a short menu is read faster mid-dictation.</description></item>
/// <item><description>Transcribing is seconds and cannot be meaningfully cancelled from a menu
/// that takes longer to open; Escape still works.</description></item>
/// <item><description>At most ONE recovery row ever appears: "Insert Last Dictation". Failed
/// transcription recordings heal quietly (kept on disk briefly, pruned by age) instead of
/// demanding menu decisions - the strange "Transcribe/Delete Saved Recording" pair is gone by
/// design; do not re-add it.</description></item>
/// <item><description>The newest takes lead the idle menu, the last one first, because the
/// menu is the backstop for words that could not land. Three at most: a menu is not a
/// history.</description></item>
/// <item><description>A recording is orthogonal to a dictation - both can be true - so its
/// section comes first and stays whatever the dictation does; the tray is the one place that is
/// always reachable to stop it. A running recording's line replaces the idle status line.
/// </description></item>
/// </list>
/// </summary>
public static class MenuSections
{
    /// <summary>How many takes the menu offers.</summary>
    public const int TakeLimit = 3;

    public static IReadOnlyList<IReadOnlyList<MenuRow>> Sections(
        DictationState state,
        bool isDictationReady,
        bool hasRecoveredText,
        bool hasTakes,
        MeetingRecordingState recordingState = MeetingRecordingState.Idle,
        bool canRecord = false,
        bool supportsSystemAudio = false)
    {
        var sections = new List<IReadOnlyList<MenuRow>>();
        if (state != DictationState.Idle || recordingState == MeetingRecordingState.Idle || hasRecoveredText)
        {
            sections.Add([MenuRow.StatusLine]);
        }

        switch (recordingState)
        {
            case MeetingRecordingState.Idle:
                break;
            case MeetingRecordingState.Starting:
            case MeetingRecordingState.Stopping:
                sections.Add([MenuRow.RecordingLine]);
                break;
            case MeetingRecordingState.Recording:
                sections.Add([MenuRow.RecordingLine, MenuRow.PauseRecording, MenuRow.StopRecording]);
                break;
            case MeetingRecordingState.Paused:
                sections.Add([MenuRow.RecordingLine, MenuRow.ResumeRecording, MenuRow.StopRecording]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(recordingState));
        }

        switch (state)
        {
            case DictationState.Preparing:
            case DictationState.Listening:
                sections.Add([MenuRow.StopAndInsert, MenuRow.CancelDictation]);
                break;

            case DictationState.Transcribing:
            case DictationState.Inserting:
                break;

            case DictationState.Idle:
                if (!isDictationReady)
                {
                    sections.Add([MenuRow.SetupHints, MenuRow.FinishSetup]);
                }

                if (hasRecoveredText)
                {
                    sections.Add([MenuRow.InsertLastDictation]);
                }

                if (hasTakes)
                {
                    sections.Add([MenuRow.Takes, MenuRow.CopyTakes]);
                }

                if (recordingState == MeetingRecordingState.Idle && canRecord)
                {
                    sections.Add(supportsSystemAudio
                        ? [MenuRow.StartRecording, MenuRow.StartVoiceNote]
                        : [MenuRow.StartRecording]);
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }

        sections.Add([MenuRow.Settings, MenuRow.Quit]);
        return sections;
    }

    /// <summary>
    /// The words on a recording row. The start and stop rows carry the shortcut that does the
    /// same - written into the title, as no tray menu here can show a global chord.
    /// </summary>
    /// <param name="isDegraded">The other side is not arriving (<see cref="CaptureHealth"/>).</param>
    /// <param name="microphoneMissing">The microphone is not arriving.</param>
    public static string RecordingTitle(
        MenuRow row,
        MeetingRecordingState recordingState,
        TimeSpan duration,
        string? shortcut = null,
        bool isDegraded = false,
        bool microphoneMissing = false) => row switch
    {
        MenuRow.RecordingLine => recordingState switch
        {
            MeetingRecordingState.Starting => "Starting recording…",
            MeetingRecordingState.Stopping => "Saving recording…",
            _ => RecordingLine(recordingState == MeetingRecordingState.Paused, duration, isDegraded, microphoneMissing),
        },
        MenuRow.StartRecording => WithShortcut("Record", shortcut),
        MenuRow.StartVoiceNote => "Record Voice Note",
        MenuRow.PauseRecording => "Pause Recording",
        MenuRow.ResumeRecording => "Resume Recording",
        MenuRow.StopRecording => WithShortcut("Stop Recording", shortcut),
        _ => throw new ArgumentOutOfRangeException(nameof(row)),
    };

    /// <summary>Upstream <c>MenuBarStatus.recordingLine</c>.</summary>
    private static string RecordingLine(bool isPaused, TimeSpan duration, bool isDegraded, bool microphoneMissing)
    {
        string line = $"{(isPaused ? "Paused" : "Recording")} — {RecordingTime.Clock(duration)}";
        if (isPaused) { return line; }
        if (microphoneMissing && isDegraded) { return line + " — nothing arriving"; }
        if (microphoneMissing) { return line + " — not your microphone"; }
        if (isDegraded) { return line + " — only your microphone"; }
        return line;
    }

    private static string WithShortcut(string title, string? shortcut) =>
        shortcut is null ? title : $"{title}   {shortcut}";
}
