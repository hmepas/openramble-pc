using System.Diagnostics;
using DictationCore;

namespace OpenRamble.AppCore;

/// <summary>
/// State of the dictation panel: what is written on it, whether it is visible and what has
/// already been said out loud.
///
/// Lives separately from the window intentionally. A window requires a graphics session, and
/// the display rules - the seconds counter, the self-hiding message, the announcements - are
/// checked without one.
/// </summary>
/// <remarks>
/// <para>
/// The Swift original is <c>@MainActor</c> and relies on that for its safety. Here the state is
/// guarded by a lock instead, because the self-hide timer resumes on a thread-pool thread and
/// there is no main actor to hop back onto. Callbacks - visibility and announcements - are
/// raised outside the lock, so an implementation is free to marshal to the UI thread itself.
/// </para>
/// <para>
/// The Swift <c>Timer</c> that recomputed <c>elapsed</c> twice a second is gone:
/// <see cref="Elapsed"/> is computed from a monotonic start instant, and the timer's only
/// remaining job - telling the view to redraw - is left to the view, which owns its own frame
/// clock. <see cref="IsTicking"/> keeps the meaning the tests pin: the seconds are running.
/// </para>
/// </remarks>
public sealed class OverlayModel
{
    /// <summary>
    /// Exactly as many samples as the waveform draws: publishing more would mean half the
    /// buffer is never shown.
    /// </summary>
    public const int WaveformSampleCount = 24;

    private readonly Lock gate = new();
    private readonly IAccessibilityAnnouncing announcer;
    private readonly TimeSpan noticeDuration;
    private readonly TimeSpan? outcomeDuration;
    private readonly float[] waveformSamples = new float[WaveformSampleCount];

    /// <summary>Words that could not land. The application owns them; the panel only shows them.</summary>
    private WaitingWords? waiting;

    /// <summary>How a waiting take just ended.</summary>
    private TakeOutcome? outcome;

    private CancellationTokenSource? outcomeHide;
    private DictationHotkey key = SettingsDefaults.Hotkey;
    private bool isKeyDown;

    private DictationState state = DictationState.Idle;
    private DictationNotice? notice;
    private EnginePreparationState? enginePreparation;
    private bool isWaitingForEngine;
    /// <summary>
    /// A session dismissal that arrived under a notice. The notice owns the panel until its
    /// timer decides; then the dismissal is honoured rather than the finished session revived.
    /// </summary>
    private bool dismissalDeferred;
    private bool isVisible;
    private TimeSpan frozenElapsed;
    private MonotonicInstant? startedAt;
    private CancellationTokenSource? autoHide;

    /// <summary>
    /// What has already been said out loud. Without this memory a screen reader would repeat
    /// "recording" for as long as the dictation lasts and drown out everything else.
    /// </summary>
    private string? lastAnnouncement;

    /// <param name="outcomeDuration">A fixed time for every outcome, so a test need not wait out the real ones.</param>
    public OverlayModel(
        IAccessibilityAnnouncing announcer,
        TimeSpan? noticeDuration = null,
        TimeSpan? outcomeDuration = null)
    {
        this.announcer = announcer;
        this.noticeDuration = noticeDuration ?? TimeSpan.FromSeconds(4);
        this.outcomeDuration = outcomeDuration;
    }

    public WaitingWords? Waiting { get { lock (gate) { return waiting; } } }

    public TakeOutcome? Outcome { get { lock (gate) { return outcome; } } }

    /// <summary>The dictation key, as the panel names it.</summary>
    public DictationHotkey Key { get { lock (gate) { return key; } } }

    /// <summary>Whether the real key is down over the waiting words: the drawn one goes down with it.</summary>
    public bool IsKeyDown { get { lock (gate) { return isKeyDown; } } }

    /// <summary>The words that wait, or <c>null</c> once they no longer do.</summary>
    public void SetWaiting(WaitingWords? value, DictationHotkey dictationKey)
    {
        bool? visibilityChange;
        OverlayContent? toAnnounce = null;
        lock (gate)
        {
            key = dictationKey;
            if (waiting == value) { return; }

            waiting = value;
            if (value is null)
            {
                isKeyDown = false;
                visibilityChange = IsAtRestLocked && notice is null ? SetVisibleLocked(RestsVisibleLocked) : null;
            }
            else
            {
                // The panel is the answer now; whatever it said a moment ago is not.
                ClearOutcomeLocked();
                CancelAutoHideLocked();
                notice = null;
                dismissalDeferred = false;
                visibilityChange = SetVisibleLocked(true);
                toAnnounce = TakeAnnouncementLocked();
            }
        }

        Raise(visibilityChange, toAnnounce);
    }

    /// <summary>Say how a waiting take ended, then leave.</summary>
    public void ShowOutcome(TakeOutcome value)
    {
        bool? visibilityChange;
        OverlayContent? toAnnounce;
        CancellationToken token;
        lock (gate)
        {
            ClearOutcomeLocked();
            waiting = null;
            isKeyDown = false;
            outcome = value;
            visibilityChange = SetVisibleLocked(true);
            toAnnounce = TakeAnnouncementLocked();
            outcomeHide = new CancellationTokenSource();
            token = outcomeHide.Token;
        }

        Raise(visibilityChange, toAnnounce);
        _ = ExpireOutcome(outcomeDuration ?? value.Duration, token);
    }

    /// <summary>The dictation key went down over the waiting words, or came back up.</summary>
    public void SetKeyDown(bool down)
    {
        lock (gate) { isKeyDown = down; }
    }

    private async Task ExpireOutcome(TimeSpan duration, CancellationToken token)
    {
        try
        {
            await Task.Delay(duration, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        bool? visibilityChange = null;
        lock (gate)
        {
            if (token.IsCancellationRequested) { return; }

            outcome = null;
            outcomeHide = null;
            lastAnnouncement = null;
            if (IsAtRestLocked && notice is null) { visibilityChange = SetVisibleLocked(RestsVisibleLocked); }
        }

        Raise(visibilityChange, null);
    }

    private void ClearOutcomeLocked()
    {
        outcomeHide?.Cancel();
        outcomeHide = null;
        outcome = null;
    }

    private bool IsAtRestLocked => state is DictationState.Idle or DictationState.Inserting;

    /// <summary>Nothing but waiting words or their outcome keeps the panel up at rest.</summary>
    private bool RestsVisibleLocked => waiting is not null || outcome is not null;

    /// <summary>Whether the panel should be on the screen. Shown by its owner.</summary>
    public bool IsVisible
    {
        get
        {
            lock (gate)
            {
                return isVisible;
            }
        }
    }

    public Action<bool>? OnVisibilityChange { get; set; }

    public DictationState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public DictationNotice? Notice
    {
        get
        {
            lock (gate)
            {
                return notice;
            }
        }
    }

    /// <summary>Whether the seconds are counting.</summary>
    public bool IsTicking
    {
        get
        {
            lock (gate)
            {
                return startedAt is not null;
            }
        }
    }

    public TimeSpan Elapsed
    {
        get
        {
            lock (gate)
            {
                return ElapsedLocked();
            }
        }
    }

    /// <summary>Recent microphone peaks (0…1), oldest first.</summary>
    public IReadOnlyList<float> WaveformSamples
    {
        get
        {
            lock (gate)
            {
                return waveformSamples.ToArray();
            }
        }
    }

    public OverlayContent Content
    {
        get
        {
            lock (gate)
            {
                return OverlayContent.Make(state, notice, ElapsedLocked(), enginePreparation, isWaitingForEngine, waiting, outcome, key);
            }
        }
    }

    /// <summary>Whether the panel is currently telling someone that the model is still loading.</summary>
    public bool IsWaitingForEngine
    {
        get
        {
            lock (gate)
            {
                return isWaitingForEngine;
            }
        }
    }

    /// <summary>
    /// The take is finished and the model is not resident yet. <c>true</c> opens the wait,
    /// <c>false</c> closes it; the kernel calls the second on every exit, and a dismissal closes
    /// it too.
    /// </summary>
    public void SetWaitingForEngine(bool waiting)
    {
        lock (gate)
        {
            isWaitingForEngine = waiting;
        }
    }

    /// <summary>
    /// What the recognition engine is doing, pushed in by whoever manages its residency.
    /// </summary>
    /// <remarks>
    /// A separate channel from <see cref="Show"/> because it is not the dictation's state: a
    /// reload that rides under the person's voice starts before the key is released and can
    /// outlast it. The panel only ever mentions it while there is nothing else to say - see
    /// <see cref="OverlayContent.Make"/>.
    /// </remarks>
    public void UpdateEnginePreparation(EnginePreparationState? value)
    {
        lock (gate)
        {
            enginePreparation = value;
        }
    }

    /// <summary>Panel shortcut for a screen reader.</summary>
    public string AccessibilityLabel => Content.AccessibilityLabel;

    /// <summary>Show dictation status.</summary>
    public void Show(DictationState newState, TimeSpan elapsed)
    {
        bool? visibilityChange;
        OverlayContent? toAnnounce = null;

        lock (gate)
        {
            CancelAutoHideLocked();
            state = newState;
            notice = null;
            // A new take supersedes a dismissal deferred from the previous notice, and a new
            // take is not waiting for anything yet.
            dismissalDeferred = false;
            if (newState != DictationState.Transcribing) { isWaitingForEngine = false; }
            // A take in progress is the news; how the last one ended is not.
            if (newState is not (DictationState.Idle or DictationState.Inserting)) { ClearOutcomeLocked(); }

            // Once text is recognized, the destination application is the only useful feedback
            // surface. Do not cover it with a redundant insertion HUD.
            if (newState == DictationState.Inserting)
            {
                visibilityChange = HideLocked();
            }
            else
            {
                if (newState == DictationState.Listening)
                {
                    Array.Clear(waveformSamples);
                }

                SetElapsedLocked(elapsed, ticking: newState == DictationState.Listening);
                visibilityChange = SetVisibleLocked(true);
                toAnnounce = TakeAnnouncementLocked();
            }
        }

        Raise(visibilityChange, toAnnounce);
    }

    public void UpdateInputLevel(float level)
    {
        lock (gate)
        {
            if (state != DictationState.Listening)
            {
                return;
            }

            Array.Copy(waveformSamples, 1, waveformSamples, 0, WaveformSampleCount - 1);
            waveformSamples[WaveformSampleCount - 1] = Math.Clamp(level, 0f, 1f);
        }
    }

    /// <summary>Show the message and remove it after the configured time.</summary>
    public void ShowNotice(DictationNotice newNotice)
    {
        bool? visibilityChange;
        OverlayContent? toAnnounce;
        CancellationToken token;

        lock (gate)
        {
            // Words that did not land are not a four-second notice: the application has handed
            // them to SetWaiting, where they stay, in full, with their action.
            if (newNotice.RecoverableText is not null && newNotice.WordsDidNotLand && waiting is not null) { return; }

            CancelAutoHideLocked();
            // Keep the recording clock alive behind a transient notice. The notice does not
            // display elapsed time, but returning to the HUD must not lose the seconds during
            // which it was visible.
            SetElapsedLocked(ElapsedLocked(), ticking: state == DictationState.Listening);
            notice = newNotice;
            visibilityChange = SetVisibleLocked(true);
            toAnnounce = TakeAnnouncementLocked();
            autoHide = new CancellationTokenSource();
            token = autoHide.Token;
        }

        Raise(visibilityChange, toAnnounce);
        _ = ExpireNotice(token);
    }

    /// <summary>
    /// Remove the panel.
    ///
    /// The message stays on the screen: the person must have time to read it, and the cleanup
    /// after the session arrives right behind it.
    /// </summary>
    public void Hide()
    {
        bool? visibilityChange;
        lock (gate)
        {
            visibilityChange = HideLocked();
        }

        Raise(visibilityChange, null);
    }

    private bool? HideLocked()
    {
        if (notice is not null)
        {
            // The notice owns the panel until its timer decides whether to return to the
            // underlying session or honour this dismissal. Erasing the live state now would
            // resurrect nothing correctly: a notice over a running take must come back to
            // that take, and a notice over a finished one must not revive it.
            dismissalDeferred = true;
            return null;
        }

        SetElapsedLocked(ElapsedLocked(), ticking: false);
        // The session that owned the HUD is over. Without this line a notice shown later would
        // expire into the dead session's "Turning on the microphone…" - a panel nothing is
        // ever going to remove. A dismissed panel that stayed in Transcribing kept describing
        // live animated work after the window was gone.
        state = DictationState.Idle;
        isWaitingForEngine = false;
        CancelAutoHideLocked();
        bool? change = SetVisibleLocked(RestsVisibleLocked);
        // The next dictation must announce itself again, even if the state matches the
        // previous one.
        lastAnnouncement = null;
        return change;
    }

    private async Task ExpireNotice(CancellationToken token)
    {
        try
        {
            await Task.Delay(noticeDuration, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The deferred hide belongs to its own show: a newer notice or a new session took
            // the panel over, and this one has nothing left to remove.
            return;
        }

        bool? visibilityChange;
        OverlayContent? toAnnounce = null;

        lock (gate)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            notice = null;
            // The same warning in a later session is new information. Keep de-duplication only
            // for repeated updates within one impression.
            lastAnnouncement = null;

            if (dismissalDeferred)
            {
                // Cleanup followed the failure notice immediately. The person has had the
                // notice's whole lifetime to read it; now honour the dismissal instead of
                // resurrecting the completed transcription underneath it.
                dismissalDeferred = false;
                state = DictationState.Idle;
                isWaitingForEngine = false;
                SetElapsedLocked(ElapsedLocked(), ticking: false);
                visibilityChange = SetVisibleLocked(RestsVisibleLocked);
                if (RestsVisibleLocked) { toAnnounce = TakeAnnouncementLocked(); }
            }
            else
            {
                switch (state)
                {
                    case DictationState.Preparing:
                    case DictationState.Listening:
                    case DictationState.Transcribing:
                        // An unrelated menu action can show a notice while the microphone keeps
                        // running. Return to the underlying session instead of silently
                        // removing its HUD and elapsed timer.
                        SetElapsedLocked(ElapsedLocked(), ticking: state == DictationState.Listening);
                        visibilityChange = SetVisibleLocked(true);
                        toAnnounce = TakeAnnouncementLocked();
                        break;

                    default:
                        // At rest, only words that wait - or how they ended - keep the panel up.
                        visibilityChange = SetVisibleLocked(RestsVisibleLocked);
                        if (RestsVisibleLocked) { toAnnounce = TakeAnnouncementLocked(); }
                        break;
                }
            }
        }

        Raise(visibilityChange, toAnnounce);
    }

    private TimeSpan ElapsedLocked() =>
        startedAt is MonotonicInstant start ? start.DurationTo(MonotonicInstant.Now) : frozenElapsed;

    /// <summary>Show elapsed time, and decide whether it keeps running.</summary>
    private void SetElapsedLocked(TimeSpan value, bool ticking)
    {
        if (!ticking)
        {
            frozenElapsed = value;
            startedAt = null;
            return;
        }

        if (startedAt is not null)
        {
            return;
        }

        frozenElapsed = value;
        startedAt = MonotonicInstant.Now.Advanced(-value);
    }

    private void CancelAutoHideLocked()
    {
        autoHide?.Cancel();
        autoHide = null;
    }

    private bool? SetVisibleLocked(bool visible)
    {
        if (visible == isVisible)
        {
            return null;
        }

        isVisible = visible;
        return visible;
    }

    private OverlayContent? TakeAnnouncementLocked()
    {
        OverlayContent content = OverlayContent.Make(state, notice, ElapsedLocked(), enginePreparation, isWaitingForEngine, waiting, outcome, key);
        if (content.Announcement is not string announcement || announcement == lastAnnouncement)
        {
            return null;
        }

        lastAnnouncement = announcement;
        return content;
    }

    private void Raise(bool? visibilityChange, OverlayContent? toAnnounce)
    {
        if (visibilityChange is bool visible)
        {
            OnVisibilityChange?.Invoke(visible);
        }

        if (toAnnounce is not null)
        {
            Debug.Assert(toAnnounce.Announcement is not null, "announced content must carry words");
            announcer.Announce(toAnnounce.Announcement!, toAnnounce.IsAnnouncementUrgent);
        }
    }
}
