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
    /// True while an engine that was deliberately given back should stay cold.
    ///
    /// An idle unload is a decision, not a failure: the comeback belongs to the next key press.
    /// The standing preparation rule must respect that, or the app would immediately reload
    /// what it just released. Setup never sets this, because during setup the person is waiting
    /// for the engine rather than resting from it.
    /// </summary>
    private bool shouldStayUnloadedUntilUse;

    private IdleUnloadPolicy modelUnloadTimeout;

    /// <summary>Test-only idle-unload countdown override (see <see cref="AppEnvironment"/>).</summary>
    private readonly TimeSpan? idleUnloadDelayOverride;

    /// <summary>One pending idle-unload timer at most; any dictation activity replaces it.</summary>
    private Timer? idleUnloadTimer;

    /// <summary>
    /// Model preparation is single-flight at this boundary as well as inside the transcriber.
    /// This keeps UI state and recovery decisions causal when startup, wake and a key press
    /// arrive together.
    /// </summary>
    private Task<EngineWarmupOutcome>? engineWarmupTask;
    private int engineWarmupTaskRevision;

    /// <summary>
    /// At most one retry loop may exist. A transient engine failure never becomes a model
    /// redownload request.
    /// </summary>
    private Task? engineWarmupRetryTask;
    private CancellationTokenSource? engineWarmupRetryCancellation;
    private int engineWarmupRetryRevision;
    private readonly TimeSpan? engineWarmupRetryDelayOverride;

    private Timer? preparationTimer;
    private DateTimeOffset? preparationStartedAt;

    private enum EngineWarmupOutcome
    {
        Ready,
        Skipped,
        Retryable,
        RepairRequired,
    }

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

    /// <summary>
    /// Publish what the disk holds, and let readiness start what readiness starts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The macOS build combines two stores here through <see cref="ModelPairState"/>. This port
    /// installs one model - the acoustic term hinter has no ONNX export - so the main store's
    /// state IS the state.
    /// </para>
    /// <para>
    /// A disk inspection cannot see an engine failure: the files are intact, but the model did
    /// not come up. Until the person explicitly asks for a repair the refusal stays on screen -
    /// otherwise the repair button disappears and dictation still does not work. Decided before
    /// publishing, not after: readiness is what starts a preparation, so a "ready" the app is
    /// about to take back would start one that instantly finds nothing to do.
    /// </para>
    /// <para>
    /// Every route by which the files become usable starts preparation. Readiness arrives from
    /// more places than the two events that used to start a warm-up: a finished install, yes,
    /// but also a plain look at the disk - the Settings window refreshes on open, and a run that
    /// begins before the download re-reads the result afterwards. Readiness discovered that way
    /// used to start nothing, and the app sat on a ready model with a cold engine that had never
    /// once been loaded, which is the one cold engine a key press cannot warm. Hanging the rule
    /// on the fact itself, rather than on the events that happen to produce it, is what makes it
    /// standing.
    /// </para>
    /// </remarks>
    private void ApplyModelState(ModelState main)
    {
        ModelState refreshed = main;
        string? failure;
        lock (gate) { failure = engineLoadFailure; }
        if (failure is not null && refreshed.IsReady)
        {
            refreshed = new ModelState.RepairRequired(failure);
        }

        ModelStateValue = refreshed;
        PublishRemainingDownload(main);

        if (transcriber is null)
        {
            IsEngineReady = ModelStateValue.IsReady;
        }
        else if (!ModelStateValue.IsReady)
        {
            IsEngineReady = false;
        }

        if (ModelStateValue.IsReady) { PrepareEngineIfIdleAndCold(); }
    }

    /// <summary>
    /// Publish what the download button would fetch if it were pressed now.
    ///
    /// Recomputed wherever the answer can have changed, and from one place, so the two facts it
    /// depends on - what the disk holds and whether the engine has rejected an intact copy -
    /// cannot be consulted by half the screens.
    /// </summary>
    private void PublishRemainingDownload(ModelState main)
    {
        bool rejected;
        lock (gate) { rejected = engineLoadFailure is not null; }
        // Anything but a ready model on disk means the whole model is still to fetch; an engine
        // that rejected verified files means fetching it again.
        long bytes = !main.IsReady || rejected ? mainModelBytes : 0;
        RemainingDownloadMegabytes = (int)((bytes + 500_000) / 1_000_000);
    }

    /// <summary>
    /// What a fresh start costs: the model, from nothing.
    ///
    /// The delete confirmation asks a different question from the download button - not "what
    /// is missing now" but "what will you pay to get this back". Asking
    /// <see cref="RemainingDownloadMegabytes"/> there gives 0, which is why that sentence used
    /// to carry a hardcoded number that no manifest change would ever reach. Read from the
    /// bundled manifest instead.
    /// </summary>
    public int FullModelDownloadMegabytes => (int)((mainModelBytes + 500_000) / 1_000_000);

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

            // The first load reads the weights and plans the run, and that takes seconds. It
            // starts here so that nobody pays for it under their first dictation.
            //
            // Launch gave up its explicit warm-up because readiness alone starts one; an install
            // cannot, because the standing rule stands down while isInstalling holds - the
            // readiness published a few lines above therefore started nothing. It has just been
            // cleared, so this is the first instant the rule can speak, and it speaks in both
            // forms: the load itself, and then the rule, for the case where that load only
            // joined an attempt already in flight and so scheduled no retry ladder of its own.
            if (ModelStateValue.IsReady) { await WarmUpEngine().ConfigureAwait(false); }
            PrepareEngineIfIdleAndCold();
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

    /// <summary>
    /// Delete the model, and revoke everything that was preparing it.
    /// </summary>
    /// <remarks>
    /// Deletion is an ownership boundary, not another model lifecycle event. Every preparation
    /// task is revoked before the transcriber is asked to unload, so a cancellation-deaf load
    /// cannot publish Ready or queue a retry after the files have gone away. A revoked
    /// preparation has nobody left to put its indicator down: the ladder lowers it when it
    /// finishes, but only for the revision it still owns, and the bump here takes that revision
    /// away - so a ladder cancelled mid-wait woke up belonging to nobody and left the app saying
    /// it was preparing a model that no longer existed. Deletion is where the revoking happens,
    /// so deletion is where it is undone.
    /// </remarks>
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

        ModelStateValue = new ModelState.Deleting();
        IsEngineReady = false;
        lock (gate)
        {
            engineLoadFailure = null;
            engineWarmupTaskRevision += 1;
            engineWarmupTask = null;
            RevokeRetryLocked();
        }

        IsPreparingEngine = false;
        EndPreparationCountdown();
        CancelIdleUnload();

        _ = Task.Run(async () =>
        {
            if (transcriber is not null) { await transcriber.Unload().ConfigureAwait(false); }
            await store.Delete().ConfigureAwait(false);
            await RefreshModelState().ConfigureAwait(false);
        });
    }

    // MARK: - Engine

    /// <summary>Does the app currently want a ready engine?</summary>
    /// <remarks>
    /// The files are usable, the recognizer is not loaded, and nobody gave the memory back on
    /// purpose. This is the "desired state" half of the rule that keeps setup from stalling; it
    /// is deliberately not about events.
    /// </remarks>
    private bool WantsEngineReady
    {
        get
        {
            lock (gate)
            {
                return modelState.IsReady && !isEngineReady && !shouldStayUnloadedUntilUse;
            }
        }
    }

    /// <summary>
    /// Leave the "downloaded but cold" state without waiting for an event.
    /// </summary>
    /// <remarks>
    /// Preparation used to be started only by events: launch, a finished install, a key press.
    /// Any missed event left the model ready on disk, the engine cold, nothing running, and a
    /// setup screen claiming to prepare. This is the standing rule instead of those events: if
    /// the files are ready and the engine is not, and no attempt is alive, start one. Safe to
    /// call from anywhere - <see cref="WarmUpEngine"/> single-flights.
    /// </remarks>
    public void PrepareEngineIfIdleAndCold()
    {
        if (transcriber is null) { return; }
        lock (gate)
        {
            if (!modelState.IsReady
                || isEngineReady
                || isPreparingEngine
                || engineWarmupTask is not null
                || engineWarmupRetryTask is not null
                || isRecyclingEngine
                || isInstalling
                // A deliberate idle unload owns its own comeback: that engine is meant to stay
                // cold until the next key press.
                || shouldStayUnloadedUntilUse)
            {
                return;
            }
        }

        DiagnosticLog.Emit("engine.prepare.resumed", ("reason", "ready model, cold engine, nothing running"));
        _ = Task.Run(() => WarmUpEngine());
    }

    /// <summary>What woke the engine back up.</summary>
    public enum RewarmTrigger
    {
        KeyDown,
        AppActivation,
    }

    /// <summary>
    /// The earliest reload triggers: they fire before any readiness guard can swallow the press.
    /// Single-flight through <see cref="WarmUpEngine"/>'s shared task; calling on every press is
    /// safe and free when warm.
    /// </summary>
    /// <remarks>
    /// There used to be a memory-pressure tier in this condition, deciding whether waking was
    /// polite right now. It is gone with the rest of the pressure machinery: reloading costs a
    /// moment and a few hundred megabytes, which is not a decision worth negotiating with the OS
    /// about. And there used to be a "ping" after this - a full inference over a second of
    /// silence, on every key press, which the real dictation then waited for. That made every
    /// dictation two inferences where one was needed; warming still happens where it is earned,
    /// once after a load and once after the machine wakes.
    /// </remarks>
    public void RewarmEngineIfCold(RewarmTrigger trigger)
    {
        if (transcriber is null) { return; }
        lock (gate)
        {
            if (isRecyclingEngine
                || isEngineReady
                || !hasEngineBeenReady
                || isPreparingEngine
                || !modelState.IsReady)
            {
                return;
            }

            shouldStayUnloadedUntilUse = false;
        }

        DiagnosticLog.Emit("engine.rewarm", ("trigger", trigger));
        _ = Task.Run(() => WarmUpEngine());
    }

    /// <summary>
    /// Does one attempt's outcome leave the app still owing a ready engine?
    /// </summary>
    /// <remarks>
    /// Two places read an outcome - the first attempt and the retry ladder - and this is the
    /// only sentence either of them is allowed to read it by. They used to answer it separately,
    /// and they answered "skipped" differently: the first attempt treated "nothing happened" as
    /// one moment's answer, the ladder as the world's. A single cancelled load inside the ladder
    /// therefore ended preparation for good, on the one engine nothing else can rescue - an
    /// engine that has never been ready is not woken by a key press, has no idle-unload comeback
    /// to wait for, and is reloaded only when readiness changes, which it does not, because the
    /// model has been ready the whole time. Quitting was the only way out.
    /// </remarks>
    private bool PreparationContinues(EngineWarmupOutcome outcome) => outcome switch
    {
        // The engine is loaded. Nothing is owed.
        EngineWarmupOutcome.Ready => false,
        // The files are the problem, and only the person can answer for them. The card says so
        // and offers the button.
        EngineWarmupOutcome.RepairRequired => false,
        // A momentary failure that says nothing about the verified files. Wait longer and retry.
        EngineWarmupOutcome.Retryable => true,
        // Nothing happened: a cancelled request, an overtaken generation, a guard that was
        // briefly false. Whether that is the end depends on one thing only - whether the app
        // still wants a ready engine.
        EngineWarmupOutcome.Skipped => WantsEngineReady,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    /// <summary>
    /// Prepare one fully warm engine. Calls from launch, wake, recovery and a key-down are
    /// coalesced here so only the owner updates the UI and decides whether a failure means
    /// "retry" or "repair files".
    /// </summary>
    private async Task<EngineWarmupOutcome> WarmUpEngine(bool allowAutomaticRetry = true)
    {
        Task<EngineWarmupOutcome> task;
        int revision;
        bool owner;
        lock (gate)
        {
            if (engineWarmupTask is { } inFlight)
            {
                task = inFlight;
                revision = engineWarmupTaskRevision;
                owner = false;
            }
            else
            {
                engineWarmupTaskRevision += 1;
                revision = engineWarmupTaskRevision;
                task = Task.Run(PerformEngineWarmup);
                engineWarmupTask = task;
                owner = true;
            }
        }

        EngineWarmupOutcome outcome = await task.ConfigureAwait(false);
        if (!owner) { return outcome; }

        lock (gate)
        {
            if (engineWarmupTaskRevision == revision) { engineWarmupTask = null; }
        }

        if (allowAutomaticRetry && PreparationContinues(outcome))
        {
            ScheduleEngineWarmupRetry(outcome == EngineWarmupOutcome.Retryable
                ? "the engine failed to load"
                : "preparation did not start");
        }

        return outcome;
    }

    private async Task<EngineWarmupOutcome> PerformEngineWarmup()
    {
        // One source of truth. The published model state is what the screens read, and asking
        // the store again only introduces a second opinion that can disagree for an instant -
        // and that instant used to be terminal: the disagreement returned "nothing to do",
        // which schedules no retry, so a fresh install could sit on a promise nobody was keeping.
        if (transcriber is null || engineDirectory is null) { return EngineWarmupOutcome.Skipped; }
        if (!ModelStateValue.IsReady) { return EngineWarmupOutcome.Skipped; }

        IsEngineReady = false;
        IsPreparingEngine = true;
        // The retry loop owns one continuous countdown across its waits; a lone attempt owns its
        // own. Whoever owns it also ends it, so the indicator never blinks between two attempts
        // of the same preparation.
        bool ownsCountdown;
        lock (gate) { ownsCountdown = engineWarmupRetryTask is null; }
        if (ownsCountdown) { BeginPreparationCountdown(EnginePreparationState.Phase.LoadingRecognizer); }

        long loadBegan = Stopwatch.GetTimestamp();
        DiagnosticLog.Emit("engine.load.begin", ("state", DictationStateValue));

        try
        {
            await transcriber.Prepare(engineDirectory).ConfigureAwait(false);
            long loaded = Stopwatch.GetTimestamp();
            SetPreparationPhase(EnginePreparationState.Phase.WarmingUp);
            await transcriber.WarmUpInference().ConfigureAwait(false);
            DiagnosticLog.Emit(
                "engine.load.end",
                ("ok", true),
                ("load_ms", Stopwatch.GetElapsedTime(loadBegan, loaded)),
                ("warm_ms", Stopwatch.GetElapsedTime(loaded)));

            lock (gate)
            {
                engineLoadFailure = null;
                RevokeRetryLocked();
            }

            IsEngineReady = true;
            RescheduleIdleUnload();
            return EngineWarmupOutcome.Ready;
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
            // deletion do. Not a broken model - whether the app still owes an engine is decided
            // by the one sentence that reads outcomes, see PreparationContinues.
            return EngineWarmupOutcome.Skipped;
        }
        catch (ASREngineError.ModelsUnavailable error)
        {
            DiagnosticLog.EmitException(
                "engine.load.failed",
                error,
                ("load_ms", Stopwatch.GetElapsedTime(loadBegan)));
            string detail = "the files passed verification, but the engine couldn't load the model: "
                + error.Message;
            // The model state said the files are there - the guard at the top of this method
            // is that very statement, and it is what makes this a rejection rather than a
            // missing model. The repair redownloads them anyway, so the volume is republished
            // right here: without it the only control on the setup screen read "Redownload
            // Model — 0 MB".
            ModelState filesOnDisk = ModelStateValue;
            lock (gate)
            {
                engineLoadFailure = detail;
                RevokeRetryLocked();
            }

            // The volume goes out before the state: a screen that wakes on "repair required"
            // reads the button's number in the same instant, and a number published a beat later
            // is the "0 MB" this comment is about, seen once on a loaded test host.
            PublishRemainingDownload(filesOnDisk);
            ModelStateValue = new ModelState.RepairRequired(detail);
            Notify(new DictationNotice(
                DictationNoticeKind.Failure,
                $"The model didn't load. An explicit repair will redownload {RemainingDownloadMegabytes} MB."));
            return EngineWarmupOutcome.RepairRequired;
        }
        catch (Exception error) when (error is ASREngineError or IOException or InvalidOperationException)
        {
            // A momentary failure says nothing about the verified files on disk. Keep them and
            // try again in the background.
            DiagnosticLog.EmitException(
                "engine.load.retryable",
                error,
                ("load_ms", Stopwatch.GetElapsedTime(loadBegan)));
            lock (gate) { engineLoadFailure = null; }
            return EngineWarmupOutcome.Retryable;
        }
        finally
        {
            bool stillOwns;
            lock (gate) { stillOwns = engineWarmupRetryTask is null; }
            if (stillOwns)
            {
                IsPreparingEngine = false;
                EndPreparationCountdown();
            }
        }
    }

    /// <summary>
    /// Keep preparing until it works. There is nothing else to do.
    /// </summary>
    /// <remarks>
    /// The files are verified; only the load failed, and the commonest reason is a machine
    /// momentarily busy with the hundreds of megabytes it just wrote. A budget of attempts turned
    /// that into a permanent "give up" state with a button, on the very first run - the app now
    /// simply waits longer between attempts and stays honestly "preparing" throughout, so the
    /// interface never has to ask a person to retry work it can do itself. One task at a time,
    /// fenced by revision, cancelled by unload and delete.
    /// </remarks>
    private void ScheduleEngineWarmupRetry(string initialDetail)
    {
        int revision;
        CancellationToken token;
        lock (gate)
        {
            if (engineWarmupRetryTask is not null || !modelState.IsReady || disposed) { return; }
            engineWarmupRetryRevision += 1;
            revision = engineWarmupRetryRevision;
            engineWarmupRetryCancellation = new CancellationTokenSource();
            token = engineWarmupRetryCancellation.Token;
            // Claimed under the lock so the countdown below is owned by the loop from its first
            // instant, before any attempt can end it.
            engineWarmupRetryTask = Task.CompletedTask;
        }

        DiagnosticLog.Emit("engine.rewarm.scheduled", ("reason", initialDetail));
        // The countdown keeps running across the waits: the person sees one continuous
        // "preparing", which is exactly what is happening.
        IsPreparingEngine = true;
        BeginPreparationCountdown(EnginePreparationState.Phase.LoadingRecognizer);

        Task loop = Task.Run(async () =>
        {
            int failures = 0;
            while (true)
            {
                // Re-assert after each attempt: the attempt's own bookkeeping runs while this
                // loop is still going to try again.
                IsPreparingEngine = true;
                try
                {
                    await Task.Delay(EngineWarmupRetryDelay(failures), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    FinishRetryPresentation(revision);
                    return;
                }

                bool current;
                lock (gate) { current = engineWarmupRetryRevision == revision && modelState.IsReady; }
                if (!current)
                {
                    FinishRetryPresentation(revision);
                    return;
                }

                EngineWarmupOutcome outcome = await WarmUpEngine(allowAutomaticRetry: false).ConfigureAwait(false);
                if (!PreparationContinues(outcome))
                {
                    FinishRetryPresentation(revision);
                    return;
                }

                failures += 1;
            }
        });
        lock (gate)
        {
            if (engineWarmupRetryRevision == revision) { engineWarmupRetryTask = loop; }
        }
    }

    private TimeSpan EngineWarmupRetryDelay(int failures) =>
        engineWarmupRetryDelayOverride ?? EngineWarmupBackoff.Delay(failures);

    /// <summary>
    /// Hand the preparation indicator back once the loop is done owning it.
    /// </summary>
    /// <remarks>
    /// A revoked ladder arrives here with a stale revision by definition - that is what revoking
    /// means - so refusing it outright left the indicator lit with nobody behind it. Only a live
    /// successor may keep the indicator, and a successor is exactly what a non-null retry task
    /// means: every revocation site clears it, so a stale revision with nothing in that slot is
    /// a ladder that left without an heir.
    /// </remarks>
    private void FinishRetryPresentation(int revision)
    {
        lock (gate)
        {
            if (engineWarmupRetryRevision != revision && engineWarmupRetryTask is not null) { return; }
            engineWarmupRetryTask = null;
            engineWarmupRetryCancellation?.Dispose();
            engineWarmupRetryCancellation = null;
        }

        IsPreparingEngine = false;
        EndPreparationCountdown();
    }

    /// <summary>Cancel the retry loop. Called under <c>gate</c>.</summary>
    private void RevokeRetryLocked()
    {
        engineWarmupRetryRevision += 1;
        engineWarmupRetryCancellation?.Cancel();
        engineWarmupRetryCancellation = null;
        engineWarmupRetryTask = null;
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
            }
        });
    }

    /// <summary>
    /// First inference after wake, taken out of the person's way.
    ///
    /// Sleep is where the engine's execution state dies. Without this, the first dictation of the
    /// morning pays the reconnect. The ping forces it now, while nobody is waiting; if even that
    /// hangs, the engine is wedged and gets recycled here rather than during someone's sentence.
    /// This is one of the two places warming is earned - the other is right after a load.
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
                // "one wedge, one cure" guard, and the person's key keeps starting dictations
                // nothing can finish.
                lock (gate) { isRecyclingEngine = false; }
            }

            // A recycle whose reload was overtaken leaves a ready model with a cold engine and
            // nothing running - the state the standing rule exists to leave.
            PrepareEngineIfIdleAndCold();
        });
    }

    // MARK: - Idle unload (the person's own control)

    /// <summary>
    /// When to give the engine's memory back after dictation goes idle. Mirrors Handy's "Unload
    /// Model" row; <see cref="IdleUnloadPolicy.Never"/> is the always-resident behaviour.
    /// </summary>
    public IdleUnloadPolicy ModelUnloadTimeout
    {
        get { lock (gate) { return modelUnloadTimeout; } }
        set
        {
            if (!Set(ref modelUnloadTimeout, value)) { return; }
            UpdateSettings(current => current with { ModelUnloadTimeout = value.RawValue });
            RescheduleIdleUnload();
        }
    }

    /// <summary>
    /// Same doctrine as the countdown: this is visible so a test can say the app arms nothing it
    /// has no reason to arm.
    /// </summary>
    public bool IsAwaitingIdleUnload { get { lock (gate) { return idleUnloadTimer is not null; } } }

    /// <summary>Every state change re-arms or cancels the countdown: activity disarms it, idleness starts it.</summary>
    private void RescheduleIdleUnload()
    {
        CancelIdleUnload();
        if (transcriber is null) { return; }
        TimeSpan? policyDelay = ModelUnloadTimeout.IdleDelay;
        if (DictationStateValue != DictationState.Idle
            || !IsEngineReady
            // Setup is not idleness: the person is granting permissions, not resting from
            // dictation, and the try-out is a minute away. The engine stays so that first
            // dictation is instant. Nothing depends on this any more - the setup screen no
            // longer waits for a loaded engine, and a resting one is woken by the same key press
            // as everywhere else - so it is a courtesy, not a guard.
            || !OnboardingCompleted
            || policyDelay is null)
        {
            return;
        }

        // The override shortens the countdown for tests; the decision itself (never keeps the
        // engine forever) always belongs to the policy.
        TimeSpan delay = idleUnloadDelayOverride ?? policyDelay.Value;
        lock (gate)
        {
            if (disposed) { return; }
            idleUnloadTimer = new Timer(
                _ =>
                {
                    lock (gate)
                    {
                        idleUnloadTimer?.Dispose();
                        idleUnloadTimer = null;
                    }

                    PerformIdleUnload();
                },
                null,
                delay,
                Timeout.InfiniteTimeSpan);
        }
    }

    private void CancelIdleUnload()
    {
        lock (gate)
        {
            idleUnloadTimer?.Dispose();
            idleUnloadTimer = null;
        }
    }

    private void PerformIdleUnload()
    {
        if (transcriber is null) { return; }
        lock (gate)
        {
            if (dictationState != DictationState.Idle
                || isRecoveryOperationActive
                || isRecyclingEngine
                || isPreparingEngine
                || !isEngineReady)
            {
                return;
            }
        }

        DiagnosticLog.Emit("engine.idle.unload", ("policy", ModelUnloadTimeout.RawValue));
        _ = Task.Run(async () =>
        {
            if (!await transcriber.UnloadIfIdle().ConfigureAwait(false)) { return; }
            lock (gate) { shouldStayUnloadedUntilUse = true; }
            // HasEngineBeenReady stays true: dictation remains enabled, the reload rides under
            // the next voice. Deliberately no proactive rewarm: the comeback is the next key
            // press.
            IsEngineReady = false;
            EnginePreparation = EnginePreparationState.Make(
                EnginePreparationState.Phase.Idle, TimeSpan.Zero);
        });
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

    /// <summary>A milestone completed: the bar moves, the seconds keep counting.</summary>
    private void SetPreparationPhase(EnginePreparationState.Phase phase)
    {
        lock (gate)
        {
            if (preparationTimer is null) { return; }
        }

        EnginePreparation = EnginePreparationState.Make(phase, ElapsedPreparation());
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
