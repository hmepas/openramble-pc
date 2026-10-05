using System.Collections.Concurrent;
using DictationCore;
using OpenRamble.AppCore;

namespace OpenRamble.AppCore.Tests;

internal sealed class FakeAnnouncer : IAccessibilityAnnouncing
{
    private readonly ConcurrentQueue<(string Message, bool Urgent)> announcements = new();

    public IReadOnlyList<(string Message, bool Urgent)> Announcements => announcements.ToArray();

    public IReadOnlyList<string> Messages => announcements.Select(entry => entry.Message).ToArray();

    public void Announce(string message, bool urgent) => announcements.Enqueue((message, urgent));
}

/// <summary>
/// The behaviour of the panel: when it is on the screen, when it disappears and what it says out
/// loud.
///
/// The window itself takes no part here - it requires a graphics session, which the check may
/// not have. Everything else is checked completely.
/// </summary>
public sealed class OverlayModelTests
{
    private readonly FakeAnnouncer announcer = new();
    private readonly OverlayModel model;
    private readonly List<bool> visibility = [];

    public OverlayModelTests()
    {
        model = new OverlayModel(announcer, TimeSpan.FromMilliseconds(120));
        model.OnVisibilityChange = visible =>
        {
            lock (visibility)
            {
                visibility.Add(visible);
            }
        };
    }

    private IReadOnlyList<bool> Visibility
    {
        get
        {
            lock (visibility)
            {
                return visibility.ToArray();
            }
        }
    }

    /// <summary>
    /// Wait for the self-hide to actually happen.
    ///
    /// Earlier tests slept a fixed 220 ms against a 120 ms display time. A 100 ms margin is
    /// enough on one's own machine and not enough on a loaded runner: the message was still up
    /// and the check failed on level ground. We wait on the fact, with a large margin - a fast
    /// machine passes in the same milliseconds, a slow one simply waits longer.
    /// </summary>
    private async Task WaitForNoticeToHide(TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        while (DateTime.UtcNow < deadline)
        {
            if (model.Notice is null)
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("the message did not remove itself in time");
    }

    // MARK: - Showing and clearing

    [Fact]
    public void Scenario001()
    {
        model.Show(DictationState.Listening, TimeSpan.Zero);
        Assert.True(model.IsVisible);

        model.Hide();

        Assert.False(model.IsVisible);
        Assert.Equal([true, false], Visibility);
    }

    [Fact]
    public void Scenario002()
    {
        model.Show(DictationState.Preparing, TimeSpan.Zero);
        model.Show(DictationState.Listening, TimeSpan.Zero);
        model.Show(DictationState.Transcribing, TimeSpan.FromSeconds(1));

        Assert.Equal([true], Visibility);
    }

    // MARK: - Counter

    [Fact]
    public async Task Scenario003()
    {
        model.Show(DictationState.Listening, TimeSpan.Zero);
        Assert.True(model.IsTicking);

        // The core reports once at the start of the recording and once at the end. If the panel
        // does not count for itself, the whole dictation reads "0 s" on it.
        await Task.Delay(700);
        Assert.True(model.Elapsed > TimeSpan.FromSeconds(0.4));

        model.Show(DictationState.Transcribing, model.Elapsed);
        Assert.False(model.IsTicking);
    }

    [Fact]
    public void Scenario004()
    {
        model.Show(DictationState.Listening, TimeSpan.Zero);
        Assert.True(model.IsTicking);

        model.Hide();

        Assert.False(model.IsTicking);
    }

    // MARK: - Messages

    [Fact]
    public async Task Scenario005()
    {
        model.ShowNotice(new DictationNotice(DictationNoticeKind.Warning, "Text saved."));
        Assert.True(model.IsVisible);

        await WaitForNoticeToHide();

        Assert.Null(model.Notice);
        Assert.False(model.IsVisible);
    }

    /// <summary>
    /// The cleanup after the session arrives right behind the message. If it takes the panel with
    /// it, the person has no time to read that the text was not inserted and where it is now.
    /// </summary>
    [Fact]
    public void Scenario006()
    {
        model.ShowNotice(new DictationNotice(DictationNoticeKind.Failure, "Failed to insert text."));

        model.Hide();

        Assert.True(model.IsVisible);
        Assert.NotNull(model.Notice);
    }

    /// <summary>
    /// Two identical messages in a row.
    ///
    /// The deferred hide of the first one recognized "its own" message by the text and carried
    /// the second one off the screen three times sooner than it should.
    /// </summary>
    [Fact]
    public async Task Scenario007()
    {
        var ownAnnouncer = new FakeAnnouncer();
        var ownModel = new OverlayModel(ownAnnouncer, TimeSpan.FromSeconds(5));
        var notice = new DictationNotice(
            DictationNoticeKind.Warning,
            "Dictation is currently in progress. Please wait for it to finish.");

        ownModel.ShowNotice(notice);
        await Task.Delay(50);

        ownModel.ShowNotice(notice);
        await Task.Delay(50);

        Assert.NotNull(ownModel.Notice);
        Assert.True(ownModel.IsVisible);
    }

    [Fact]
    public void Scenario008()
    {
        model.ShowNotice(new DictationNotice(DictationNoticeKind.Warning, "Text saved."));

        model.Show(DictationState.Listening, TimeSpan.Zero);

        Assert.Null(model.Notice);
        Assert.Equal("Listening", model.Content.Title);
    }

    /// <summary>
    /// The deferred hide belongs to its own show. Otherwise it would extinguish the panel of the
    /// next dictation, already begun.
    /// </summary>
    [Fact]
    public async Task Scenario009()
    {
        model.ShowNotice(new DictationNotice(DictationNoticeKind.Info, "Hour limit reached."));
        model.Show(DictationState.Listening, TimeSpan.Zero);

        await Task.Delay(300);

        Assert.True(model.IsVisible);
    }

    // MARK: - Voice

    [Fact]
    public void Scenario010()
    {
        model.Show(DictationState.Listening, TimeSpan.Zero);

        Assert.Equal(["Recording."], announcer.Messages);
        Assert.True(announcer.Announcements[0].Urgent);
    }

    /// <summary>
    /// Without memory of what was said, a screen reader would repeat "recording" until the end of
    /// the dictation and drown out everything else.
    /// </summary>
    [Fact]
    public void Scenario011()
    {
        model.Show(DictationState.Listening, TimeSpan.Zero);
        model.Show(DictationState.Listening, TimeSpan.FromSeconds(1));
        model.Show(DictationState.Listening, TimeSpan.FromSeconds(2));

        Assert.Equal(["Recording."], announcer.Messages);
    }

    [Fact]
    public void Scenario012()
    {
        model.Show(DictationState.Listening, TimeSpan.Zero);
        model.Hide();

        model.Show(DictationState.Listening, TimeSpan.Zero);

        Assert.Equal(["Recording.", "Recording."], announcer.Messages);
    }

    [Fact]
    public void Scenario013()
    {
        model.ShowNotice(new DictationNotice(DictationNoticeKind.Failure, "Failed to record audio."));

        Assert.Equal(["Failed to record audio."], announcer.Messages);
        Assert.True(announcer.Announcements[0].Urgent);
    }

    [Fact]
    public void Scenario014()
    {
        model.Show(DictationState.Preparing, TimeSpan.Zero);
        model.Show(DictationState.Listening, TimeSpan.Zero);
        model.Show(DictationState.Transcribing, TimeSpan.FromSeconds(3));
        model.Show(DictationState.Inserting, TimeSpan.FromSeconds(3));

        Assert.Equal(
            ["Turning on the microphone", "Recording.", "Recording stopped, transcribing speech"],
            announcer.Messages);
    }

    // MARK: - Waveform

    [Fact]
    public void Scenario024()
    {
        model.Show(DictationState.Listening, TimeSpan.Zero);

        Assert.Equal(OverlayModel.WaveformSampleCount, model.WaveformSamples.Count);
        Assert.All(model.WaveformSamples, sample => Assert.Equal(0, sample));

        model.UpdateInputLevel(-1);
        model.UpdateInputLevel(0.25f);
        model.UpdateInputLevel(2);

        Assert.Equal(OverlayModel.WaveformSampleCount, model.WaveformSamples.Count);
        Assert.Equal([0f, 0.25f, 1f], model.WaveformSamples.TakeLast(3));
    }

    [Fact]
    public void Scenario025()
    {
        model.Show(DictationState.Listening, TimeSpan.Zero);
        model.UpdateInputLevel(0.5f);
        Assert.Equal(0.5f, model.WaveformSamples[^1]);

        model.Hide();
        model.Show(DictationState.Listening, TimeSpan.Zero);

        Assert.All(model.WaveformSamples, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void Scenario026()
    {
        model.Show(DictationState.Transcribing, TimeSpan.FromSeconds(2));
        Assert.True(model.IsVisible);

        model.Show(DictationState.Inserting, TimeSpan.FromSeconds(2));

        Assert.False(model.IsVisible);
        Assert.False(model.IsTicking);
    }

    [Fact]
    public async Task Scenario027()
    {
        var notice = new DictationNotice(DictationNoticeKind.Warning, "Check the microphone.");
        model.ShowNotice(notice);
        // A second announcement is possible only after the first show has ended: while it is on
        // screen a repeat counts as a duplicate and stays silent.
        await WaitForNoticeToHide();

        model.ShowNotice(notice);

        Assert.Equal(["Check the microphone.", "Check the microphone."], announcer.Messages);
    }

    /// <summary>
    /// A failing start shows its notice and the cleanup dismisses the session right behind it.
    /// The notice must still leave on its own - and must not expire into the dead session's
    /// "Turning on the microphone…", which nothing would ever remove.
    /// </summary>
    [Fact]
    public async Task FailureNoticeAfterSessionCleanupHidesThePanel()
    {
        model.Show(DictationState.Preparing, TimeSpan.Zero);
        model.ShowNotice(new DictationNotice(DictationNoticeKind.Failure, "Couldn't record audio."));
        model.Hide();

        await WaitForNoticeToHide();

        Assert.False(model.IsVisible);
    }

    /// <summary>
    /// Same trap through the other door: a menu-action notice after a finished dictation must not
    /// resurrect that dictation's HUD out of the stale state the panel was hidden in.
    /// </summary>
    [Fact]
    public async Task NoticeAfterFinishedSessionDoesNotResurrectItsHud()
    {
        model.Show(DictationState.Listening, TimeSpan.Zero);
        model.Hide();

        model.ShowNotice(new DictationNotice(DictationNoticeKind.Info, "Copied."));
        await WaitForNoticeToHide();

        Assert.False(model.IsVisible);
    }

    [Fact]
    public async Task NoticeDuringRecordingReturnsToLiveHudAfterExpiry()
    {
        model.Show(DictationState.Listening, TimeSpan.FromSeconds(3));
        model.ShowNotice(new DictationNotice(DictationNoticeKind.Info, "Copied."));

        await Task.Delay(650);

        Assert.Equal(DictationState.Listening, model.State);
        Assert.Null(model.Notice);
        Assert.True(model.IsVisible);
        Assert.True(model.IsTicking);
        Assert.Equal("Listening", model.Content.Title);
        Assert.True(model.Elapsed > TimeSpan.FromSeconds(3.4), "notice time must remain part of recording time");
    }

    /// <summary>
    /// A session dismissal must not erase the live state under a notice. The notice owns the
    /// panel until its timer decides whether to return to the underlying session or hide an idle
    /// panel.
    /// </summary>
    [Fact]
    public void ADismissalUnderANoticeLeavesTheLiveStateAlone()
    {
        model.Show(DictationState.Listening, TimeSpan.FromSeconds(3));
        model.ShowNotice(new DictationNotice(DictationNoticeKind.Info, "Copied."));

        model.Hide();

        Assert.Equal(DictationState.Listening, model.State);
        Assert.True(model.IsVisible);
        Assert.NotNull(model.Notice);
    }

    /// <summary>
    /// Cleanup may follow a failure notice immediately. Let the person read the notice, then
    /// honour that dismissal instead of resurrecting the completed transcription underneath it.
    /// </summary>
    [Fact]
    public async Task ADeferredDismissalIsHonouredWhenTheNoticeExpires()
    {
        model.Show(DictationState.Transcribing, TimeSpan.FromSeconds(3));
        model.SetWaitingForEngine(true);
        model.ShowNotice(new DictationNotice(DictationNoticeKind.Failure, "Recognition failed."));

        model.Hide();

        Assert.True(model.IsVisible);
        Assert.NotNull(model.Notice);
        await WaitForNoticeToHide();
        Assert.Equal(DictationState.Idle, model.State);
        Assert.False(model.IsWaitingForEngine);
        Assert.False(model.IsVisible);
    }

    /// <summary>
    /// Starting another take supersedes a deferred dismissal from the previous notice. A later
    /// notice must return to that new live session.
    /// </summary>
    [Fact]
    public async Task ANewTakeSupersedesADeferredDismissal()
    {
        model.Show(DictationState.Transcribing, TimeSpan.FromSeconds(3));
        model.ShowNotice(new DictationNotice(DictationNoticeKind.Failure, "Recognition failed."));
        model.Hide();

        model.Show(DictationState.Listening, TimeSpan.Zero);
        model.ShowNotice(new DictationNotice(DictationNoticeKind.Info, "Copied."));

        await WaitForNoticeToHide();
        Assert.Equal(DictationState.Listening, model.State);
        Assert.True(model.IsVisible);
        Assert.True(model.IsTicking);
    }

    /// <summary>The wait reaches the panel's words, and a new take clears it.</summary>
    [Fact]
    public void TheModelWaitReachesThePanelAndANewTakeClearsIt()
    {
        model.Show(DictationState.Transcribing, TimeSpan.FromSeconds(1));
        model.SetWaitingForEngine(true);
        Assert.Equal("Waking the model…", model.Content.Title);

        model.SetWaitingForEngine(false);
        Assert.Equal("Transcribing…", model.Content.Title);

        model.SetWaitingForEngine(true);
        model.Show(DictationState.Preparing, TimeSpan.Zero);
        Assert.False(model.IsWaitingForEngine);
    }

    /// <summary>
    /// The panel picks up the engine's own narration, and drops it again when the load lands.
    ///
    /// Pushed in rather than passed to <c>Show</c>, because a reload starts before the key is
    /// released and can outlast it: between the release and the result there is no state change
    /// left to carry it.
    /// </summary>
    [Fact]
    public void TheLoadingEngineReachesThePanelAndLeavesItAgain()
    {
        model.Show(DictationState.Transcribing, TimeSpan.FromSeconds(3));
        Assert.Equal("Transcribing…", model.Content.Title);

        model.UpdateEnginePreparation(EnginePreparationState.Make(
            EnginePreparationState.Phase.LoadingRecognizer,
            TimeSpan.FromSeconds(12)));

        Assert.Contains("12", model.Content.Title, StringComparison.Ordinal);
        Assert.True(model.IsVisible);

        model.UpdateEnginePreparation(EnginePreparationState.Make(
            EnginePreparationState.Phase.Ready,
            TimeSpan.Zero));

        Assert.Equal("Transcribing…", model.Content.Title);
    }

    /// <summary>
    /// A load that ticks for a minute must not make the screen reader read a minute of seconds.
    ///
    /// The panel announces on entering a state, and the loading line changes every second. Reading
    /// each one would bury whatever the person was actually listening to.
    /// </summary>
    [Fact]
    public void TheTickingLoadIsNeverAnnounced()
    {
        model.Show(DictationState.Transcribing, TimeSpan.Zero);
        int spokenBefore = announcer.Announcements.Count;

        for (int second = 1; second <= 5; second += 1)
        {
            model.UpdateEnginePreparation(EnginePreparationState.Make(
                EnginePreparationState.Phase.LoadingRecognizer,
                TimeSpan.FromSeconds(second)));
            model.Show(DictationState.Transcribing, TimeSpan.Zero);
        }

        Assert.Equal(spokenBefore, announcer.Announcements.Count);
    }
}
