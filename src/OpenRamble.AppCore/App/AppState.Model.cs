using System.Diagnostics;
using DictationCore;
using LocalASR;

namespace OpenRamble.AppCore;

/// <summary>The model on disk, the engine in memory, and the seconds between them.</summary>
public sealed partial class AppState
{
    private string? engineDirectory;

    /// <summary>Is the model being installed right now?</summary>
    private bool isInstalling;

    /// <summary>
    /// An engine failure during warm-up. The files on disk are intact, so a disk inspection does
    /// not see it - the state is kept here until an explicit repair.
    /// </summary>
    private string? engineLoadFailure;

    /// <summary>Whether an engine recycle is already underway — one wedge, one cure.</summary>
    private bool isRecyclingEngine;

    /// <summary>
    /// When the engine last did real work. Decides whether a keypress warms it in parallel with
    /// the recording (see <see cref="EngineWarming"/>).
    /// </summary>
    private DateTimeOffset? lastEngineActivity;

    /// <summary>Consecutive warning-tier pressure events; one blip does nothing.</summary>
    private int consecutiveWarnings;

    /// <summary>When the last engine load completed — feeds the anti-thrash hold.</summary>
    private DateTimeOffset? lastLoadCompletedAt;

    /// <summary>The most recent tier the OS reported; rules between events use it.</summary>
    private MemoryPressureTier lastPressureTier = MemoryPressureTier.Normal;

    /// <summary>
    /// One-shot: armed only while a residency rule waits on a time boundary. At rest — normal
    /// pressure, or nothing pending — no timer exists.
    /// </summary>
    private Timer? residencyTimer;

    private Timer? preparationTimer;
    private DateTimeOffset? preparationStartedAt;

    // MARK: - Model

    public async Task RefreshModelState()
    {
        if (store is null) { return; }

        // While an installation is running, inspecting the disk would reset the state to "model
        // not installed": there is no ready marker yet. Progress would vanish from the screen
        // exactly when the person opened settings to look at it.
        lock (gate)
        {
            if (isInstalling) { return; }
        }

        ModelState main = await Task.Run(store.RefreshState).ConfigureAwait(false);
        ApplyModelState(main);
    }

    /// <remarks>
    /// The macOS build combines two stores here through <see cref="ModelPairState"/>. This port
    /// installs one model - the acoustic term hinter has no ONNX export - so the main store's
    /// state IS the state. Running it through the pair combiner with an absent second store would
    /// answer "not installed" forever.
    /// </remarks>
    private void ApplyModelState(ModelState main)
    {
        ModelStateValue = main;
        RemainingDownloadMegabytes = (int)((main.IsReady ? 0 : mainModelBytes) + 500_000) / 1_000_000;

        // A disk inspection cannot see an engine failure: the files are intact, but the model did
        // not come up. Until the person explicitly asks for a repair the refusal stays on screen -
        // otherwise the repair button disappears and dictation still does not work.
        string? failure;
        lock (gate) { failure = engineLoadFailure; }
        if (failure is not null && ModelStateValue.IsReady)
        {
            ModelStateValue = new ModelState.RepairRequired(failure);
        }

        if (transcriber is null)
        {
            IsEngineReady = ModelStateValue.IsReady;
        }
        else if (!ModelStateValue.IsReady)
        {
            IsEngineReady = false;
        }
    }

    /// <summary>Did a model ship inside this installation?</summary>
    public bool HasBundledModel =>
        bundledModelDirectory is { } bundle && Directory.Exists(bundle);

    public void InstallModel()
    {
        // Pressing again during the download must not start anything: the button lives on screen
        // until the first state arrives, and pressing it twice is easier than it sounds.
        if (store is null) { return; }
        lock (gate)
        {
            if (isInstalling) { return; }
            isInstalling = true;
        }

        IsEngineReady = false;

        // An engine failure leaves the files on disk "intact" - the store would say ready. An
        // explicit repair is required to keep the promise of the message and re-download instead
        // of silently repeating the warm-up.
        bool engineRejectedModels;
        lock (gate)
        {
            engineRejectedModels = engineLoadFailure is not null;
            // An explicit human command opens a new attempt: the previous failure no longer
            // holds, and a fresh installation will warm up again.
            engineLoadFailure = null;
        }

        _ = Task.Run(async () =>
        {
            ModelState latest = store.RefreshState();
            if (!latest.IsReady || engineRejectedModels)
            {
                using var monitor = new CancellationTokenSource();
                Task watching = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (ModelState state in store.States(monitor.Token).ConfigureAwait(false))
                        {
                            ApplyModelState(state);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                });

                bool repair = latest.IsReady || latest.RequiresRepair;
                if (bundledModelDirectory is { } bundle && Directory.Exists(bundle))
                {
                    // Same verification, same atomic promotion as a download - only the bytes come
                    // from next to the executable instead of the network.
                    await store.ImportModel(bundle, force: repair).ConfigureAwait(false);
                }
                else if (repair) { await store.Repair().ConfigureAwait(false); }
                else { await store.Install().ConfigureAwait(false); }

                await monitor.CancelAsync().ConfigureAwait(false);
                await watching.ConfigureAwait(false);
            }

            // Preserve the store's terminal state. Scanning the install directory after a failure
            // finds no ready marker and used to erase the detailed error with "not installed"
            // before the user could read it or press Retry.
            ApplyModelState(store.CurrentState());
            lock (gate) { isInstalling = false; }

            // The first load reads the weights and plans the run, and that takes seconds. Do it
            // now so the person does not wait at the moment of their first dictation.
            if (ModelStateValue.IsReady) { await WarmUpEngine().ConfigureAwait(false); }
        });
    }

    public void CancelModelInstall()
    {
        lock (gate)
        {
            if (!isInstalling) { return; }
        }

        store?.CancelInstall();
    }

    public void DeleteModel()
    {
        // A dictation is running - the model is at work. Taking it away means losing what has
        // already been said and showing a loading error instead.
        //
        // The check comes first on purpose: a person needs an answer to their own press, not
        // silence because something inside is missing.
        if (DictationStateValue != DictationState.Idle)
        {
            Notify(new DictationNotice(
                DictationNoticeKind.Warning,
                "Dictation is in progress. Wait for it to finish."));
            return;
        }

        if (store is null) { return; }

        _ = Task.Run(async () =>
        {
            IsEngineReady = false;
            lock (gate) { engineLoadFailure = null; }
            if (transcriber is not null) { await transcriber.Unload().ConfigureAwait(false); }
            await store.Delete().ConfigureAwait(false);
            await RefreshModelState().ConfigureAwait(false);
        });
    }

    // MARK: - Engine

    private async Task WarmUpEngine()
    {
        if (transcriber is null || store is null || engineDirectory is null) { return; }
        if (store.CurrentState() is not ModelState.Ready) { return; }

        IsEngineReady = false;
        IsPreparingEngine = true;
        BeginPreparationCountdown(EnginePreparationState.Phase.LoadingRecognizer);
        long loadBegan = Stopwatch.GetTimestamp();
        DiagnosticLog.Emit("engine.load.begin", ("state", DictationStateValue));

        try
        {
            await transcriber.Prepare(engineDirectory).ConfigureAwait(false);
            long loaded = Stopwatch.GetTimestamp();
            await transcriber.WarmUpInference().ConfigureAwait(false);
            DiagnosticLog.Emit(
                "engine.load.end",
                ("ok", true),
                ("load_ms", Stopwatch.GetElapsedTime(loadBegan, loaded)),
                ("warm_ms", Stopwatch.GetElapsedTime(loaded)));

            IsEngineReady = true;
            lock (gate)
            {
                engineLoadFailure = null;
                lastLoadCompletedAt = now();
                lastEngineActivity = now();
            }

            EvaluateResidency();
        }
        catch (OperationCanceledException)
        {
            DiagnosticLog.Emit(
                "engine.load.end",
                ("ok", false),
                ("reason", "overtaken"),
                ("load_ms", Stopwatch.GetElapsedTime(loadBegan)));
            // The load was overtaken: an unload bumped the transcriber's generation while the
            // weights were still coming off disk, which is exactly what wedge recovery and model
            // deletion do. Not a broken model - the newer owner is responsible for the engine from
            // here - so no repair state and no message. Catching it matters more than the silence:
            // letting it escape kills the recycle task before it clears
            // <see cref="isRecyclingEngine"/>, and one wedge then makes every later wedge
            // incurable.
        }
        catch (Exception error) when (error is ASREngineError or IOException or InvalidOperationException)
        {
            DiagnosticLog.EmitException(
                "engine.load.failed",
                error,
                ("load_ms", Stopwatch.GetElapsedTime(loadBegan)));
            string detail = "the files passed verification, but the engine couldn't load the model: "
                + error.Message;
            lock (gate) { engineLoadFailure = detail; }
            ModelStateValue = new ModelState.RepairRequired(detail);
            int megabytes = RemainingDownloadMegabytes == 0
                ? ModelStatus.FullDownloadMegabytes
                : RemainingDownloadMegabytes;
            Notify(new DictationNotice(
                DictationNoticeKind.Failure,
                $"The model didn't load. An explicit repair will redownload {megabytes} MB."));
        }
        finally
        {
            IsPreparingEngine = false;
            EndPreparationCountdown();
        }
    }

    /// <summary>
    /// Run one second of silence through the engine to pull its execution state back into place.
    /// A hung ping means the engine is wedged — recycle it.
    /// </summary>
    private void PingEngine(TimeSpan deadline)
    {
        if (transcriber is null) { return; }
        _ = Task.Run(async () =>
        {
            Task warm = transcriber.WarmUpInference();
            // The loser of the race is abandoned, and an abandoned task that faults later is an
            // unobserved exception. Read it here so nothing is thrown into the void.
            _ = warm.ContinueWith(
                static finished => _ = finished.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);

            Task finished = await Task.WhenAny(warm, Task.Delay(deadline)).ConfigureAwait(false);
            if (finished != warm || warm.IsFaulted)
            {
                RecycleWedgedEngine();
                return;
            }

            lock (gate) { lastEngineActivity = now(); }
        });
    }

    /// <summary>Internal, not private: the residency test drives the exact press hook.</summary>
    public void WarmEngineUnderVoiceIfCold()
    {
        if (transcriber is null) { return; }
        lock (gate)
        {
            if (isRecyclingEngine) { return; }
        }

        // Residency gave the memory back: the press starts the FULL reload in parallel with the
        // recording. The single-flight transcriber coalesces this with the transcription path's
        // own prepare, and the preparation UI narrates honestly if the reload outlasts the speech.
        if (!IsEngineReady)
        {
            if (HasEngineBeenReady && !IsPreparingEngine)
            {
                _ = Task.Run(WarmUpEngine);
            }

            return;
        }

        DateTimeOffset? last;
        lock (gate) { last = lastEngineActivity; }
        if (!EngineWarming.ShouldWarm(last, now())) { return; }

        // A generous deadline: the recording gives it cover, and a real wedge still surfaces
        // through the dictation's own deadline right after.
        PingEngine(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// First inference after wake, taken out of the person's way.
    ///
    /// Sleep is where the engine's execution state dies. Without this, the first dictation of the
    /// morning pays the reconnect. The ping forces it now, while nobody is waiting; if even that
    /// hangs, the engine is wedged and gets recycled here rather than during someone's sentence.
    /// </summary>
    private void RevalidateEngineAfterWake()
    {
        if (transcriber is null || !IsEngineReady || DictationStateValue != DictationState.Idle) { return; }
        lock (gate)
        {
            if (isRecyclingEngine) { return; }
        }

        PingEngine(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Reload the recognition engine after a wedged transcription.
    ///
    /// The stall means an inference session stuck on something that died under it. The files on
    /// disk are fine; only the loaded session is poisoned. Unload and warm up again: the next
    /// dictation runs on a fresh session instead of the same stuck one, and the existing
    /// preparation UI narrates the warm-up honestly.
    /// </summary>
    private void RecycleWedgedEngine()
    {
        if (transcriber is null) { return; }
        lock (gate)
        {
            if (isRecyclingEngine) { return; }
            isRecyclingEngine = true;
        }

        IsEngineReady = false;
        _ = Task.Run(async () =>
        {
            try
            {
                await transcriber.Unload().ConfigureAwait(false);
                await WarmUpEngine().ConfigureAwait(false);
            }
            finally
            {
                // The flag falls whatever happened. Leaving it raised after a failed recovery
                // makes the engine permanently un-recyclable: every later wedge returns at the
                // "one wedge, one cure" guard, the residency evaluator sees a forever-busy
                // engine, and the person's key keeps starting dictations nothing can finish.
                lock (gate) { isRecyclingEngine = false; }
            }
        });
    }

    // MARK: - Engine residency (automatic, zero settings)

    /// <summary>Internal, not private: tests inject tiers the OS only sends under real starvation.</summary>
    public void RegisterMemoryPressure(MemoryPressureTier tier)
    {
        lock (gate)
        {
            lastPressureTier = tier;
            switch (tier)
            {
                case MemoryPressureTier.Warning: consecutiveWarnings += 1; break;
                case MemoryPressureTier.Critical: break;
                case MemoryPressureTier.Normal: consecutiveWarnings = 0; break;
            }
        }

        EvaluateResidency();
    }

    /// <summary>
    /// One evaluation path for every trigger: pressure change, finished dictation, finished load,
    /// wake, boundary timer.
    /// </summary>
    private void EvaluateResidency()
    {
        MemoryPressureTier tier;
        int warnings;
        TimeSpan idleFor;
        TimeSpan sinceLoad;
        DateTimeOffset reference = now();
        bool recycling;

        lock (gate)
        {
            residencyTimer?.Dispose();
            residencyTimer = null;
            tier = lastPressureTier;
            warnings = consecutiveWarnings;
            idleFor = lastEngineActivity is DateTimeOffset activity
                ? reference - activity
                : TimeSpan.MaxValue;
            sinceLoad = lastLoadCompletedAt is DateTimeOffset loaded
                ? reference - loaded
                : TimeSpan.MaxValue;
            recycling = isRecyclingEngine;
        }

        EngineResidencyPolicy.Decision decision = EngineResidencyPolicy.Decide(
            tier,
            warnings,
            engineLoaded: IsEngineReady,
            engineBusy: DictationStateValue != DictationState.Idle
                || IsRecoveryOperationActive
                || recycling
                || IsPreparingEngine,
            idleFor,
            sinceLoad);

        switch (decision)
        {
            case EngineResidencyPolicy.Decision.Keep:
                break;

            case EngineResidencyPolicy.Decision.Unload:
                if (transcriber is null) { return; }
                _ = Task.Run(async () =>
                {
                    if (!await transcriber.UnloadIfIdle().ConfigureAwait(false)) { return; }
                    // HasEngineBeenReady stays true: dictation remains enabled, the reload rides
                    // under the next voice.
                    IsEngineReady = false;
                    EnginePreparation = EnginePreparationState.Make(
                        EnginePreparationState.Phase.Idle, TimeSpan.Zero);
                });
                break;

            case EngineResidencyPolicy.Decision.CheckAgain checkAgain:
                TimeSpan after = checkAgain.After < TimeSpan.FromSeconds(1)
                    ? TimeSpan.FromSeconds(1)
                    : checkAgain.After;
                lock (gate)
                {
                    if (disposed) { return; }
                    residencyTimer = new Timer(
                        _ =>
                        {
                            lock (gate)
                            {
                                residencyTimer?.Dispose();
                                residencyTimer = null;
                            }

                            EvaluateResidency();
                        },
                        null,
                        after,
                        Timeout.InfiniteTimeSpan);
                }

                break;
        }
    }

    // MARK: - Engine preparation countdown

    private void BeginPreparationCountdown(EnginePreparationState.Phase phase)
    {
        lock (gate)
        {
            preparationTimer?.Dispose();
            preparationStartedAt = now();
        }

        EnginePreparation = EnginePreparationState.Make(phase, TimeSpan.Zero);

        lock (gate)
        {
            if (disposed) { return; }
            preparationTimer = new Timer(
                _ => TickPreparation(),
                null,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1));
        }
    }

    private void TickPreparation()
    {
        lock (gate)
        {
            if (preparationTimer is null) { return; }
        }

        EnginePreparation = EnginePreparationState.Make(EnginePreparation.PhaseValue, ElapsedPreparation());
    }

    private TimeSpan ElapsedPreparation()
    {
        lock (gate)
        {
            return preparationStartedAt is DateTimeOffset started ? now() - started : TimeSpan.Zero;
        }
    }

    private void EndPreparationCountdown()
    {
        lock (gate)
        {
            preparationTimer?.Dispose();
            preparationTimer = null;
            preparationStartedAt = null;
        }

        EnginePreparation = EnginePreparationState.Make(
            IsEngineReady ? EnginePreparationState.Phase.Ready : EnginePreparationState.Phase.Idle,
            TimeSpan.Zero);
    }
}
