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
    private readonly float[] waveformSamples = new float[WaveformSampleCount];

    private DictationState state = DictationState.Idle;
    private DictationNotice? notice;
    private bool isVisible;
    private TimeSpan frozenElapsed;
    private MonotonicInstant? startedAt;
    private CancellationTokenSource? autoHide;

    /// <summary>
    /// What has already been said out loud. Without this memory a screen reader would repeat
    /// "recording" for as long as the dictation lasts and drown out everything else.
    /// </summary>
    private string? lastAnnouncement;

    public OverlayModel(IAccessibilityAnnouncing announcer, TimeSpan? noticeDuration = null)
    {
        this.announcer = announcer;
        this.noticeDuration = noticeDuration ?? TimeSpan.FromSeconds(4);
    }

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
                return OverlayContent.Make(state, notice, ElapsedLocked());
            }
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
        SetElapsedLocked(ElapsedLocked(), ticking: false);
        if (notice is not null)
        {
            return null;
        }

        CancelAutoHideLocked();
        bool? change = SetVisibleLocked(false);
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

            switch (state)
            {
                case DictationState.Preparing:
                case DictationState.Listening:
                case DictationState.Transcribing:
                    // An unrelated menu action can show a notice while the microphone keeps
                    // running. Return to the underlying session instead of silently removing
                    // its HUD and elapsed timer.
                    SetElapsedLocked(ElapsedLocked(), ticking: state == DictationState.Listening);
                    visibilityChange = SetVisibleLocked(true);
                    toAnnounce = TakeAnnouncementLocked();
                    break;

                default:
                    visibilityChange = SetVisibleLocked(false);
                    break;
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
        OverlayContent content = OverlayContent.Make(state, notice, ElapsedLocked());
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
