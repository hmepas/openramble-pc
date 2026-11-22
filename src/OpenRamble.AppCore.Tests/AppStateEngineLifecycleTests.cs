using DictationCore;
using LocalASR;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The engine's life after upstream 0.8–0.16: the person's own idle-unload control, the key
/// press that wakes a resting engine, a preparation that keeps trying rather than giving up, and
/// the standing rule that a ready model with a cold engine is a state the app leaves by itself.
/// </summary>
/// <remarks>
/// Ported from the macOS <c>AppStateTests</c> additions and <c>FirstLoadNeverStrandsTests</c>.
/// The worker-readiness scenarios ("the worker died, the app hears about it") have no
/// counterpart: recognition here is in-process, and an engine that has nothing to lose its
/// readiness to cannot be told it did.
/// </remarks>
public sealed class AppStateEngineLifecycleTests : IDisposable
{
    private readonly AppHarness harness = new();

    private FakeHotkeyMonitor Monitor => harness.Monitor;

    public void Dispose() => harness.Dispose();

    private void FinishSetup() =>
        harness.Settings.Write(new AppSettings { OnboardingCompletedRevision = OnboardingRevision.Current });

    // MARK: - Retrying

    /// <summary>One transient failure is retried exactly once, and nobody is asked to redownload.</summary>
    [Fact]
    public async Task ATransientLoadFailureIsRetriedWithoutRedownloadingVerifiedModels()
    {
        harness.InstallModelMarker();
        var engine = new ScriptedLoadEngine([ScriptedLoadEngine.Outcome.Fails]);
        harness.WarmUpEngine = engine;
        harness.EngineWarmupRetryDelay = TimeSpan.FromMilliseconds(5);
        AppState state = harness.MakeState();

        await TestWait.For("the engine became ready", () => state.IsEngineReady);

        Assert.Equal(2, engine.Attempts);
        Assert.Equal(1, engine.WarmUps);
        Assert.True(state.ModelStateValue.IsReady, "a transient failure is not model corruption");
        Assert.DoesNotContain(harness.Overlay.Notices, notice => notice.Message.Contains("redownload", StringComparison.Ordinal));
        await TestWait.For("the app stopped saying it is working", () => !state.IsPreparingEngine);
    }

    /// <summary>
    /// Preparation never gives up and never blames the verified files.
    ///
    /// This used to stop after a fixed budget and tell the person to restart the app - the state a
    /// first install could land in for good. Now the attempts continue, so the only honest report
    /// is "still preparing".
    /// </summary>
    [Fact]
    public async Task PersistentFailuresKeepRetryingWithoutBlamingTheModel()
    {
        harness.InstallModelMarker();
        var engine = new ScriptedLoadEngine([], fallback: ScriptedLoadEngine.Outcome.Fails);
        harness.WarmUpEngine = engine;
        harness.EngineWarmupRetryDelay = TimeSpan.FromMilliseconds(2);
        AppState state = harness.MakeState();

        await TestWait.For("attempts continue past any fixed budget", () => engine.Attempts >= 4);

        Assert.False(state.IsEngineReady);
        Assert.True(state.IsPreparingEngine, "the interface stays honestly busy");
        Assert.True(state.ModelStateValue.IsReady, "a load failure is not model corruption");
        Assert.DoesNotContain(harness.Overlay.Notices, notice => notice.Message.Contains("redownload", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Overlay.Notices, notice => notice.Message.Contains("couldn't become ready", StringComparison.Ordinal));
        state.DeleteModel();
    }

    /// <summary>
    /// "Nothing happened" ends an attempt, never the preparation.
    ///
    /// A cancelled load is one moment's answer, not the world's. With the asymmetry upstream had,
    /// the app gave up on the second one and only quitting helped, because the standing rule fires
    /// on readiness and readiness had not changed.
    /// </summary>
    [Fact]
    public async Task AFirstLoadCancelledTwiceIsStillFinishedByTheAppItself()
    {
        harness.InstallModelMarker();
        var engine = new ScriptedLoadEngine(
        [
            ScriptedLoadEngine.Outcome.Cancelled,
            ScriptedLoadEngine.Outcome.Cancelled,
            ScriptedLoadEngine.Outcome.Succeeds,
        ]);
        harness.WarmUpEngine = engine;
        harness.EngineWarmupRetryDelay = TimeSpan.FromMilliseconds(5);
        AppState state = harness.MakeState();

        await TestWait.For("the engine finishes its first load", () => state.IsEngineReady);

        Assert.True(engine.Attempts >= 3, "the preparation went past the cancellation that used to end it");
        Assert.True(state.HasEngineBeenReady, "the first dictation has something to recognize with");
        await TestWait.For("the app has stopped saying it is working", () => !state.IsPreparingEngine);
    }

    /// <summary>
    /// The app must not be left telling a person about a load it abandoned.
    ///
    /// While the ladder waits between attempts it holds the "preparing" indicator on purpose: one
    /// continuous sentence for one continuous preparation. Deleting the model revokes the ladder
    /// mid-wait; the indicator has to come down with it, or the standing rule can never start a
    /// preparation for the model installed next.
    /// </summary>
    [Fact]
    public async Task DeletingTheModelMidRetryStopsTheAppNarratingPreparation()
    {
        harness.InstallModelMarker();
        harness.EngineWarmupRetryDelay = TimeSpan.FromSeconds(1);
        harness.WarmUpEngine = new ScriptedLoadEngine([], fallback: ScriptedLoadEngine.Outcome.Fails);
        AppState state = harness.MakeState();
        await TestWait.For("the first attempt fails and the ladder takes over", () => state.IsPreparingEngine);
        await TestWait.For(
            "the ladder is waiting rather than attempting",
            () => state.EnginePreparation.PhaseValue == EnginePreparationState.Phase.LoadingRecognizer);

        state.DeleteModel();
        await TestWait.For("the files are gone", () => state.ModelStateValue is ModelState.NotInstalled);
        // The revoked ladder is asleep for a whole second; give it its wake-up.
        await Task.Delay(1200);

        Assert.False(
            state.IsPreparingEngine,
            "The app says it is preparing a model that has been deleted. Nothing will clear this.");
        Assert.Equal(EnginePreparationState.Phase.Idle, state.EnginePreparation.PhaseValue);
    }

    /// <summary>
    /// The invariant behind the tests above, stated once and watched.
    ///
    /// A ready model with an engine that has never loaded is the app's own debt, and it is the
    /// only state in which every surface lies at once. The app is allowed to pass through it and
    /// never to rest in it. Here every single load ends without an answer, forever - the harshest
    /// script there is - and the app must go on visibly working rather than fall quiet.
    /// </summary>
    [Fact]
    public async Task TheAppNeverRestsOnAReadyModelWithAnEngineThatHasNeverLoaded()
    {
        harness.InstallModelMarker();
        var engine = new ScriptedLoadEngine([], fallback: ScriptedLoadEngine.Outcome.Cancelled);
        harness.WarmUpEngine = engine;
        harness.EngineWarmupRetryDelay = TimeSpan.FromMilliseconds(5);
        AppState state = harness.MakeState();
        await TestWait.For("the model is read off the disk", () => state.ModelStateValue.IsReady);

        int quietSamples = 0;
        int worstRun = 0;
        for (int sample = 0; sample < 200; sample++)
        {
            bool owesAReadyEngine = state.ModelStateValue.IsReady && !state.IsEngineReady && !state.HasEngineBeenReady;
            quietSamples = owesAReadyEngine && !state.IsPreparingEngine ? quietSamples + 1 : 0;
            worstRun = Math.Max(worstRun, quietSamples);
            await Task.Delay(10);
        }

        // A preparation that never succeeds never stops, which is the whole point; taking the
        // model away is the app's own way of revoking one.
        state.DeleteModel();

        Assert.True(worstRun < 20, $"the app spent {worstRun * 10} ms owing a ready engine with nothing preparing one");
        Assert.True(engine.Attempts > 2, "it kept trying, rather than merely looking busy");
    }

    // MARK: - Deletion

    /// <summary>
    /// Deletion revokes a load that is past the point where cancelling it would help, so its
    /// completion can neither publish Ready nor queue a retry after the files are gone.
    /// </summary>
    [Fact]
    public async Task DeletingTheModelRevokesACancellationDeafLoadBeforeReadyCanRepublish()
    {
        harness.InstallModelMarker();
        var engine = new SlowFirstLoadEngine();
        harness.WarmUpEngine = engine;
        AppState state = harness.MakeState();
        await TestWait.For("the load started", () => engine.LoadStarted);

        state.DeleteModel();
        Assert.IsType<ModelState.Deleting>(state.ModelStateValue);
        Assert.False(state.IsDictationReady);
        await TestWait.For("the engine was told to unload", () => engine.Unloads == 1);

        engine.FinishLoading();
        await TestWait.For("the files are gone", () => state.ModelStateValue is ModelState.NotInstalled);
        await Task.Delay(50);

        Assert.Equal(0, engine.WarmUps);
        Assert.False(state.IsEngineReady);
        Assert.False(state.ModelStateValue.IsReady);
        Assert.False(state.IsPreparingEngine);
    }

    /// <summary>The delete confirmation names what deleting will cost, not what is missing.</summary>
    [Fact]
    public async Task TheDeleteConfirmationNamesTheWholeDownloadItWillCost()
    {
        harness.InstallModelMarker();
        AppState state = harness.MakeState();

        await state.RefreshModelState();

        Assert.Equal(0, state.RemainingDownloadMegabytes);
        // Read from the bundled manifest, so it needs no fallback and no remembered constant.
        int manifest = (int)((ModelManifest.Bundled().TotalByteCount + 500_000) / 1_000_000);
        Assert.Equal(manifest, state.FullModelDownloadMegabytes);
    }

    // MARK: - Idle unload

    /// <summary>
    /// After an hour without dictation the engine's memory goes back, and the next key press
    /// brings it back under the voice.
    /// </summary>
    [Fact]
    public async Task IdleUnloadFreesTheEngineAfterTheCountdown()
    {
        harness.InstallModelMarker();
        FinishSetup();
        var engine = new CountingASREngine();
        harness.WarmUpEngine = engine;
        harness.IdleUnloadDelayOverride = TimeSpan.FromMilliseconds(40);
        AppState state = harness.MakeState();
        await TestWait.For("the engine warmed up", () => state.IsEngineReady);

        await TestWait.For("the countdown returned the memory", () => !state.IsEngineReady);
        Assert.Equal(1, engine.Unloads);
        Assert.True(state.IsDictationReady, "an unloaded engine must not block the press");

        Monitor.Tap();
        await TestWait.For("the comeback rode the next key press", () => state.IsEngineReady);
        Assert.Equal(2, engine.ModelLoads);
    }

    /// <summary>The key press is the earliest reload trigger: it fires before any readiness guard can swallow it.</summary>
    [Fact]
    public async Task KeyDownRewarmsAColdEngine()
    {
        harness.InstallModelMarker();
        FinishSetup();
        var engine = new CountingASREngine();
        harness.WarmUpEngine = engine;
        AppState state = harness.MakeState();
        await TestWait.For("the engine warmed up", () => state.IsEngineReady);
        int baseline = engine.Inferences;

        state.IdleUnloadDelayOverride = TimeSpan.Zero;
        await TestWait.For("the engine rested", () => !state.IsEngineReady);
        // A zero countdown would put the rewarmed engine straight back to rest; the question
        // here is only whether the press wakes it.
        state.IdleUnloadDelayOverride = null;

        Monitor.OnHotkeyDown!();
        await TestWait.For("the press rewarmed the resting engine", () => state.IsEngineReady);
        Assert.Equal(2, engine.ModelLoads);
        Assert.True(engine.Inferences > baseline, "a reloaded generation is warmed before readiness");
    }

    /// <summary>
    /// Re-arming the countdown from several threads at once leaves exactly one countdown.
    ///
    /// Every state change re-arms it, and state changes arrive from the controller, the warm-up
    /// and the UI on whatever thread they happen on. A re-arm that cancelled, checked and then
    /// armed in three separate steps let two of them each cancel nothing and each arm one; the
    /// one overwritten kept running, nobody could cancel it, and an hour after it was armed it
    /// unloaded the engine however recently the person had dictated.
    /// </summary>
    [Fact]
    public async Task ConcurrentReschedulesLeaveNoOrphanedCountdown()
    {
        harness.InstallModelMarker();
        FinishSetup();
        var engine = new CountingASREngine();
        harness.WarmUpEngine = engine;
        AppState state = harness.MakeState();
        await TestWait.For("the engine warmed up", () => state.IsEngineReady);

        TimeSpan shortCountdown = TimeSpan.FromMilliseconds(500);
        using var start = new Barrier(8);
        // Threads of their own: eight pool threads parked on a barrier starve the pool every
        // other suite running alongside depends on.
        Task[] racers = [.. Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(
            () =>
            {
                start.SignalAndWait();
                for (int i = 0; i < 200; i++) { state.IdleUnloadDelayOverride = shortCountdown; }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default))];
        await Task.WhenAll(racers);
        // Back to the hour: this re-arm replaces the one live countdown, and should be the only
        // one left.
        state.IdleUnloadDelayOverride = null;

        await Task.Delay(shortCountdown * 2);
        Assert.True(state.IsEngineReady, "an orphaned short countdown unloaded the engine");
        Assert.True(state.IsAwaitingIdleUnload, "an orphaned countdown disarmed the live one");
        Assert.Equal(0, engine.Unloads);
    }

    /// <summary>
    /// The engine stays an hour after the last dictation, for everyone. Every other residency
    /// test shortens the countdown, so this is the one that notices if the hour itself changes.
    /// </summary>
    [Fact]
    public void TheEngineStaysLoadedForAnHourAfterTheLastDictation()
    {
        Assert.Equal(TimeSpan.FromSeconds(60 * 60), AppState.EngineIdleUnloadDelay);
    }

    /// <summary>
    /// The "Unload model" picker is gone. Somebody who once chose "Never" gets the same hour as
    /// everyone else, and one reload after an idle hour.
    /// </summary>
    [Fact]
    public async Task ALegacyNeverSettingNoLongerKeepsTheEngineForever()
    {
        harness.InstallModelMarker();
        FinishSetup();
        string settings = Path.Combine(harness.Paths.Support(), "settings.json");
        string stored = File.ReadAllText(settings);
        File.WriteAllText(settings, stored.TrimEnd().TrimEnd('}').TrimEnd() + ",\n  \"modelUnloadTimeout\": \"never\"\n}\n");
        harness.WarmUpEngine = new CountingASREngine();
        harness.IdleUnloadDelayOverride = TimeSpan.FromMilliseconds(30);
        AppState state = harness.MakeState();
        await TestWait.For("the engine warmed up", () => state.IsEngineReady);

        await TestWait.For("a stored Never is ignored", () => !state.IsEngineReady);
    }

    /// <summary>
    /// The reported first-run stall, reproduced.
    ///
    /// Setup takes a trip through the system settings, and that trip can outlast the idle-unload
    /// timer. The engine then gave its memory back and waited for a key press that nobody makes on
    /// the setup screen. Setup is not idleness.
    /// </summary>
    [Fact]
    public async Task IdleUnloadNeverStrandsSetupBeforeOnboardingIsFinished()
    {
        harness.InstallModelMarker();
        harness.WarmUpEngine = new CountingASREngine();
        harness.IdleUnloadDelayOverride = TimeSpan.FromMilliseconds(30);
        // Onboarding is still open: the person has not finished setting up.
        AppState state = harness.MakeState();
        await TestWait.For("the engine warmed up", () => state.IsEngineReady);

        await Task.Delay(200);
        Assert.True(state.IsEngineReady, "setup is not idleness: the engine must still be there when the person returns");

        // Once setup is done, the courtesy applies again.
        state.OnboardingCompleted = true;
        // Re-arms the countdown, as the next dictation would.
        state.IdleUnloadDelayOverride = TimeSpan.FromMilliseconds(30);
        await TestWait.For("after setup, idle memory goes back as promised", () => !state.IsEngineReady);
    }

    // MARK: - The standing rule

    /// <summary>
    /// Readiness learned from the disk starts preparation, like any other route.
    ///
    /// A model that became usable while the app was already running - the download finishing, or
    /// the Settings window re-reading the disk - used to start nothing at all, and an engine that
    /// has never once been loaded is the one cold engine a key press cannot warm.
    /// </summary>
    [Fact]
    public async Task ReadinessLearnedFromTheDiskStartsPreparation()
    {
        var engine = new CountingASREngine();
        harness.WarmUpEngine = engine;
        // This run starts before the download, exactly as a wiped machine does.
        AppState state = harness.MakeState();
        await Task.Delay(100);
        Assert.Equal(0, engine.ModelLoads);

        // The files become usable, and the app learns it by looking at the disk.
        harness.InstallModelMarker();
        await state.RefreshModelState();

        await TestWait.For("a usable model with a cold engine is a state the app leaves by itself", () => state.IsEngineReady);
        Assert.True(state.HasEngineBeenReady);
        Assert.True(engine.ModelLoads > 0);
    }

    /// <summary>
    /// A ready engine must NOT be given a second inference when a key is pressed.
    ///
    /// Every key press used to run a full inference over a second of silence and the real
    /// dictation then waited for it, so every dictation was two inferences where one was needed.
    /// Warming still happens where it is earned - once after a model load, and once after the
    /// machine wakes.
    /// </summary>
    [Fact]
    public async Task AReadyEngineIsNotWarmedAgainOnEveryPress()
    {
        harness.InstallModelMarker();
        var engine = new CountingASREngine();
        harness.WarmUpEngine = engine;
        AppState state = harness.MakeState();
        await TestWait.For("the engine warmed up", () => state.IsEngineReady);
        int afterLoad = engine.Inferences;
        Assert.True(afterLoad >= 1, "a freshly loaded model is warmed once");

        Monitor.OnHotkeyDown!();
        Monitor.OnPress!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);
        Monitor.OnHotkeyUp!();
        Monitor.OnRelease!();
        await TestWait.For("dictation ended", () => state.DictationStateValue == DictationState.Idle);

        Assert.Equal(afterLoad, engine.Inferences);
    }

    // MARK: - Waking the model

    /// <summary>
    /// A take that ends while the model is still loading says so. The panel is the only feedback
    /// channel during dictation, and until now it said "Transcribing…" through the whole wait -
    /// calling a wait work is a small lie that becomes a large one on a short take. The
    /// announcement is retracted on every exit.
    /// </summary>
    [Fact]
    public async Task ATakeThatWaitsForTheModelSaysSoAndRetractsIt()
    {
        harness.InstallModelMarker();
        FinishSetup();
        var engine = new SlowFirstLoadEngine(holdFromLoad: 2);
        harness.WarmUpEngine = engine;
        AppState state = harness.MakeState();
        await TestWait.For("the engine warmed up", () => state.IsEngineReady);

        state.IdleUnloadDelayOverride = TimeSpan.Zero;
        await TestWait.For("the engine rested", () => !state.IsEngineReady);
        state.IdleUnloadDelayOverride = null;

        // The press starts the reload under the voice; the release finds it still running.
        Monitor.OnHotkeyDown!();
        Monitor.OnPress!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);
        Monitor.OnHotkeyUp!();
        Monitor.OnRelease!();

        await TestWait.For("the wait was announced", () => harness.Overlay.EngineWaits.Contains(true));
        Assert.Equal(DictationState.Transcribing, state.DictationStateValue);

        engine.FinishLoading();
        await TestWait.For("dictation ended", () => state.DictationStateValue == DictationState.Idle);
        Assert.Equal([true, false], harness.Overlay.EngineWaits);
        Assert.True(state.IsEngineReady);
    }

    // MARK: - The try-out step over a real load

    /// <summary>
    /// The whole window, over a real <see cref="AppState"/>, for the length of a real load.
    ///
    /// The engine here holds its first load open exactly as the one-time load after install does.
    /// Everything the try-out step shows is read off the app through the same two calls the
    /// window makes: while the load runs the screen names it, and when the load ends by itself
    /// the screen goes back to asking for the trial.
    /// </summary>
    [Fact]
    public async Task TheTryOutScreenNarratesTheFirstLoadAndStopsWhenItEnds()
    {
        harness.InstallModelMarker();
        var engine = new SlowFirstLoadEngine();
        harness.WarmUpEngine = engine;
        AppState state = harness.MakeState();
        await TestWait.For("the first load is visibly running", () => state.IsPreparingEngine);
        Assert.False(state.HasEngineBeenReady, "this is the load that has never finished");
        Assert.False(state.IsDictationReady, "a key press right now is refused - that is what the screen has to say");

        Assert.Equal(
            "The model is getting ready for this PC — usually 20–40 seconds, and only once.",
            TryOutFooter(state));
        Assert.Equal(
            $"Preparing the model — Step 1 of 3 · {state.EnginePreparation.Title}",
            TryOutStatusLine(state));

        // Nothing is asked of the person: the load finishes on its own.
        engine.FinishLoading();
        await TestWait.For("the engine finishes loading", () => state.IsEngineReady);

        Assert.Equal("Try dictation first, or press “Skip the try-out”.", TryOutFooter(state));
        Assert.Null(TryOutStatusLine(state));
        Assert.True(state.IsDictationReady);
    }

    /// <summary>The same card the setup step draws, asked for its one-line form.</summary>
    private static string? TryOutStatusLine(AppState state) =>
        ModelStatus.Make(
            state.ModelStateValue,
            ModelStatus.Place.Onboarding,
            state.EnginePreparation,
            state.RemainingDownloadMegabytes,
            state.IsEngineReady,
            state.IsPreparingEngine).SetupLine;

    /// <summary>Read off the app by the app's own reader, never assembled here.</summary>
    private static string? TryOutFooter(AppState state) =>
        OnboardingGate.BlockReason(OnboardingStep.TryIt, new OnboardingConditions(state, trialSucceeded: false));
}
