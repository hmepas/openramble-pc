using System.Diagnostics;
using DictationCore;
using LocalASR;

namespace OpenRamble.AppCore;

/// <summary>The model on disk, the engine in memory, and the seconds between them.</summary>
public sealed partial class AppState
{
    /// <summary>The folder recognition loads from: the current model's, or the previous one's while it serves.</summary>
    private string? engineDirectory;

    /// <summary>The current model's folder.</summary>
    private string? currentEngineDirectory;

    /// <summary>The model the current one replaced, if this build knows one; see <see cref="ModelLineup"/>.</summary>
    private ModelStore? previousStore;
    private string? previousEngineDirectory;

    /// <summary>What the disk held for the previous model at the last look.</summary>
    private ModelState previousModelState = new ModelState.NotInstalled();

    /// <summary>The lineup chose another model while something was being recognized.</summary>
    private bool modelSwitchPending;

    /// <summary>Is the model being installed right now?</summary>
    private bool isInstalling;

    /// <summary>
    /// Engine failures during warm-up, one per model folder. The files on disk are intact, so a
    /// disk inspection does not see them - each is kept here until an explicit repair, or until
    /// the folder it happened on loads after all.
    /// </summary>
    /// <remarks>
    /// One per folder, not one in all: a single slot let the previous model's failure erase the
    /// current one's, the lineup moved back onto the model that had just failed, and the two
    /// took turns failing forever.
    /// </remarks>
    private readonly Dictionary<string, EngineLoadFailure> engineLoadFailures = [];

    /// <summary>
    /// Which folder the engine could not load, in words for the person, and whether its files or
    /// the engine's runtime are to blame. Only the first is something a download can fix.
    /// </summary>
    private sealed record EngineLoadFailure(string Directory, string Detail, ModelLineup.LoadFailure Kind);

    /// <summary>
    /// One lineup decision at a time, and no take beginning in the middle of one.
    /// </summary>
    /// <remarks>
    /// The disk is looked at from the interface, from the install monitor and from background
    /// tasks, and each look decides which folder serves and what the upgrade card says. Two of
    /// them interleaving could leave the card from one decision beside the folder from the other.
    /// A key-down takes the same lock around its start, so a take cannot begin between the
    /// "nothing is being recognized" check and the switch it allows.
    /// </remarks>
    private readonly Lock modelLineupGate = new();

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

    /// <summary>Test-only idle-unload countdown override (see <see cref="AppEnvironment"/>).</summary>
    private TimeSpan? idleUnloadDelayOverride;

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
        if (previousStore is not null)
        {
            ModelState previous = await Task.Run(previousStore.RefreshState).ConfigureAwait(false);
            lock (gate) { previousModelState = previous; }
        }

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
        lock (modelLineupGate)
        {
            ModelState refreshed = ApplyLineup(main);
            EngineLoadFailure? failure;
            lock (gate) { failure = DecisiveLoadFailureLocked(); }
            // Only a failure of the folder that serves takes readiness back. One the lineup has
            // already stepped away from changes nothing about the model in use.
            if (failure is not null && refreshed.IsReady)
            {
                refreshed = failure.Kind == ModelLineup.LoadFailure.Runtime
                    ? new ModelState.Failed(new ModelStoreError.EngineUnavailable(failure.Detail))
                    : new ModelState.RepairRequired(failure.Detail);
            }

            // The volume goes out before the state: a screen that wakes on "repair required"
            // reads the button's number in the same instant.
            PublishRemainingDownload(main);
            ModelStateValue = refreshed;

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
    }

    /// <summary>
    /// The failure that takes readiness back, if the serving folder has one. Called under <c>gate</c>.
    /// </summary>
    /// <remarks>
    /// When the previous model fails after the current one already has, nothing usable is left,
    /// and what a repair can do is decided by the current model: a repair only ever fetches it.
    /// </remarks>
    private EngineLoadFailure? DecisiveLoadFailureLocked()
    {
        string serving = engineDirectory ?? currentEngineDirectory ?? string.Empty;
        if (!engineLoadFailures.TryGetValue(serving, out EngineLoadFailure? failure)) { return null; }
        return currentEngineDirectory is { } current && engineLoadFailures.TryGetValue(current, out EngineLoadFailure? decisive)
            ? decisive
            : failure;
    }

    /// <summary>The current model's own load failure, if it has one. Called under <c>gate</c>.</summary>
    private EngineLoadFailure? CurrentLoadFailureLocked() =>
        currentEngineDirectory is { } current && engineLoadFailures.TryGetValue(current, out EngineLoadFailure? failure)
            ? failure
            : null;

    /// <summary>
    /// Point recognition at the model the lineup chooses and publish the upgrade's own state.
    /// </summary>
    /// <remarks>
    /// Returns the state of the model in use, which is what readiness, the menu and onboarding
    /// describe; the newer model's state goes to <see cref="UpgradeState"/> while the previous
    /// one still serves. A build with no previous model is unchanged: the main state is the
    /// state. The switch itself is only a new folder - the next preparation loads it, and
    /// <see cref="EngineByModelFiles"/> gives back the other engine's weights as it does - and it
    /// never happens under a dictation or a re-recognition: the model that was serving keeps
    /// serving until they finish.
    /// <para>
    /// The current model serves once it has loaded, not once it has verified. An engine that
    /// rejected its files, or whose runtime cannot start on this machine, leaves the previous
    /// model serving; rejected files are offered again as a download, a runtime that cannot start
    /// is not offered anything, because new files would not change it.
    /// </para>
    /// <para>Called under <see cref="modelLineupGate"/>.</para>
    /// </remarks>
    private ModelState ApplyLineup(ModelState main)
    {
        if (previousStore is null) { return main; }

        ModelState previous;
        string? serving;
        EngineLoadFailure? failure;
        lock (gate)
        {
            previous = previousModelState;
            serving = engineDirectory;
            failure = CurrentLoadFailureLocked();
        }

        ModelLineup.LoadFailure currentFailure = failure is not null && main.IsReady
            ? failure.Kind
            : ModelLineup.LoadFailure.None;
        ModelState? upgrade = currentFailure switch
        {
            ModelLineup.LoadFailure.Files => new ModelState.RepairRequired(failure!.Detail),
            ModelLineup.LoadFailure.Runtime => null,
            _ => main,
        };

        var lineup = new ModelLineup(main, previous, currentFailure);
        string? folder = lineup.Recognizer == ModelLineup.Slot.Previous ? previousEngineDirectory : currentEngineDirectory;
        if (folder != serving)
        {
            if (IsRecognitionInProgress)
            {
                lock (gate) { modelSwitchPending = true; }
                UpgradeState = lineup.Recognizer == ModelLineup.Slot.Current ? null : upgrade;
                return serving == previousEngineDirectory ? previous : main;
            }

            lock (gate)
            {
                engineDirectory = folder;
                modelSwitchPending = false;
            }

            // The engine still holds the other model; the next preparation loads this one.
            if (transcriber is not null) { IsEngineReady = false; }
        }

        if (lineup.Recognizer == ModelLineup.Slot.Previous)
        {
            UpgradeState = upgrade;
            return previous;
        }

        UpgradeState = null;
        if (lineup.RemovesPrevious && lineup.Recognizer == ModelLineup.Slot.None) { RemovePreviousModel(); }
        return main;
    }

    /// <summary>Where recognition loads from right now.</summary>
    private string ServingEngineDirectory
    {
        get { lock (gate) { return engineDirectory ?? currentEngineDirectory ?? string.Empty; } }
    }

    /// <summary>A take or a History entry is being recognized, or is about to be.</summary>
    private bool IsRecognitionInProgress => DictationStateValue != DictationState.Idle || IsRetranscribing;

    /// <summary>Carry out a model switch that waited for recognition to finish.</summary>
    private void ApplyPendingModelSwitch()
    {
        bool pending;
        lock (gate) { pending = modelSwitchPending; }
        if (!pending || IsRecognitionInProgress) { return; }
        _ = Task.Run(RefreshModelState);
    }

    /// <summary>
    /// Delete the previous model once the current one has loaded and warmed up.
    ///
    /// Verified files are not yet a working engine; the old copy is the only fallback until the
    /// new one has recognized something, so it goes after the first successful warm-up and not
    /// at the end of the download.
    /// </summary>
    private void RemovePreviousModelOnceReplaced(string loadedDirectory)
    {
        if (loadedDirectory == currentEngineDirectory) { RemovePreviousModel(); }
    }

    private void RemovePreviousModel()
    {
        if (previousStore is not { } previous) { return; }
        _ = Task.Run(async () =>
        {
            if (previous.RefreshState() is ModelState.NotInstalled) { return; }
            await previous.Delete().ConfigureAwait(false);
            lock (gate) { previousModelState = previous.CurrentState(); }
            DiagnosticLog.Emit("model.previous.removed");
        });
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
        lock (gate) { rejected = CurrentLoadFailureLocked() is { Kind: ModelLineup.LoadFailure.Files }; }
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

        // An upgrade downloads beside a working model: dictation keeps using the previous one and
        // only the upgrade card shows progress.
        if (UpgradeState is null) { IsEngineReady = false; }

        // An engine failure leaves the files on disk "intact" - the store would say ready. An
        // explicit repair is required to keep the promise of the message and re-download instead
        // of silently repeating the warm-up.
        bool engineRejectedModels;
        lock (gate)
        {
            // Only rejected files are fetched again. A runtime that could not start gets a new
            // load attempt below, from the same verified files.
            engineRejectedModels = CurrentLoadFailureLocked() is { Kind: ModelLineup.LoadFailure.Files };
            // An explicit human command opens a new attempt: the previous failures no longer
            // hold, and a fresh installation will warm up again.
            engineLoadFailures.Clear();
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
            engineLoadFailures.Clear();
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
            if (previousStore is not null) { await previousStore.Delete().ConfigureAwait(false); }
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

        /// <summary>A file was brought into the Library; its transcript needs the engine.</summary>
        Import,
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
        journal.Record(DiagnosticEvent.EnginePreparing);
        // The retry loop owns one continuous countdown across its waits; a lone attempt owns its
        // own. Whoever owns it also ends it, so the indicator never blinks between two attempts
        // of the same preparation.
        bool ownsCountdown;
        lock (gate) { ownsCountdown = engineWarmupRetryTask is null; }
        if (ownsCountdown) { BeginPreparationCountdown(EnginePreparationState.Phase.LoadingRecognizer); }

        long loadBegan = Stopwatch.GetTimestamp();
        DiagnosticLog.Emit("engine.load.begin", ("state", DictationStateValue));

        string preparing = ServingEngineDirectory;
        try
        {
            await transcriber.Prepare(preparing).ConfigureAwait(false);
            long loaded = Stopwatch.GetTimestamp();
            SetPreparationPhase(EnginePreparationState.Phase.WarmingUp);
            await transcriber.WarmUpInference().ConfigureAwait(false);
            DiagnosticLog.Emit(
                "engine.load.end",
                ("ok", true),
                ("load_ms", Stopwatch.GetElapsedTime(loadBegan, loaded)),
                ("warm_ms", Stopwatch.GetElapsedTime(loaded)));

            // The lineup moved to the other model while this one was loading: what is warm is
            // not what serves. Nothing failed; whether the app still owes an engine is decided by
            // PreparationContinues, like any other overtaken load.
            if (ServingEngineDirectory != preparing) { return EngineWarmupOutcome.Skipped; }

            lock (gate)
            {
                // A failure of another folder - the model the lineup stepped away from - still
                // stands: it is what keeps this one serving.
                engineLoadFailures.Remove(preparing);
                RevokeRetryLocked();
            }

            IsEngineReady = true;
            RescheduleIdleUnload();
            RemovePreviousModelOnceReplaced(preparing);
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
        catch (Exception error) when (error is ASREngineError.ModelsUnavailable or ASREngineError.RuntimeUnavailable)
        {
            bool runtime = error is ASREngineError.RuntimeUnavailable;
            journal.Record(DiagnosticEvent.EngineFailed, errorCode: error.HResult);
            DiagnosticLog.EmitException(
                runtime ? "engine.runtime.failed" : "engine.load.failed",
                error,
                ("load_ms", Stopwatch.GetElapsedTime(loadBegan)));
            // The model state said the files are there - the guard at the top of this method
            // is that very statement, and it is what makes this a rejection rather than a
            // missing model. A runtime that cannot start is neither: the files are not at fault.
            string detail = runtime
                ? "the speech engine couldn't start on this PC: " + error.Message
                : "the files passed verification, but the engine couldn't load the model: " + error.Message;
            lock (gate)
            {
                engineLoadFailures[preparing] = new EngineLoadFailure(
                    preparing,
                    detail,
                    runtime ? ModelLineup.LoadFailure.Runtime : ModelLineup.LoadFailure.Files);
            }

            // The failure is a fact about this folder; the lineup decides what serves now. A
            // repair redownloads rejected files, so the volume is republished along with the
            // state: without it the only control on the setup screen read "Redownload Model —
            // 0 MB".
            ApplyModelState(store?.CurrentState() ?? ModelStateValue);
            if (ServingEngineDirectory != preparing && preparing == currentEngineDirectory)
            {
                // The previous model still loads, and serves again. For the app this is an
                // overtaken load: an engine is still owed, for the other folder.
                Notify(new DictationNotice(
                    DictationNoticeKind.Warning,
                    runtime
                        ? $"Parakeet Ultra can't run on this PC ({error.Message}). Dictation keeps using the current model."
                        : "Parakeet Ultra didn't load. Dictation keeps using the current model."));
                return EngineWarmupOutcome.Skipped;
            }

            // Nothing usable is left. The lead names what failed now; what a repair can do is
            // the decisive failure's to say - see DecisiveLoadFailureLocked.
            bool repairHelps;
            bool bothFailed;
            lock (gate)
            {
                RevokeRetryLocked();
                repairHelps = DecisiveLoadFailureLocked() is not { Kind: ModelLineup.LoadFailure.Runtime };
                bothFailed = preparing != currentEngineDirectory && CurrentLoadFailureLocked() is not null;
            }

            string lead = bothFailed
                ? $"Neither Parakeet Ultra nor the previous model loaded ({error.Message})."
                : runtime
                    ? $"The speech engine couldn't start on this PC ({error.Message})."
                    : "The model didn't load.";
            Notify(new DictationNotice(
                DictationNoticeKind.Failure,
                repairHelps
                    ? $"{lead} An explicit repair will redownload {RemainingDownloadMegabytes} MB."
                    : $"{lead} Downloading the model again won't fix this."));
            return EngineWarmupOutcome.RepairRequired;
        }
        catch (Exception error) when (error is ASREngineError or IOException or InvalidOperationException)
        {
            // A momentary failure says nothing about the verified files on disk. Keep them and
            // try again in the background.
            journal.Record(DiagnosticEvent.EngineFailed, errorCode: error.HResult);
            DiagnosticLog.EmitException(
                "engine.load.retryable",
                error,
                ("load_ms", Stopwatch.GetElapsedTime(loadBegan)));
            lock (gate) { engineLoadFailures.Remove(preparing); }

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

    // MARK: - Idle unload

    /// <summary>
    /// How long the engine stays loaded after the last dictation: an hour, for everyone, with no
    /// setting (upstream 0.33.1).
    /// </summary>
    /// <remarks>
    /// Five minutes, the old default, made a third of upstream's measured week of key presses
    /// cold (106 of 303); the gaps in that same week project about one in ten for an hour. A
    /// cold take still keeps its words: the reload runs under the voice. A stored choice from
    /// the old "Unload model" picker, "Never" included, is ignored and dropped from the settings
    /// file on its next write.
    /// </remarks>
    public static TimeSpan EngineIdleUnloadDelay { get; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Test-only override for the one-hour countdown, which suites never sleep through. Setting
    /// it re-arms the countdown, so a test can rest the engine now and keep it awake after.
    /// </summary>
    public TimeSpan? IdleUnloadDelayOverride
    {
        get { lock (gate) { return idleUnloadDelayOverride; } }
        set
        {
            lock (gate) { idleUnloadDelayOverride = value; }
            RescheduleIdleUnload();
        }
    }

    /// <summary>
    /// Same doctrine as the countdown: this is visible so a test can say the app arms nothing it
    /// has no reason to arm.
    /// </summary>
    public bool IsAwaitingIdleUnload { get { lock (gate) { return idleUnloadTimer is not null; } } }

    /// <summary>Every state change re-arms or cancels the countdown: activity disarms it, idleness starts it.</summary>
    /// <remarks>
    /// Cancel, check and arm happen under one hold of <c>gate</c>. State changes arrive on
    /// whatever thread made them, and as three separate steps two re-arms could each cancel
    /// the same old timer and each arm a new one; the first was then overwritten while still
    /// running, nothing could cancel it, and it unloaded the engine a full countdown after it
    /// was armed, however recently the person had dictated. Swift gets this for free from the
    /// main actor.
    /// </remarks>
    private void RescheduleIdleUnload()
    {
        lock (gate)
        {
            idleUnloadTimer?.Dispose();
            idleUnloadTimer = null;
            if (transcriber is null || disposed) { return; }
            if (dictationState != DictationState.Idle
                || !isEngineReady
                // Setup is not idleness: the person is granting permissions, not resting from
                // dictation, and the try-out is a minute away. The engine stays so that first
                // dictation is instant. Nothing depends on this any more - the setup screen no
                // longer waits for a loaded engine, and a resting one is woken by the same key
                // press as everywhere else - so it is a courtesy, not a guard.
                || !OnboardingCompleted
                // An import reads its pieces with this engine: unloading under it would only
                // send the file back to wait for the model. The countdown starts when it is done.
                || !importActivity.IsIdle)
            {
                return;
            }

            // The override shortens the countdown for tests.
            TimeSpan delay = idleUnloadDelayOverride ?? EngineIdleUnloadDelay;
            Timer? armed = null;
            armed = new Timer(
                _ =>
                {
                    lock (gate)
                    {
                        // Disposing a timer does not recall a callback already queued: one that
                        // was replaced or cancelled while it waited for the gate is not the
                        // countdown any more, and must neither disarm the live one nor unload.
                        // Arming holds the gate, so `armed` is assigned before this can read it.
                        if (idleUnloadTimer is not { } live || !ReferenceEquals(live, armed)) { return; }
                        live.Dispose();
                        idleUnloadTimer = null;
                    }

                    PerformIdleUnload();
                },
                null,
                delay,
                Timeout.InfiniteTimeSpan);
            idleUnloadTimer = armed;
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
                || insertionOwner != InsertionOwner.None
                || isRecyclingEngine
                || isPreparingEngine
                || !isEngineReady
                || !importActivity.IsIdle)
            {
                return;
            }
        }

        DiagnosticLog.Emit("engine.idle.unload", ("idle_ms", IdleUnloadDelayOverride ?? EngineIdleUnloadDelay));
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
