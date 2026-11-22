using DictationCore;
using LocalASR;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// Application wiring: what happens at the edges, where things usually break.
/// </summary>
/// <remarks>
/// The macOS file's Accessibility scenarios (003-010, 013) have no counterpart: Windows needs no
/// such grant, so there is nothing to request, relaunch or repair. Its 025 (a top-up download
/// after an app update) needs a second model, which this port does not install.
/// </remarks>
public sealed class AppStateTests : IDisposable
{
    private readonly AppHarness harness = new();

    private FakeHotkeyMonitor Monitor => harness.Monitor;

    private FakeOverlay Overlay => harness.Overlay;

    private FakeCapture Capture => harness.Capture;

    public void Dispose() => harness.Dispose();

    [Fact]
    public void Scenario001()
    {
        string message = AppState.CaptureFailureMessage(
            new AudioCaptureError.UnsupportedAudioFormat("48 kHz stereo couldn't be converted"),
            MicrophoneRemedy.Windows);

        Assert.Contains("audio format", message);
        Assert.Contains("48 kHz stereo", message);
        Assert.DoesNotContain("disk space", message);
    }

    /// <summary>
    /// A refused microphone is the one capture failure whose remedy belongs to a platform.
    ///
    /// The Windows sentence is pinned here by its words, because it is what ships on Windows and
    /// no test of ours runs there; the Linux one is checked for what it must not say - there is no
    /// Settings page on that side to send anybody to. See <see cref="MicrophoneRemedy"/>.
    /// </summary>
    [Fact]
    public void TheRefusalMessageIsThePlatformsOwn()
    {
        var refused = new AudioCaptureError.MicrophonePermissionDenied();

        Assert.Equal(
            "No microphone access. Open Windows Settings.",
            AppState.CaptureFailureMessage(refused, MicrophoneRemedy.Windows));
        Assert.DoesNotContain(
            "Windows",
            AppState.CaptureFailureMessage(refused, MicrophoneRemedy.Linux),
            StringComparison.Ordinal);
    }

    // MARK: - Microphone level

    /// <summary>
    /// A take that peaks far below any healthy microphone earns one hint, after the session,
    /// and only once per run - the second quiet take says nothing new about the hardware.
    /// </summary>
    [Fact]
    public async Task AVeryQuietTakeIsExplainedOnce()
    {
        AppState state = await ReadyState();

        await DictateOnceAtLevel(state, peak: 0.005f);
        Assert.Contains(Overlay.Notices, notice => notice.Message.Contains("very quiet", StringComparison.Ordinal));
        Assert.Contains("-46 dB", Overlay.Notices.Last(n => n.Message.Contains("very quiet", StringComparison.Ordinal)).Message, StringComparison.Ordinal);

        int hints = Overlay.Notices.Count(notice => notice.Message.Contains("very quiet", StringComparison.Ordinal));
        await DictateOnceAtLevel(state, peak: 0.005f);
        Assert.Equal(hints, Overlay.Notices.Count(notice => notice.Message.Contains("very quiet", StringComparison.Ordinal)));
    }

    /// <summary>The first field laptop peaked at −25 dBFS and must pass in silence.</summary>
    [Fact]
    public async Task AnOrdinaryTakeEarnsNoLevelHint()
    {
        AppState state = await ReadyState();

        await DictateOnceAtLevel(state, peak: 0.059f);

        Assert.DoesNotContain(Overlay.Notices, notice => notice.Message.Contains("very quiet", StringComparison.Ordinal));
    }

    private async Task DictateOnceAtLevel(AppState state, float peak)
    {
        Monitor.OnPress!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);
        harness.OnSamples!([peak]);
        Monitor.OnRelease!();
        await TestWait.For("dictation ended", () => state.DictationStateValue == DictationState.Idle);
    }

    // MARK: - Onboarding

    /// <summary>
    /// An update is not a first launch. The flow comes back only when this build asks for a newer
    /// revision than the one the person finished - which is a decision made in code, on purpose.
    /// </summary>
    [Fact]
    public void OnboardingReturnsOnlyWhenItsRevisionMovedOn()
    {
        harness.Settings.Write(new AppSettings { OnboardingCompletedRevision = OnboardingRevision.Current });
        Assert.True(harness.MakeState().OnboardingCompleted);

        harness.Settings.Write(new AppSettings { OnboardingCompletedRevision = OnboardingRevision.Current - 1 });
        AppState state = harness.MakeState();
        Assert.False(state.OnboardingCompleted);

        state.OnboardingCompleted = true;
        Assert.Equal(OnboardingRevision.Current, harness.Settings.Read().OnboardingCompletedRevision);
    }

    // MARK: - Permissions

    [Fact]
    public async Task FirstLaunchDoesNotCoverOnboardingWithPermissionNotice()
    {
        harness.Permissions.MicrophoneGranted = false;

        _ = harness.MakeState();
        await Task.Delay(50);

        Assert.Empty(Overlay.Notices);
    }

    [Fact]
    public async Task ReturningUserSeesTheMissingPermissionNamed()
    {
        harness.Settings.Write(new AppSettings { OnboardingCompletedRevision = OnboardingRevision.Current });
        harness.Permissions.MicrophoneGranted = false;

        _ = harness.MakeState();
        await TestWait.For("the notice reached the panel", () => Overlay.Notices.Count > 0);

        Assert.Equal(
            "Dictation is off: grant Microphone access in Windows Settings.",
            Overlay.Notices[0].Message);
    }

    /// <summary>
    /// The same notice on the other platform, in words that are true there.
    ///
    /// The sentence pinned above is Windows', and this is the whole extent of the difference:
    /// nothing was granted on Linux, so nothing can be revoked and there is no page to be sent to.
    /// Every other word of the permission story is shared.
    /// </summary>
    [Fact]
    public async Task TheMissingPermissionNoticeIsThePlatformsOwn()
    {
        harness.MicrophoneRemedy = MicrophoneRemedy.Linux;
        harness.Settings.Write(new AppSettings { OnboardingCompletedRevision = OnboardingRevision.Current });
        harness.Permissions.MicrophoneGranted = false;

        _ = harness.MakeState();
        await TestWait.For("the notice reached the panel", () => Overlay.Notices.Count > 0);

        Assert.Equal(MicrophoneRemedy.Linux.DictationIsOff, Overlay.Notices[0].Message);
        Assert.DoesNotContain("Windows", Overlay.Notices[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scenario002()
    {
        harness.Permissions.MicrophoneGranted = false;

        AppState state = harness.MakeState();

        Assert.False(state.MicrophoneGranted);
        Assert.False(state.IsDictationReady);
        Assert.Equal(MicrophoneConsentState.Denied, state.MicrophoneState);
    }

    /// <summary>
    /// The permission poll runs forever by design — so an unchanged tick must publish nothing.
    /// An unguarded write here re-rendered the entire tray scene once a second for the whole life
    /// of the process.
    /// </summary>
    [Fact]
    public async Task SteadyStatePermissionTickPublishesNothing()
    {
        AppState state = harness.MakeState();
        state.RefreshPermissions();
        // Let the launch-time async work (model scan, recovery import) settle so it cannot
        // masquerade as tick-driven churn.
        await Task.Delay(300);

        int publishes = 0;
        state.PropertyChanged += (_, _) => Interlocked.Increment(ref publishes);
        state.RefreshPermissions();
        state.RefreshPermissions();

        Assert.Equal(0, publishes);
    }

    /// <summary>
    /// The hook needs no consent, so unlike macOS it is running from the first line and a revoked
    /// microphone does not stop it. What a revoke does stop is dictation.
    /// </summary>
    [Fact]
    public void Scenario011()
    {
        AppState state = harness.MakeState();

        Assert.True(Monitor.IsRunning);
        Assert.Equal(DictationHotkey.RightCtrl, Monitor.Hotkey);
        Assert.False(state.IsDictationReady);
    }

    [Fact]
    public void Scenario012()
    {
        AppState state = harness.MakeState();

        harness.Permissions.MicrophoneGranted = false;
        state.RefreshPermissions();

        Assert.True(Monitor.IsRunning);
        Assert.False(state.IsDictationReady);
    }

    [Fact]
    public async Task MicrophoneGrantReturnsFocusToOpenRamble()
    {
        harness.Permissions.MicrophoneGranted = false;
        harness.MicrophoneConsent.RequestResult = true;
        AppState state = harness.MakeState();

        state.RequestMicrophone();
        await TestWait.For("the app came back to the front", () => harness.MicrophoneConsent.ActivationCount > 0);

        Assert.Equal(1, harness.MicrophoneConsent.RequestCount);
        Assert.Equal(0, harness.MicrophoneConsent.OpenSettingsCount);
        await TestWait.For("the permission is seen", () => state.MicrophoneGranted);
    }

    [Fact]
    public async Task DeniedMicrophoneRequestKeepsSettingsInFront()
    {
        harness.Permissions.MicrophoneGranted = false;
        harness.MicrophoneConsent.RequestResult = false;
        AppState state = harness.MakeState();

        state.RequestMicrophone();
        await TestWait.For("Settings was opened", () => harness.MicrophoneConsent.OpenSettingsCount > 0);

        Assert.Equal(1, harness.MicrophoneConsent.RequestCount);
        Assert.Equal(0, harness.MicrophoneConsent.ActivationCount);
        Assert.False(state.MicrophoneGranted);
        Assert.Equal(MicrophoneConsentState.WaitingForSettings, state.MicrophoneState);
    }

    /// <summary>The take that was interrupted by a revoke must survive on disk.</summary>
    [Fact]
    public async Task Scenario014()
    {
        harness.InstallModelMarker();
        AppState state = harness.MakeState();
        await state.RefreshModelState();
        Monitor.OnPress!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);

        harness.Permissions.MicrophoneGranted = false;
        state.RefreshPermissions();
        await TestWait.For(
            "the take reached safekeeping",
            () => harness.RecoveryFiles().Count > 0 && state.DictationStateValue == DictationState.Idle);

        Assert.False(state.IsDictationReady);
        Assert.NotEmpty(harness.RecoveryFiles());
    }

    [Fact]
    public async Task Scenario015()
    {
        harness.InstallModelMarker();
        harness.Transcription.Delay = TimeSpan.FromMilliseconds(300);
        AppState state = harness.MakeState();
        await state.RefreshModelState();
        Monitor.OnPress!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);
        Monitor.OnRelease!();
        await TestWait.For("transcription started", () => state.DictationStateValue == DictationState.Transcribing);

        harness.Permissions.MicrophoneGranted = false;
        state.RefreshPermissions();

        Assert.Contains("Microphone access was revoked", state.LastNotice?.Message);
    }

    [Fact]
    public void Scenario016()
    {
        harness.Permissions.MicrophoneGranted = false;

        Assert.False(harness.MakeState().IsDictationReady);
    }

    /// <summary>Pressing the key without the permission must not start a recording.</summary>
    [Fact]
    public void Scenario017()
    {
        harness.Permissions.MicrophoneGranted = false;
        AppState state = harness.MakeState();

        Monitor.OnPress!();

        Assert.Equal(DictationState.Idle, state.DictationStateValue);
    }

    // MARK: - Model

    [Fact]
    public async Task Scenario018()
    {
        AppState state = harness.MakeState();
        await state.RefreshModelState();
        Assert.False(state.ModelStateValue.IsReady);

        Monitor.OnPress!();

        Assert.Equal(DictationState.Idle, state.DictationStateValue);
    }

    // MARK: - First successful dictation

    [Fact]
    public async Task Scenario019()
    {
        AppState state = await ReadyState();
        Assert.Equal(0, state.SuccessfulDictationCount);

        Monitor.OnPress!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);
        Assert.Equal(0, state.SuccessfulDictationCount);
        Monitor.OnRelease!();
        await TestWait.For("dictation ended", () => state.DictationStateValue == DictationState.Idle);

        Assert.Equal(1, state.SuccessfulDictationCount);
    }

    [Fact]
    public async Task Scenario020()
    {
        AppState state = await ReadyState();

        Monitor.OnPress!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);
        state.CancelCurrentDictation();
        await TestWait.For("dictation ended", () => state.DictationStateValue == DictationState.Idle);

        Assert.Equal("Dictation cancelled. Its local recording is queued for deletion.", state.LastNotice?.Message);
        Assert.Equal(0, state.SuccessfulDictationCount);
    }

    [Fact]
    public async Task Scenario021()
    {
        AppState state = await ReadyState();
        Assert.True(state.ModelStateValue.IsReady);

        Monitor.OnPress!();

        Assert.Equal(DictationState.Preparing, state.DictationStateValue);
    }

    /// <summary>
    /// Deleting a model in the middle of a dictation. The model is at work right now; taking it
    /// away means losing what has already been said and showing a vague loading error instead.
    /// </summary>
    [Fact]
    public async Task Scenario022()
    {
        AppState state = await ReadyState();
        Monitor.OnPress!();
        Assert.NotEqual(DictationState.Idle, state.DictationStateValue);

        state.DeleteModel();

        Assert.Equal(DictationNoticeKind.Warning, state.LastNotice?.Kind);
        Assert.True(state.ModelStateValue.IsReady);
        await TestWait.For(
            "the message reached the panel",
            () => Overlay.Notices.Any(notice => notice.Message.Contains("Wait for it to finish")));
    }

    [Fact]
    public async Task Scenario023()
    {
        AppState state = await ReadyState();
        Assert.True(state.ModelStateValue.IsReady);

        state.DeleteModel();

        await TestWait.For("the model is gone", () => !state.ModelStateValue.IsReady);
    }

    /// <summary>
    /// The model files are intact, but the engine does not pick them up. A disk inspection cannot
    /// see that state - it would say "ready" again.
    /// </summary>
    [Fact]
    public async Task Scenario026()
    {
        harness.InstallModelMarker();
        harness.WarmUpEngine = new FailingASREngine();
        AppState state = harness.MakeState();

        // The warm-up at launch fails and demands an explicit repair.
        await TestWait.For(
            "the warm-up failed honestly",
            () => state.ModelStateValue is ModelState.RepairRequired);

        // The person opened settings - the screen refreshed the model state from disk.
        await state.RefreshModelState();

        // The repair button may not disappear: dictation does not work, and "preparing the
        // model…" without end is a dead end with no way out.
        Assert.IsType<ModelState.RepairRequired>(state.ModelStateValue);
        Assert.False(state.IsEngineReady);
    }

    // MARK: - Press when not ready

    /// <summary>
    /// Pressing when not ready used to do absolutely nothing: the kernel rejected the start
    /// silently, and the person was left with a seemingly working program that ignores a key.
    /// </summary>
    [Fact]
    public async Task Scenario027()
    {
        AppState state = harness.MakeState();
        await state.RefreshModelState();
        Assert.False(state.ModelStateValue.IsReady);

        Monitor.OnPress!();

        Assert.Equal(DictationState.Idle, state.DictationStateValue);
        await WaitForNotice("model isn't downloaded");
    }

    [Fact]
    public async Task Scenario028()
    {
        harness.InstallModelMarker();
        harness.Permissions.MicrophoneGranted = false;
        AppState state = harness.MakeState();
        await state.RefreshModelState();

        Monitor.OnPress!();

        Assert.Equal(DictationState.Idle, state.DictationStateValue);
        await WaitForNotice("microphone access");
    }

    /// <summary>The same answer to the gesture without holding: silence would be dishonest here too.</summary>
    [Fact]
    public async Task Scenario029()
    {
        AppState state = harness.MakeState();
        await state.RefreshModelState();

        Monitor.OnDoubleTap!();

        Assert.Equal(DictationState.Idle, state.DictationStateValue);
        Assert.False(state.IsHandsFreeActive);
        await WaitForNotice("model isn't downloaded");
    }

    /// <summary>There is nothing to explain when ready: the panel shows the dictation itself.</summary>
    [Fact]
    public async Task Scenario030()
    {
        AppState state = await ReadyState();
        int before = Overlay.Notices.Count;

        Monitor.OnPress!();
        Assert.Equal(DictationState.Preparing, state.DictationStateValue);

        await Task.Delay(80);
        Assert.Equal(before, Overlay.Notices.Count);
    }

    /// <summary>
    /// A press on top of a running dictation is also rejected by the kernel - and here silence is
    /// appropriate: the panel is on the screen and the person can see what is happening.
    /// </summary>
    [Fact]
    public async Task Scenario031()
    {
        AppState state = await ReadyState();
        Monitor.OnPress!();
        Assert.Equal(DictationState.Preparing, state.DictationStateValue);
        int before = Overlay.Notices.Count;

        Monitor.OnPress!();

        await Task.Delay(80);
        Assert.Equal(before, Overlay.Notices.Count);
    }

    // MARK: - The raw key

    /// <summary>
    /// The key is published as a physical fact, separately from any dictation it may start.
    ///
    /// This is what the first-launch try-out shows. Without it, a press that starts nothing - the
    /// engine still loading, the microphone not granted, a virtual-machine client keeping the key
    /// for itself - is indistinguishable from a press that never arrived, and the person's only
    /// remaining move is to reinstall.
    /// </summary>
    [Fact]
    public void TheRawKeyIsVisibleEvenWhenNothingStarts()
    {
        harness.Permissions.MicrophoneGranted = false;
        AppState state = harness.MakeState();
        Assert.False(state.IsHotkeyHeld);
        Assert.Equal(0, state.HotkeyPressCount);

        Monitor.OnHotkeyDown!();

        Assert.True(state.IsHotkeyHeld);
        Assert.Equal(1, state.HotkeyPressCount);
        Assert.Equal(DictationState.Idle, state.DictationStateValue);

        Monitor.OnHotkeyUp!();

        Assert.False(state.IsHotkeyHeld);
        Assert.Equal(1, state.HotkeyPressCount);
    }

    /// <summary>The interface subscribes to these two; a silent change would never reach a screen.</summary>
    [Fact]
    public void TheRawKeyIsAnnouncedToTheInterface()
    {
        AppState state = harness.MakeState();
        var changed = new List<string?>();
        // Launch work raises its own changes from background threads while this test reads.
        state.PropertyChanged += (_, args) => { lock (changed) { changed.Add(args.PropertyName); } };

        Monitor.Tap();

        string?[] seen;
        lock (changed) { seen = [.. changed]; }
        Assert.Contains(nameof(AppState.IsHotkeyHeld), seen);
        Assert.Contains(nameof(AppState.HotkeyPressCount), seen);
    }

    /// <summary>
    /// Sleep eats the release. Without erasing the memory of the hold on wake, the try-out - and
    /// the menu behind it - would show a key held down for the rest of the session.
    /// </summary>
    [Fact]
    public void WakingUpForgetsAHoldThatWasNeverReleased()
    {
        AppState state = harness.MakeState();
        Monitor.OnHotkeyDown!();
        Assert.True(state.IsHotkeyHeld);

        harness.SystemEvents.PostDidWake();

        Assert.False(state.IsHotkeyHeld);
    }

    /// <summary>
    /// Tracking that could not be installed is said out loud.
    ///
    /// The alternative is the worst failure this application has: a process that runs, shows its
    /// icon, holds nothing, and answers no keypress for the rest of the session, with nothing
    /// anywhere explaining why.
    /// </summary>
    [Fact]
    public async Task ARefusedKeyboardHookIsSaidOutLoud()
    {
        _ = harness.MakeState();

        Monitor.OnTrackingFailure!("OpenRamble couldn't start watching the dictation key.");

        await WaitForNotice("couldn't start watching the dictation key");
        Assert.Equal(DictationNoticeKind.Failure, Overlay.Notices[^1].Kind);
    }

    // MARK: - Gestures

    [Fact]
    public async Task Scenario032()
    {
        AppState state = await ReadyState();

        Monitor.OnDoubleTap!();

        Assert.Equal(DictationState.Preparing, state.DictationStateValue);
        Assert.True(state.IsHandsFreeActive);
    }

    /// <summary>
    /// The double tap arrives after the first press already started the session. A new one cannot
    /// start at that moment - it would not pass the "free state" check, and hands-free mode would
    /// be unreachable.
    /// </summary>
    [Fact]
    public async Task Scenario033()
    {
        AppState state = await ReadyState();
        Monitor.OnPress!();
        Assert.False(state.IsHandsFreeActive);

        Monitor.OnDoubleTap!();

        Assert.True(state.IsHandsFreeActive);
        Assert.True(Monitor.IsHandsFreeActive);
    }

    [Fact]
    public async Task Scenario034()
    {
        AppState state = await ReadyState();

        // While there is no dictation, Escape is an ordinary key for somebody else's application.
        Monitor.OnEscape!();

        Assert.Equal(DictationState.Idle, state.DictationStateValue);
        Assert.Equal(0, Capture.AbortCount);
    }

    [Fact]
    public async Task Scenario035()
    {
        AppState state = await ReadyState();
        Monitor.OnPress!();

        Monitor.OnEscape!();

        await TestWait.For("dictation cancelled", () => state.DictationStateValue == DictationState.Idle);
    }

    /// <summary>
    /// Escape during insertion. There is nothing to cancel: the paste has already gone into
    /// somebody else's window. The text appears in the document a moment later - so a message
    /// about a deleted recording would say the exact opposite of what the person sees.
    /// </summary>
    [Fact]
    public async Task Scenario061()
    {
        AppState state = await ReadyState();

        harness.Inserter.HoldInsertions();
        Monitor.OnPress!();
        await TestWait.For(
            "insertion started",
            () =>
            {
                if (state.DictationStateValue == DictationState.Listening) { Monitor.OnRelease!(); }
                return state.DictationStateValue == DictationState.Inserting;
            });

        Monitor.OnEscape!();

        Assert.NotEqual("Dictation cancelled. Its local recording is queued for deletion.", state.LastNotice?.Message);

        harness.Inserter.ReleaseInsertions();
        await TestWait.For("dictation ended", () => state.DictationStateValue == DictationState.Idle);
        Assert.Equal(["Ping test"], harness.Inserter.InsertedTexts);
    }

    // MARK: - Settings

    [Fact]
    public async Task Scenario036()
    {
        const string spokenTerm = "зетапрайм";
        harness.Transcription.Text = $"open {spokenTerm} port and check the indexes";
        AppState state = await ReadyState();

        await DictateOnce(state);

        AppState.LastDictation last = Assert.IsType<AppState.LastDictation>(state.LastDictationValue);
        Assert.Contains(spokenTerm, last.InsertedText);

        // The person corrected the term - the dictionary learned exactly this pair.
        int learned = state.LearnCorrections(last.InsertedText.Replace(spokenTerm, "ZetaPrime"));

        Assert.Equal(1, learned);
        Assert.Contains(
            state.Replacements,
            replacement => replacement.Spoken == spokenTerm && replacement.Written == "ZetaPrime");

        // Repeating the same edit produces no duplicates.
        Assert.Equal(0, state.LearnCorrections(last.InsertedText.Replace(spokenTerm, "ZetaPrime")));
    }

    [Fact]
    public void Scenario038()
    {
        Assert.False(
            harness.MakeState().LearnFromEdits,
            "learning from edits rereads the contents of somebody else's field; only a person may turn that on");
    }

    [Fact]
    public void Scenario039()
    {
        AppState state = harness.MakeState();

        state.LearnFromEdits = true;
        Assert.True(harness.Settings.Read().LearnFromEdits);

        // "Restart": a new AppState over the same settings file.
        Assert.True(harness.MakeState().LearnFromEdits);

        state.LearnFromEdits = false;
        Assert.False(harness.MakeState().LearnFromEdits);
    }

    /// <summary>
    /// A switched-off toggle must mean "the field is not read", not "read and kept quiet". The
    /// difference is visible only from the outside, so the captures are counted, not the result.
    /// </summary>
    [Fact]
    public async Task Scenario040()
    {
        AppState state = await ReadyState();
        state.LearnFromEdits = false;

        await DictateOnce(state);

        Assert.Equal(0, harness.FocusedField.Captured);
    }

    /// <summary>
    /// Positive control for the previous test: without it, wiring where the field is never read
    /// would pass.
    /// </summary>
    [Fact]
    public async Task Scenario041()
    {
        AppState state = await ReadyState();
        state.LearnFromEdits = true;

        await DictateOnce(state);

        await TestWait.For("the field was read once", () => harness.FocusedField.Captured == 1);
    }

    [Fact]
    public void Scenario043()
    {
        AppState state = harness.MakeState();

        state.Hotkey = DictationHotkey.CapsLock;

        Assert.Equal(DictationHotkey.CapsLock, Monitor.Hotkey);
        Assert.Equal("CapsLock", harness.Settings.Read().Hotkey);
        Assert.Equal(DictationHotkey.CapsLock, harness.MakeState().Hotkey);
    }

    [Fact]
    public void Scenario044()
    {
        AppState state = harness.MakeState();

        Assert.Null(state.HotkeyWarning);

        state.Hotkey = DictationHotkey.CapsLock;

        Assert.NotNull(state.HotkeyWarning);
    }

    /// <summary>
    /// The platform's own sentence reaches the warning, and only that sentence is the platform's.
    ///
    /// The warning is assembled inside AppCore, which is on no platform, so the one part of it
    /// that names a Settings app has to be handed in - see
    /// <see cref="AppEnvironment.HotkeySettingsHint"/>. A build that forgot to hand it in still
    /// warns, correctly, and just stops short of saying where to go; that is the fallback this
    /// pins alongside the wiring.
    /// </summary>
    [Fact]
    public void TheHotkeyWarningCarriesThePlatformSettingsHint()
    {
        harness.HotkeySettingsHint = HotkeyAdvice.WindowsSettingsHint;
        AppState state = harness.MakeState();

        state.Hotkey = DictationHotkey.CapsLock;

        Assert.EndsWith(
            HotkeyAdvice.WindowsSettingsHint,
            Assert.IsType<string>(state.HotkeyWarning),
            StringComparison.Ordinal);
    }

    /// <summary>Start at login is off until a person says otherwise.</summary>
    [Fact]
    public void LaunchAtLoginIsOffByDefaultAndReachesTheSystem()
    {
        AppState state = harness.MakeState();
        Assert.False(state.LaunchAtLogin);

        state.LaunchAtLogin = true;

        Assert.True(harness.LaunchAtLogin.IsEnabled);
    }

    /// <summary>A registration failure is reported and the switch snaps back to the truth.</summary>
    [Fact]
    public void LaunchAtLoginFailureIsVisible()
    {
        harness.LaunchAtLogin.Failure = new UnauthorizedAccessException("no");
        AppState state = harness.MakeState();

        state.LaunchAtLogin = true;

        Assert.False(state.LaunchAtLogin);
        Assert.Contains("login item", state.LastNotice?.Message);
    }

    // MARK: - Dictionary

    [Fact]
    public void Scenario045()
    {
        AppState state = harness.MakeState();

        state.AddReplacement("Sentry", "Sentry");

        Assert.Equal(["Sentry"], state.Replacements.Select(replacement => replacement.Written));
        Assert.Equal(["Sentry"], harness.MakeState().Replacements.Select(replacement => replacement.Written));
    }

    [Fact]
    public void Scenario046()
    {
        AppState state = harness.MakeState();

        state.AddReplacement("   ", "Sentry");
        state.AddReplacement("sentry", " ");

        Assert.Empty(state.Replacements);
    }

    /// <summary>
    /// An unread dictionary blocks the whole write path.
    ///
    /// It used to turn into an empty array, and the very first edit would overwrite the file with
    /// it: the person lost everything they had accumulated, silently and irreversibly.
    /// </summary>
    [Fact]
    public void Scenario047()
    {
        const string broken = "{this is not json";
        WriteReplacementsFile(broken);

        AppState state = harness.MakeState();
        Assert.False(state.IsDictionaryEditable);
        Assert.NotNull(state.DictionaryProblem);

        state.AddReplacement("Sentry", "Sentry");

        Assert.Empty(state.Replacements);
        Assert.Equal(broken, File.ReadAllText(ReplacementsPath));
    }

    [Fact]
    public async Task Scenario048()
    {
        WriteReplacementsFile("{this is not json");

        _ = harness.MakeState();

        await TestWait.For(
            "the message reached the panel",
            () => Overlay.Notices.Any(notice => notice.Message.Contains("replacement dictionary")));
    }

    [Fact]
    public void Scenario049()
    {
        const string future = """{"version": 99, "items": []}""";
        WriteReplacementsFile(future);

        AppState state = harness.MakeState();
        state.AddReplacement("future", "Future");

        Assert.False(state.IsDictionaryEditable);
        Assert.Equal(future, File.ReadAllText(ReplacementsPath));
    }

    [Fact]
    public void Scenario050()
    {
        AppState state = harness.MakeState();
        state.AddReplacement("Sentry", "Sentry");
        state.AddReplacement("deploy", "deploy");

        state.RemoveReplacements([0]);

        Assert.Equal(["deploy"], state.Replacements.Select(replacement => replacement.Written));
        Assert.Equal(["deploy"], harness.MakeState().Replacements.Select(replacement => replacement.Written));
    }

    /// <summary>The dictionary travels as a file, and an import never destroys.</summary>
    [Fact]
    public void DictionaryRoundTripsThroughTheTransferFile()
    {
        AppState state = harness.MakeState();
        state.AddReplacement("kubectl", "kubectl");
        byte[] exported = state.ExportedDictionary();

        AppState fresh = harness.MakeState();
        fresh.RemoveReplacements([0]);
        fresh.ImportDictionary(exported);

        Assert.Equal(["kubectl"], fresh.Replacements.Select(replacement => replacement.Written));
        Assert.Contains("Imported 1 phrase", fresh.LastNotice?.Message);
    }

    [Fact]
    public async Task Scenario062()
    {
        harness.Transcription.Text = "Включи фичер флэк на staging";
        AppState state = await ReadyState();
        Assert.Empty(state.Replacements);

        await DictateOnce(state);

        Assert.Equal("Включи feature flag на staging", harness.Inserter.InsertedTexts[^1]);
    }

    [Fact]
    public async Task Scenario063()
    {
        harness.Transcription.Text = "фичер флэк";
        AppState state = await ReadyState();
        state.AddReplacement("фичер флэк", "launch switch");

        await DictateOnce(state);

        Assert.Equal("Launch switch", harness.Inserter.InsertedTexts[^1]);
    }

    [Fact]
    public async Task Scenario064()
    {
        AppState state = await ReadyState();

        await DictateOnce(state);

        Assert.NotNull(state.LastSpeed);
        Assert.True(Overlay.DismissCount > 0);
    }

    // MARK: - Cleaning

    /// <summary>
    /// Recordings that survived a crash are tidied up quietly: header repaired, moved to
    /// safekeeping, nothing announced.
    /// </summary>
    [Fact]
    public async Task Scenario052()
    {
        string orphan = Path.Combine(harness.Paths.Takes(), "take-old.wav");
        AbandonedWav.Write(orphan, sampleBytes: 32_000);

        _ = harness.MakeState();
        await TestWait.For("the take moved into safekeeping", () => harness.RecoveryFiles().Count > 0);

        Assert.False(File.Exists(orphan));
        IReadOnlyList<string> kept = harness.RecoveryFiles();
        Assert.Single(kept);
        byte[] repaired = File.ReadAllBytes(kept[0]);
        Assert.Equal(32_000u, BitConverter.ToUInt32(repaired, 40));
        Assert.Empty(Overlay.Notices);
    }

    /// <summary>A leftover from a previous launch stays kept — silently.</summary>
    [Fact]
    public async Task LeftoverRecordingIsNotAnnouncedOnEveryLaunch()
    {
        string leftover = Path.Combine(harness.Paths.AudioRecovery(), "recording-old.wav");
        File.WriteAllBytes(leftover, new byte[64_000]);

        AppState state = harness.MakeState();
        await Task.Delay(200);

        Assert.True(File.Exists(leftover));
        Assert.Empty(Overlay.Notices);
        Assert.Null(state.LastNotice);
    }

    /// <summary>
    /// A fragment shorter than the recognition minimum does not survive launch. An accidental key
    /// press killed together with the app is not "a recording after a failure".
    /// </summary>
    [Fact]
    public async Task TooShortFragmentDoesNotBecomeALaunchError()
    {
        string blip = Path.Combine(harness.Paths.Takes(), "take-blip.wav");
        AbandonedWav.Write(blip, sampleBytes: 3200);

        AppState state = harness.MakeState();
        await TestWait.For("the fragment was swept", () => !File.Exists(blip));

        Assert.Empty(harness.RecoveryFiles());
        Assert.Empty(Overlay.Notices);
        Assert.Null(state.LastNotice);
    }

    // MARK: - Helpers

    private string ReplacementsPath => Path.Combine(harness.Paths.Support(), "replacements.json");

    private void WriteReplacementsFile(string content) =>
        File.WriteAllText(ReplacementsPath, content);

    private async Task<AppState> ReadyState()
    {
        harness.InstallModelMarker();
        AppState state = harness.MakeState();
        await state.RefreshModelState();
        Assert.True(state.IsDictationReady);
        return state;
    }

    /// <summary>One complete dictation: pressed, waited for recording, released, waited for text.</summary>
    private async Task DictateOnce(AppState state)
    {
        Monitor.OnPress!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);
        Monitor.OnRelease!();
        await TestWait.For("the text was produced", () => state.LastDictationValue is not null);
    }

    /// <summary>
    /// Wait for a message on the panel. The explanation goes there through a task, and right
    /// after the press it is not there yet.
    /// </summary>
    private Task WaitForNotice(string fragment) =>
        TestWait.For(
            $"the notice containing '{fragment}'",
            () => Overlay.Notices.Any(notice => notice.Message.Contains(fragment, StringComparison.Ordinal)));
}

internal static class TestWait
{
    public static async Task For(string what, Func<bool> condition, int timeoutMilliseconds = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) { return; }
            await Task.Delay(2).ConfigureAwait(false);
        }

        Assert.Fail($"didn't wait: {what}");
    }
}
