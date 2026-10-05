using LocalASR;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The bar a first run has to clear, written down so a machine can check it.
///
/// A person installs OpenRamble, and without ever quitting it reaches working dictation -
/// however long they take, in whatever order they grant the permission and download the model,
/// and whatever the idle rule decides about memory while they are away.
///
/// So at every instant the setup step is allowed to be in exactly two conditions: it can
/// advance, or it is blocked by something genuinely in motion, which will finish on its own and
/// then let it advance. These tests check both with one measurement, because in this harness
/// every real cost is instant: the engine answers immediately and the model is already on disk.
/// Anything genuinely in motion therefore finishes inside the budget, and a screen still blocked
/// at the end of it was waiting for work nobody was doing - "Model ready" over "Getting the model
/// ready…", with the Continue button greyed out and only quitting to fix it.
///
/// Nothing here mocks the thing under test. The screen is assembled from the same two calls the
/// onboarding window makes - <see cref="ModelStatus.Make"/> for the card and
/// <see cref="OnboardingGate.BlockReason"/> for the footer and the button - over a real
/// <see cref="AppState"/> driven through the real first-run sequence.
/// </summary>
/// <remarks>
/// The macOS "the worker stops being ready during setup" scenario has no counterpart: recognition
/// here is in-process.
/// </remarks>
public sealed class SetupScreenBarTests : IDisposable
{
    private readonly AppHarness harness = new();

    public void Dispose() => harness.Dispose();

    // MARK: - Awkward orderings

    /// <summary>
    /// The long stay: the setup window is open longer than the idle timer.
    ///
    /// Reopened setup is ordinary - "Finish Setup…" in the menu puts this exact screen back in
    /// front of a person whose settings already say onboarding was completed once. The idle-unload
    /// countdown runs against a screen where nobody dictates, so it always wins, and the engine's
    /// stated comeback is a key press that this screen is the reason nobody makes.
    /// </summary>
    [Fact]
    public async Task SetupAdvancesAfterTheIdleTimerUnloadsTheEngineOnTheSetupScreen()
    {
        harness.Settings.Write(new AppSettings { OnboardingCompletedRevision = OnboardingRevision.Current });
        harness.IdleUnloadDelayOverride = TimeSpan.FromMilliseconds(20);
        harness.WarmUpEngine = new CountingASREngine();
        harness.InstallModelMarker();

        AppState state = harness.MakeState();
        var screen = new SetupScreen(state);
        await TestWait.For("the engine loads", () => state.IsEngineReady);

        // The person is reading the setup screen. Nothing is dictated.
        await TestWait.For("the idle timer gives the memory back", () => !state.IsEngineReady);

        await AssertSetupClearsTheBar(screen);
    }

    /// <summary>
    /// The permission granted first, the model afterwards - the other order.
    ///
    /// Readiness does not only arrive from the two events that start preparation. It also arrives
    /// from a plain look at the disk, and the standing rule has to hold for readiness however it
    /// arrives.
    /// </summary>
    [Fact]
    public async Task SetupAdvancesWhenReadinessArrivesFromADiskRefresh()
    {
        var engine = new CountingASREngine();
        harness.WarmUpEngine = engine;
        // Nothing on disk: this run starts before the download, exactly as a wiped machine does.

        AppState state = harness.MakeState();
        var screen = new SetupScreen(state);
        await Task.Delay(100);
        Assert.False(state.IsEngineReady, "nothing to prepare before the model exists");
        Assert.Equal(0, engine.ModelLoads);
        Assert.Equal(
            "Download the model first — without it there is nothing to recognize with.",
            screen.Footer);

        harness.InstallModelMarker();
        await state.RefreshModelState();
        Assert.True(state.ModelStateValue.IsReady, "the files are usable now");

        await AssertSetupClearsTheBar(screen);
    }

    /// <summary>
    /// The whole first run in one sequence, in the order it actually happens: launch with nothing
    /// on disk; the model arrives; the microphone is granted after a long trip to the system
    /// settings. Each step is legitimate. The end of the sequence must be a screen that lets the
    /// person through.
    /// </summary>
    [Fact]
    public async Task TheWholeFirstRunReachesAnAdvancingSetupScreen()
    {
        harness.Permissions.MicrophoneGranted = false;
        harness.WarmUpEngine = new CountingASREngine();

        AppState state = harness.MakeState();
        var screen = new SetupScreen(state);
        await Task.Delay(100);

        harness.InstallModelMarker();
        await state.RefreshModelState();
        await Task.Delay(50);

        harness.Permissions.MicrophoneGranted = true;
        state.RefreshPermissions();

        await AssertSetupClearsTheBar(screen);
    }

    /// <summary>
    /// The one way out of a rejected model has to name what it will cost.
    ///
    /// The engine refusing the model is a legitimate block: the screen says so and offers a
    /// button, and the person can press it. But the button was labelled "Redownload Model — 0 MB",
    /// because the number is computed from what is missing from disk and nothing is missing.
    /// </summary>
    [Fact]
    public async Task TheRepairOfferedOnTheSetupScreenNamesItsRealDownload()
    {
        harness.WarmUpEngine = new FailingASREngine();
        harness.InstallModelMarker();

        AppState state = harness.MakeState();
        var screen = new SetupScreen(state);
        await TestWait.For("the engine rejects the intact files", () => state.ModelStateValue.RequiresRepair);

        Assert.Equal("The model is damaged. Redownload it explicitly.", screen.Footer);
        Assert.Equal([ModelStatus.Action.Repair], screen.Card.Actions);
        Assert.Equal(
            $"Redownload Model — {state.FullModelDownloadMegabytes} MB",
            screen.Card.TitleFor(ModelStatus.Action.Repair));
        Assert.True(state.FullModelDownloadMegabytes > 0, "the repair fetches the model again, and the button says how much that is");
    }

    // MARK: - The card and the footer describe one instant

    /// <summary>
    /// Two views of one moment must not say opposite things about it.
    ///
    /// The card and the gate used to be handed the same two facts and reach opposite conclusions:
    /// with a ready model, a cold engine and no preparation running, the card said the model is
    /// ready and resting, and the footer said the model is being got ready. The gate no longer
    /// reads the engine, so whatever the card reports, the footer has nothing to contradict it with.
    /// </summary>
    [Fact]
    public void TheCardNeverCallsTheModelReadyWhileTheFooterAsksToWaitForIt()
    {
        var ready = new ModelState.Ready("/tmp/model");
        foreach (bool engineReady in new[] { false, true })
        {
            foreach (bool preparing in new[] { false, true })
            {
                ModelStatus card = ModelStatus.Make(
                    ready,
                    ModelStatus.Place.Onboarding,
                    EnginePreparationState.Make(EnginePreparationState.Phase.Idle, TimeSpan.Zero),
                    isEngineReady: engineReady,
                    isPreparingEngine: preparing);
                string? footer = OnboardingGate.BlockReason(OnboardingStep.Setup, new OnboardingConditions(true, ready));
                bool footerWaitsOnTheEngine = footer is not null && SetupScreen.EngineWaitSentences.Contains(footer);

                Assert.False(
                    card.ToneValue == ModelStatus.Tone.Success && footerWaitsOnTheEngine,
                    $"isEngineReady={engineReady} isPreparingEngine={preparing}: the card says “{card.Title}” "
                    + $"while the footer says “{footer}”. One screen, one instant, two opposite claims.");
            }
        }
    }

    /// <summary>
    /// The menu is a first-run surface too, and it must not contradict the card. Whatever the menu
    /// says about the model is said by the card type, which is handed all three facts.
    /// </summary>
    [Fact]
    public void TheMenuNeverNarratesPreparationThatIsNotHappening()
    {
        var ready = new ModelState.Ready("/tmp/model");

        ModelStatus resting = ModelStatus.Make(
            ready,
            ModelStatus.Place.Settings,
            EnginePreparationState.Make(EnginePreparationState.Phase.Idle, TimeSpan.Zero),
            isEngineReady: false,
            isPreparingEngine: false);
        Assert.Null(resting.SetupLine);

        ModelStatus loaded = ModelStatus.Make(ready, ModelStatus.Place.Settings, isEngineReady: true);
        Assert.Null(loaded.SetupLine);

        // The one moment the menu does speak about preparation is the one where preparation is
        // running, and then it names the step it is on.
        ModelStatus preparing = ModelStatus.Make(
            ready,
            ModelStatus.Place.Settings,
            EnginePreparationState.Make(EnginePreparationState.Phase.LoadingRecognizer, TimeSpan.FromSeconds(1)),
            isEngineReady: false,
            isPreparingEngine: true);
        Assert.Equal("Preparing the model — Step 1 of 3 · Loading the recognizer… 1 s", preparing.SetupLine);

        // Everything the person must act on still has its line and its button.
        ModelStatus missing = ModelStatus.Make(new ModelState.NotInstalled(), ModelStatus.Place.Settings);
        Assert.Equal("Model not installed", missing.SetupLine);
        Assert.Equal([ModelStatus.Action.Install], missing.Actions);
    }

    // MARK: - The bar itself

    /// <summary>Condition A or condition B, measured as one thing.</summary>
    private static async Task AssertSetupClearsTheBar(SetupScreen screen)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            // Both, in one wait: the screen can open the way through the instant the files
            // become usable while the first engine load is still running - which is legitimate,
            // the try-out says so in words and it ends by itself.
            if (screen.CanAdvance && screen.State.IsDictationReady) { break; }
            await Task.Delay(5);
        }

        Assert.True(
            screen.CanAdvance,
            $"The setup screen spent three seconds unable to advance, waiting for work nobody is doing.\n{screen.Report}");
        Assert.False(
            screen.CardAndFooterContradict,
            $"The card and the footer describe the same instant and disagree about it.\n{screen.Report}");
        // Letting them past the screen is only half of a first run. The next thing they do is
        // hold the key, and the app's own readiness has to accept it.
        Assert.True(
            screen.State.IsDictationReady,
            $"The setup screen let them through to a try-out that will refuse the key press.\n{screen.Report}");
    }

    /// <summary>
    /// The setup step as the person sees it: one card, one footer, one button. Built from the two
    /// calls the onboarding window makes, so the test cannot pass on a screen the app would not
    /// draw.
    /// </summary>
    private sealed class SetupScreen(AppState state)
    {
        /// <summary>
        /// The two sentences that waited on the app rather than on the person. The gate no longer
        /// has an engine to wait for, so these are kept as the shape of the failure: if a footer
        /// ever says one of them again, the requirement has come back.
        /// </summary>
        public static readonly HashSet<string> EngineWaitSentences =
        [
            "Wait for the model to finish preparing for first use.",
            "Getting the model ready…",
            "The model is getting ready for this PC — usually 20–40 seconds, and only once.",
        ];

        public AppState State => state;

        public ModelStatus Card => ModelStatus.Make(
            state.ModelStateValue,
            ModelStatus.Place.Onboarding,
            state.EnginePreparation,
            state.RemainingDownloadMegabytes,
            state.IsEngineReady,
            state.IsPreparingEngine);

        /// <summary>Read off the app by the app's own reader, never assembled here.</summary>
        public string? Footer =>
            OnboardingGate.BlockReason(OnboardingStep.Setup, new OnboardingConditions(state, trialSucceeded: true));

        public bool CanAdvance => Footer is null;

        public bool CardAndFooterContradict =>
            Card.ToneValue == ModelStatus.Tone.Success && Footer is string footer && EngineWaitSentences.Contains(footer);

        public string Report =>
            $"card: “{Card.Title}” — “{Card.Detail}”\n"
            + $"footer: “{Footer ?? "null"}” Continue {(CanAdvance ? "enabled" : "disabled")}\n"
            + $"modelState={state.ModelStateValue} isEngineReady={state.IsEngineReady} "
            + $"isPreparingEngine={state.IsPreparingEngine} hasEngineBeenReady={state.HasEngineBeenReady} "
            + $"microphone={state.MicrophoneGranted} preparation={state.EnginePreparation.PhaseValue}";
    }
}
