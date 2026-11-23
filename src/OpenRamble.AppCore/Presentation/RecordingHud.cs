namespace OpenRamble.AppCore;

/// <summary>What the recording pill says, and whether it is up at all.</summary>
/// <param name="Title">"Recording", "Paused", …; empty when hidden.</param>
/// <param name="Time">The elapsed clock, or empty when there is nothing on disk yet to time.</param>
/// <param name="IsDotLit">The red dot is solid while sound is being kept, dim otherwise.</param>
public readonly record struct RecordingHudContent(
    bool IsVisible,
    string Title,
    string Time,
    bool IsDotLit,
    string AccessibilityLabel)
{
    public static RecordingHudContent Hidden { get; } = new(false, "", "", false, "");
}

/// <summary>
/// The small pill that says a meeting is being recorded (upstream ScreenRecordingHUD.swift).
/// The tray badge alone is easy to miss; this is up for as long as anything is being captured.
/// </summary>
public static class RecordingHud
{
    public static RecordingHudContent Content(MeetingRecordingState state, TimeSpan elapsed)
    {
        string clock = RecordingTime.Clock(elapsed);
        string spoken = RecordingTime.Spoken(elapsed);
        return state switch
        {
            MeetingRecordingState.Starting =>
                new(true, "Starting recording…", "", false, "Starting recording"),
            MeetingRecordingState.Recording =>
                new(true, "Recording", clock, true, $"Recording, {spoken}"),
            MeetingRecordingState.Paused =>
                new(true, "Paused", clock, false, $"Recording paused at {spoken}"),
            MeetingRecordingState.Stopping =>
                new(true, "Saving recording…", clock, false, "Saving recording"),
            _ => RecordingHudContent.Hidden,
        };
    }

    /// <summary>
    /// The pill's corner: centred on the edge the dictation panel uses. A corner is easy to miss
    /// on a large screen; the centre is where the eye already goes for the dictation panel, which
    /// steps past the pill while both are up (see <see cref="OverlayPlacementPolicy.Origin"/>).
    /// </summary>
    public static OverlayPoint Origin(
        DictationOverlayPlacement placement,
        OverlayFrame workArea,
        OverlaySize pillSize) =>
        new(
            workArea.MidX - (pillSize.Width / 2),
            placement == DictationOverlayPlacement.Top
                ? workArea.MinY + OverlayPlacementPolicy.EdgeInset
                : workArea.MaxY - pillSize.Height - OverlayPlacementPolicy.EdgeInset);
}
