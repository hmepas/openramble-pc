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

    /// <summary>
    /// Words that could not land. The panel grows to hold them, with the reason and the action;
    /// <see cref="Title"/> and <see cref="Subtitle"/> carry the words and the reason for a panel
    /// that draws nothing else.
    /// </summary>
    public WaitingWords? Waiting { get; init; }

    /// <summary>How a waiting take ended, said once before the panel leaves.</summary>
    public TakeOutcome? Outcome { get; init; }

    /// <summary>
    /// Whether the panel shows what the person said. Upstream keeps it out of screen captures
    /// while it does; see the Linux panel for why this one cannot.
    /// </summary>
    public bool ShowsDictatedWords => Waiting is not null;

    /// <param name="enginePreparation">
    /// What the recognition engine is doing right now; <c>null</c> when nobody manages its
    /// residency and it is simply always there.
    /// </param>
    /// <param name="isWaitingForEngine">
    /// The take is finished and the model is not resident yet, so the words are waiting for a
    /// load rather than for recognition.
    /// </param>
    /// <param name="waiting">Words that could not land.</param>
    /// <param name="outcome">How a waiting take just ended.</param>
    /// <param name="key">The dictation key, as the panel names it.</param>
    public static OverlayContent Make(
        DictationState state,
        DictationNotice? notice,
        TimeSpan elapsed,
        EnginePreparationState? enginePreparation = null,
        bool isWaitingForEngine = false,
        WaitingWords? waiting = null,
        TakeOutcome? outcome = null,
        DictationHotkey key = SettingsDefaults.Hotkey)
    {
        // How the waiting ended is the last word on it.
        if (outcome is not null)
        {
            return new OverlayContent
            {
                Title = outcome.Message,
                Subtitle = null,
                // Green is for the paste alone; kept is not success and not waiting.
                Tone = outcome is TakeOutcome.Pasted ? OverlayTone.Idle : OverlayTone.Info,
                AccessibilityLabel = outcome.Message,
                Announcement = outcome.Message,
                IsAnnouncementUrgent = false,
                Outcome = outcome,
            };
        }

        // Words that did not land are not a four-second notice: they wait, in full, with their
        // action - and only at rest. A new take files them in History before its microphone opens.
        // A notice shown meanwhile still goes first, as it does over a take.
        if (waiting is not null && notice is null && state is DictationState.Idle or DictationState.Inserting)
        {
            string label = waiting.AccessibilityLabel(key.Title);
            return new OverlayContent
            {
                Title = waiting.Words,
                Subtitle = waiting.Note is { } note ? $"{note} {waiting.Reason(key.Title)}" : waiting.Reason(key.Title),
                // Copied words wait too, but nothing about them is an alarm.
                Tone = waiting.IsCopied ? OverlayTone.Info : OverlayTone.Warning,
                AccessibilityLabel = label,
                // The person is looking at the app the words were meant for, so this interrupts,
                // like every other failed landing.
                Announcement = label,
                IsAnnouncementUrgent = true,
                Waiting = waiting,
            };
        }

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

        if (state == DictationState.Transcribing && isWaitingForEngine)
        {
            // The words are safe either way, and the distinction is the difference between a
            // panel that is working and a panel that is waiting. A model that has to be loaded
            // costs seconds, and on a short take there is no speech for that load to hide under,
            // so "Transcribing…" would spend the whole wait describing work that has not
            // started. Nothing is lost and nothing is at risk here - say what is actually
            // happening, and when the preparation narrates its own seconds, let them through.
            return new OverlayContent
            {
                Title = "Waking the model…",
                Subtitle = IsLoading(enginePreparation)
                    ? enginePreparation!.Title
                    : "Your words are waiting, not lost.",
                Tone = OverlayTone.Working,
                AccessibilityLabel = "Recording stopped, waiting for the speech model to load",
                Announcement = "Waiting for the speech model to load",
                IsAnnouncementUrgent = false,
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
            const string recorded = "Your words are recorded and waiting.";
            return new OverlayContent
            {
                Title = title,
                Subtitle = recorded,
                Tone = OverlayTone.Working,
                AccessibilityLabel = $"{title}. {recorded}",
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
            or EnginePreparationState.Phase.LoadingVocabulary
            or EnginePreparationState.Phase.WarmingUp;

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
