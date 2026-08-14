using DictationCore;

namespace OpenRamble.AppCore;

/// <summary>
/// Which picture the panel puts next to the words.
/// </summary>
/// <remarks>
/// The macOS original names SF Symbols directly (<c>"mic.fill"</c>). Those names mean nothing
/// on Windows and the Segoe Fluent glyph that replaces each one is a rendering decision, so
/// the content model names the icon by role and the view resolves it. What the tests pin is
/// unchanged: every tone has an icon, and warning and failure share one.
/// </remarks>
public enum OverlayIcon
{
    Done,
    Microphone,
    Waveform,
    Info,
    Warning,
}

/// <summary>
/// What is written on the dictation panel.
///
/// The panel is the only feedback channel during dictation: the application has no window of
/// its own. Its contents are therefore assembled as a separate type and checked against a
/// state table, not by looking at the screen.
/// </summary>
public sealed record OverlayContent
{
    public required string Title { get; init; }

    public string? Subtitle { get; init; }

    public required OverlayTone Tone { get; init; }

    /// <summary>
    /// A shortcut to the entire panel for a screen reader.
    ///
    /// The panel does not take focus on purpose - otherwise the insertion would break - so a
    /// blind person never lands on it. The label is there for whoever hunts for it with the
    /// screen reader cursor.
    /// </summary>
    public required string AccessibilityLabel { get; init; }

    /// <summary>What to say out loud on entering this state. <c>null</c> - nothing to say.</summary>
    public string? Announcement { get; init; }

    /// <summary>An urgent announcement interrupts whatever the screen reader is reading.</summary>
    public required bool IsAnnouncementUrgent { get; init; }

    /// <param name="enginePreparation">
    /// What the recognition engine is doing right now; <c>null</c> when nobody manages its
    /// residency and it is simply always there.
    /// </param>
    public static OverlayContent Make(
        DictationState state,
        DictationNotice? notice,
        TimeSpan elapsed,
        EnginePreparationState? enginePreparation = null)
    {
        // The message is more important than the state: it is shown instead.
        if (notice is not null)
        {
            return new OverlayContent
            {
                Title = notice.Message,
                Subtitle = null,
                Tone = ToneFor(notice.Kind),
                AccessibilityLabel = notice.Message,
                Announcement = notice.Message,
                // The message comes exactly when something goes wrong. Waiting for the end of
                // someone else's sentence means saying it too late to be useful.
                IsAnnouncementUrgent = notice.Kind != DictationNoticeKind.Info,
            };
        }

        if (state == DictationState.Transcribing && IsLoading(enginePreparation))
        {
            // The engine was handed back to the system while the app sat idle, and the keypress
            // started a full reload. Until it lands there is nothing to recognize with, and a
            // panel saying "Transcribing…" over a model still being read off disk is a lie that
            // can last a minute and a half - the exact minute and a half in which a person
            // decides the product is broken and reaches for Task Manager.
            string title = enginePreparation!.Title;
            const string waiting = "Your words are recorded and waiting.";
            return new OverlayContent
            {
                Title = title,
                Subtitle = waiting,
                Tone = OverlayTone.Working,
                AccessibilityLabel = $"{title}. {waiting}",
                // Deliberately silent. The title carries a seconds counter, so announcing it
                // would interrupt the screen reader once per second for as long as the load
                // lasts; entering "Transcribing" has already been announced once.
                Announcement = null,
                IsAnnouncementUrgent = false,
            };
        }

        return state switch
        {
            DictationState.Idle => new OverlayContent
            {
                Title = "Done",
                Subtitle = null,
                Tone = OverlayTone.Idle,
                AccessibilityLabel = "Not dictating",
                // The panel is removed at this moment. Announcing "ready" into the void is not
                // needed: the person already heard that the dictation was over.
                Announcement = null,
                IsAnnouncementUrgent = false,
            },

            DictationState.Preparing => new OverlayContent
            {
                Title = "Turning on the microphone…",
                Subtitle = null,
                Tone = OverlayTone.Working,
                AccessibilityLabel = "Turning on the microphone",
                Announcement = "Turning on the microphone",
                IsAnnouncementUrgent = false,
            },

            DictationState.Listening => new OverlayContent
            {
                Title = "Listening",
                Subtitle = null,
                Tone = OverlayTone.Recording,
                AccessibilityLabel = $"Recording, {SpokenSeconds(elapsed)}.",
                // The main announcement in the whole application: without it a blind person
                // does not know that the microphone is on.
                Announcement = "Recording.",
                IsAnnouncementUrgent = true,
            },

            DictationState.Transcribing => new OverlayContent
            {
                Title = "Transcribing…",
                // The number is the length of the finished recording, not progress. Showing it
                // next to a spinner looks like a processing timer and misleads.
                Subtitle = null,
                Tone = OverlayTone.Working,
                AccessibilityLabel = "Recording stopped, transcribing speech",
                Announcement = "Recording stopped, transcribing speech",
                IsAnnouncementUrgent = false,
            },

            DictationState.Inserting => new OverlayContent
            {
                Title = "Done",
                Subtitle = null,
                Tone = OverlayTone.Idle,
                AccessibilityLabel = "Text inserted",
                Announcement = null,
                IsAnnouncementUrgent = false,
            },

            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
    }

    /// <summary>Is the engine being read into memory right now, rather than merely idle or ready?</summary>
    private static bool IsLoading(EnginePreparationState? preparation) =>
        preparation?.PhaseValue
            is EnginePreparationState.Phase.LoadingRecognizer
            or EnginePreparationState.Phase.LoadingVocabulary;

    private static OverlayTone ToneFor(DictationNoticeKind kind) => kind switch
    {
        DictationNoticeKind.Info => OverlayTone.Info,
        DictationNoticeKind.Warning => OverlayTone.Warning,
        DictationNoticeKind.Failure => OverlayTone.Failure,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// The same number in words.
    ///
    /// "5 s" is read by a screen reader as "5 es". For the only sign that the recording is
    /// really running, that is not enough.
    /// </summary>
    public static string SpokenSeconds(TimeSpan elapsed)
    {
        double seconds = elapsed.TotalSeconds;
        int value = (int)Math.Round(seconds < 0 ? 0 : seconds, MidpointRounding.AwayFromZero);
        return $"{value} {SecondsWord(value)}";
    }

    /// <summary>The form of the word "second" for a number.</summary>
    private static string SecondsWord(int value) => value == 1 ? "second" : "seconds";
}

public enum OverlayTone
{
    Idle,
    Recording,
    Working,
    Info,
    Warning,
    Failure,
}

public static class OverlayToneInfo
{
    extension(OverlayTone tone)
    {
        /// <summary>
        /// The status color the panel paints this tone with; <c>null</c> renders as neutral
        /// secondary. Failure joins <see cref="StatusColorRole.Attention"/> deliberately: every
        /// failure here is recoverable by the never-silently-discard rule, so it is "attention
        /// + work preserved", not a destructive red - red stays reserved for the live
        /// microphone. The warning/failure difference survives in copy and announcement
        /// urgency.
        /// </summary>
        public StatusColorRole? ColorRole => tone switch
        {
            OverlayTone.Idle => null,
            OverlayTone.Recording => StatusColorRole.Recording,
            OverlayTone.Working or OverlayTone.Info => StatusColorRole.Processing,
            OverlayTone.Warning or OverlayTone.Failure => StatusColorRole.Attention,
            _ => throw new ArgumentOutOfRangeException(nameof(tone)),
        };

        public OverlayIcon Icon => tone switch
        {
            OverlayTone.Idle => OverlayIcon.Done,
            OverlayTone.Recording => OverlayIcon.Microphone,
            OverlayTone.Working => OverlayIcon.Waveform,
            OverlayTone.Info => OverlayIcon.Info,
            OverlayTone.Warning or OverlayTone.Failure => OverlayIcon.Warning,
            _ => throw new ArgumentOutOfRangeException(nameof(tone)),
        };
    }
}
