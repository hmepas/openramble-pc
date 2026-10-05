using System.Diagnostics;
using DictationCore;

namespace LocalASR;

/// <summary>
/// Local speech recognition.
///
/// Keeps the model loaded, recognizes files and knows how to release memory.
/// </summary>
/// <remarks>
/// The Swift original is an <c>actor</c>. It protects the state but <b>not</b> the order:
/// actors are reentrant, and on each <c>await</c> the next call gets in. The rule "one
/// dictation at a time" is held by the state machine one level up - it simply does not start
/// the second one until the first has finished.
///
/// Here a lock does the protecting: every state change is synchronous, and every
/// <c>await</c> happens outside the lock - which reproduces the reentrancy the tests pin
/// down (a load in flight is joined, not restarted; a polite unload refuses while work is
/// running).
///
/// Swift cancels by cancelling the <c>Task</c>; C# needs the token in the signature, so the
/// operations that Swift lets the caller cancel take a <see cref="CancellationToken"/>.
/// </remarks>
public sealed class LocalTranscriber
{
    private readonly Lock gate = new();
    private readonly IASREngineAdapting engine;
    private readonly AudioFileReader reader;
    private string? loadedDirectory;

    /// <summary>
    /// The in-flight load, if any. Loads are single-flight: the press-time warm-up and the
    /// transcribe path both ask for the engine, and without coalescing they would start two
    /// multi-second model loads - doubling the memory spike on exactly the machine whose
    /// memory pressure caused the unload in the first place.
    /// </summary>
    private Task? loadTask;

    /// <summary>
    /// Bumped by every unload. A load that finishes into an older generation has been
    /// overtaken - its weights are released and its caller sees cancellation instead of a
    /// resurrected engine.
    /// </summary>
    private int generation;

    /// <summary>
    /// Recognition calls in flight. Residency's polite unload refuses while this is non-zero;
    /// only the forced unload (wedge recovery) ignores it.
    /// </summary>
    private int activeOperations;

    /// <summary>
    /// A silence inference that materializes the engine's execution state. It is
    /// single-flight, and a real dictation waits for it instead of competing for the same
    /// compute resources after the key is released.
    /// </summary>
    private Task? inferenceWarmupTask;

    /// <summary>
    /// Distinguishes successive warm-ups. A cancelled prediction may ignore cancellation and
    /// finish after unload plus a new prepare; its owner's cleanup must not clear the newer
    /// task's single-flight marker.
    /// </summary>
    private int inferenceWarmupEpoch;

    /// <remarks>
    /// Swift defaults the engine to <c>FluidAudioAdapter()</c>. There is no default here on
    /// purpose: the engine adapter is the next wave, and a default would have to name a type
    /// that does not exist yet.
    /// </remarks>
    public LocalTranscriber(IASREngineAdapting engine, AudioFileReader? reader = null)
    {
        this.engine = engine;
        this.reader = reader ?? new AudioFileReader();
    }

    public bool IsPrepared
    {
        get
        {
            lock (gate)
            {
                return loadedDirectory is not null;
            }
        }
    }

    /// <summary>Is a recognition or a load running right now?</summary>
    public bool IsBusy
    {
        get
        {
            lock (gate)
            {
                return activeOperations > 0 || loadTask is not null;
            }
        }
    }

    /// <summary>
    /// Load the model in advance. Single-flight: concurrent calls ride one load. The first
    /// call after installation compiles the model for the accelerator and is noticeably
    /// longer than subsequent ones - this should not happen at the moment the user is waiting
    /// for text.
    /// </summary>
    public async Task Prepare(string modelDirectory)
    {
        // Looking at the in-flight load and starting one must be a single step. In Swift the
        // actor guarantees that (nothing suspends between the two); here the lock does, and
        // the task is created inside it - starting it only runs the engine call's synchronous
        // prologue, which never re-enters this object.
        //
        // The loop is what Swift writes straight-line: ride the load in flight, and if it
        // finished on some other directory, start our own. A load that appeared meanwhile is
        // ridden too rather than duplicated.
        while (true)
        {
            Task task;
            bool rider;
            lock (gate)
            {
                if (loadedDirectory == modelDirectory)
                {
                    return;
                }

                if (loadTask is not null)
                {
                    task = loadTask;
                    rider = true;
                }
                else
                {
                    task = Load(modelDirectory, generation);
                    loadTask = task;
                    rider = false;
                }
            }

            if (rider)
            {
                await task.ConfigureAwait(false);
                // The shared task marks completion before finishing, so by the time any
                // rider resumes, the mark is already visible.
                continue;
            }

            try
            {
                await task.ConfigureAwait(false);
            }
            finally
            {
                lock (gate)
                {
                    if (ReferenceEquals(loadTask, task))
                    {
                        loadTask = null;
                    }
                }
            }

            return;
        }
    }

    private async Task Load(string modelDirectory, int expectedGeneration)
    {
        await engine.LoadModels(modelDirectory).ConfigureAwait(false);
        lock (gate)
        {
            if (generation == expectedGeneration)
            {
                loadedDirectory = modelDirectory;
                return;
            }
        }

        // Unloaded while loading: the owner has discarded this generation. Release the
        // orphaned weights and report the load as overtaken, not successful.
        await engine.Unload().ConfigureAwait(false);
        throw new OperationCanceledException();
    }

    /// <summary>
    /// Load or rebuild the acoustic term hint.
    ///
    /// Separate from <see cref="Prepare"/>, because it is a separate model with a separate
    /// fate: without it recognition works fully, and with it the terms are recognized at the
    /// sound level rather than by post-processing.
    ///
    /// <b>Call this every time the user's dictionary has changed</b> - not only when warming
    /// up. A repeated call reassembles the term set inside the live engine; the weights of
    /// the CTC model stay loaded, so the reassembly costs a fraction of a second, not
    /// thirteen. An empty set removes the hinter and releases its weights.
    ///
    /// The errors are differentiated intentionally: <see cref="VocabularyBoostError"/> is
    /// trouble with the list of terms, that is with human data, and repairing the model does
    /// not treat it; <c>ASREngineError.ModelsUnavailable</c> is a problem with the weights,
    /// and there a download is appropriate. Catch them separately.
    /// </summary>
    public async Task PrepareVocabulary(string modelDirectory, VocabularyBoost boost)
    {
        if (engine is not IVocabularyBoostCapable capable)
        {
            throw new ASREngineError.ModelsUnavailable("the engine doesn't support vocabulary hints");
        }

        await capable.LoadVocabularyModels(modelDirectory, boost).ConfigureAwait(false);
    }

    /// <summary>Recognize the recorded file.</summary>
    public async Task<ASRResult> Transcribe(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (loadedDirectory is null)
            {
                throw new ASREngineError.ModelsNotLoaded();
            }
        }

        // Stamped before the read, because the read is where the time went.
        long arrived = Stopwatch.GetTimestamp();

        // Reading and decoding the recording is synchronous file work, and it used to happen
        // right here, on whichever pool thread the dictation was waiting on. A pool thread
        // blocked on a disk is not yielded but lost, and the whole dictation waits behind it.
        // Upstream's field logs showed recognition of 11.65 s around an engine call of 0.11 s,
        // with the stage timer reporting a queue of zero - because the timer sat after this line
        // rather than before it. Disk work gets a thread of its own, which is allowed to wait.
        float[] samples;
        long decodeStarted = Stopwatch.GetTimestamp();
        try
        {
            samples = await DedicatedThread.Run(() => reader.Samples(filePath), "OpenRamble recording decode")
                .ConfigureAwait(false);
        }
        catch (AudioFileReader.Failure failure)
        {
            throw new ASREngineError.UnsupportedAudioFormat(failure.Message);
        }

        TimeSpan decoded = Stopwatch.GetElapsedTime(decodeStarted);
        cancellationToken.ThrowIfCancellationRequested();
        ASRResult result = await Transcribe(samples, cancellationToken).ConfigureAwait(false);
        // Report the whole wait, decode included, rather than only the part after it. The
        // previous number was true and useless.
        TimeSpan waited = Stopwatch.GetElapsedTime(arrived);
        TimeSpan queued = waited - result.ProcessingDuration - result.EngineDispatchDuration;
        return new ASRResult(
            result.Text,
            result.Words,
            result.AudioDuration,
            result.ProcessingDuration,
            engineDispatchDuration: result.EngineDispatchDuration,
            queueingDuration: queued > TimeSpan.Zero ? queued : TimeSpan.Zero,
            decodingDuration: decoded);
    }

    /// <summary>
    /// Recognize a ready buffer.
    ///
    /// The entire recording goes into the engine, no matter how long it is: the engine makes
    /// its own fifteen-second windows, with overlap and token deduplication. There used to be
    /// our own pause cutting here - it was written against the silent loss of speech at the
    /// seam of windows. The measurements showed that the reason for the loss was not the
    /// length of the piece but a flag of the library; the cutting cut phrases in the middle,
    /// slowed parsing down twice and on the main scenario (Russian speech with English
    /// inserts) lost three times more words than a properly configured engine. Details and
    /// numbers are in <c>docs/benchmarks.md</c>.
    /// </summary>
    public async Task<ASRResult> Transcribe(
        float[] samples,
        CancellationToken cancellationToken = default)
    {
        Task? warmup;
        int expectedGeneration;
        // Stamped at the door, before any waiting. Everything between here and the engine - a
        // shared warm-up, another dictation holding the engine, a thread that has not been
        // handed out - used to be reported nowhere at all, and that interval is where every
        // stall this app has had actually lived.
        long arrived = Stopwatch.GetTimestamp();
        lock (gate)
        {
            if (loadedDirectory is null)
            {
                throw new ASREngineError.ModelsNotLoaded();
            }

            if (samples.Length == 0)
            {
                throw new ASREngineError.UnsupportedAudioFormat("empty recording");
            }

            // Claim residency before waiting on the shared warm-up. Otherwise the warm-up
            // owner can drop its busy count just before this continuation resumes, leaving a
            // narrow window where a polite unload sees an idle engine even though a real
            // dictation is queued for it.
            activeOperations++;
            expectedGeneration = generation;
            warmup = inferenceWarmupTask;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (warmup is not null)
            {
                await warmup.ConfigureAwait(false);
                // Waiting on a shared task does not consume the waiter's cancellation.
                // Escape/deadline may have abandoned this dictation while the shared warm-up
                // was finishing; never start inference for it.
                cancellationToken.ThrowIfCancellationRequested();
            }

            lock (gate)
            {
                if (generation != expectedGeneration || loadedDirectory is null)
                {
                    throw new OperationCanceledException();
                }
            }

            // In place: the buffer is this dictation's and nobody reads it after the engine.
            // See InputGain for the numbers behind it.
            _ = InputGain.Normalize(samples);
            TimeSpan queued = Stopwatch.GetElapsedTime(arrived);
            ASRResult result = await engine.Transcribe(samples).ConfigureAwait(false);
            return new ASRResult(
                result.Text,
                result.Words,
                result.AudioDuration,
                result.ProcessingDuration,
                engineDispatchDuration: result.EngineDispatchDuration,
                queueingDuration: queued,
                decodingDuration: result.DecodingDuration);
        }
        finally
        {
            lock (gate)
            {
                activeOperations--;
            }
        }
    }

    /// <summary>
    /// Execute one representative inference after all optional models have loaded. Loading
    /// the weights alone does not compile/materialize every prediction path; without this,
    /// the first short dictation can be seconds slower than the steady state.
    /// </summary>
    public async Task WarmUpInference()
    {
        Task task;
        int expectedGeneration;
        int warmupEpoch;
        bool rider;
        // One step again: seeing no warm-up in flight and becoming the one that runs it may
        // not be interrupted, or two silence inferences start at once.
        lock (gate)
        {
            if (loadedDirectory is null)
            {
                throw new ASREngineError.ModelsNotLoaded();
            }

            expectedGeneration = generation;
            rider = inferenceWarmupTask is not null;
            if (rider)
            {
                task = inferenceWarmupTask!;
                warmupEpoch = inferenceWarmupEpoch;
            }
            else
            {
                inferenceWarmupEpoch++;
                warmupEpoch = inferenceWarmupEpoch;
                task = engine.Transcribe(new float[16_000]);
                inferenceWarmupTask = task;
                activeOperations++;
            }
        }

        if (rider)
        {
            await task.ConfigureAwait(false);
            lock (gate)
            {
                if (generation != expectedGeneration || loadedDirectory is null)
                {
                    throw new OperationCanceledException();
                }
            }

            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                activeOperations--;
                if (inferenceWarmupEpoch == warmupEpoch)
                {
                    inferenceWarmupTask = null;
                }
            }
        }

        lock (gate)
        {
            if (generation != expectedGeneration || loadedDirectory is null)
            {
                throw new OperationCanceledException();
            }
        }
    }

    /// <summary>
    /// Live preview: start, flow of samples, stop.
    ///
    /// Silently skipped if the engine cannot preview: this is decoration on top of the
    /// dictation and not a part of it - the contract is the same as for the language hint.
    /// </summary>
    public async Task StartPreview(Action<string, string> onUpdate)
    {
        if (engine is not ILivePreviewCapable capable)
        {
            return;
        }

        await capable.StartPreview(onUpdate).ConfigureAwait(false);
    }

    public async Task FeedPreview(float[] samples)
    {
        if (engine is not ILivePreviewCapable capable)
        {
            return;
        }

        await capable.FeedPreview(samples).ConfigureAwait(false);
    }

    public async Task StopPreview()
    {
        if (engine is not ILivePreviewCapable capable)
        {
            return;
        }

        await capable.StopPreview().ConfigureAwait(false);
    }

    /// <summary>
    /// Free up the memory under the model - forced.
    ///
    /// Used by wedge recovery and model deletion: it must work even when a zombie inference
    /// will never return. A load in flight is fenced out by the generation bump; its caller
    /// sees cancellation.
    /// </summary>
    /// <remarks>
    /// Swift also calls <c>cancel()</c> on the warm-up task. There is no equivalent here -
    /// the engine interface takes no token - and there does not need to be: the Swift comment
    /// says a prediction may ignore cancellation anyway, so the generation and epoch fence is
    /// what actually discards an overtaken warm-up. Dropping the reference does the same job.
    /// </remarks>
    public async Task Unload()
    {
        lock (gate)
        {
            generation++;
            inferenceWarmupEpoch++;
            inferenceWarmupTask = null;
        }

        await engine.Unload().ConfigureAwait(false);
        lock (gate)
        {
            loadedDirectory = null;
        }
    }

    /// <summary>
    /// Free up the memory under the model - polite, for residency management.
    ///
    /// Refuses while a load or a recognition is in flight: interleaving an unload with live
    /// work on the reentrant engine would end in a nondeterministic state. The owner
    /// re-evaluates on completion events.
    /// </summary>
    public async Task<bool> UnloadIfIdle()
    {
        lock (gate)
        {
            if (activeOperations > 0 || loadTask is not null)
            {
                return false;
            }
        }

        await Unload().ConfigureAwait(false);
        return true;
    }
}
