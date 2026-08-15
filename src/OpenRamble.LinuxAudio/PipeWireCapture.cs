using System.Diagnostics;
using DictationCore;
using DictationCore.Audio;
using OpenRamble.AppCore;
using OpenRamble.AppCore.Audio;

namespace OpenRamble.LinuxAudio;

/// <summary>
/// Record from the default PipeWire input to a 16 kHz mono file.
///
/// The Linux peer of <c>WasapiCapture</c>: same contract, same promises, a different way of
/// getting at the microphone. Everything the driver does not need a microphone for is reused from
/// <c>DictationCore.Audio</c> rather than rewritten - the frame queue that guarantees ordering,
/// the in-memory recognition cap, the WAV writer - so the two platforms differ in exactly one
/// thing: where the samples come from.
///
/// <para>
/// The promises come from <see cref="IAudioCapturing"/> and <see cref="IMicrophoneDevice"/>, and
/// each one exists because breaking it costs a word:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="StartRecording"/> brings the recorder up synchronously. Any delay
/// here cuts off the first word.</description></item>
/// <item><description><see cref="WaitForFirstFrame"/> waits for real audio, not for the recorder.
/// Between the two lies about a tenth of a second, and a person who starts speaking at the signal
/// loses their first word in it.</description></item>
/// <item><description>The WAV on disk is the durable copy and is finished before
/// <see cref="StopRecording"/> returns. <see cref="TakeBufferedSamples"/> is an optimisation on
/// top of it, never a replacement.</description></item>
/// <item><description><b>The stop returns.</b> Whatever the recorder does - a wedged audio server,
/// a process that ignores its signals - the take is closed and the session goes on. See
/// <see cref="StopRecording"/>.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a subprocess rather than the library.</b> See <see cref="PwRecordProcess"/>. The short
/// version is that <c>pw-record</c> ships with the audio server, does the conversion in the
/// server's graph, and - the part that matters here - can be ended by force. WASAPI's stop is
/// bounded by abandoning a thread that holds the device for the life of the process; this one is
/// bounded by SIGKILL, which is a stronger guarantee than the Windows path can make.
/// </para>
/// <para>
/// <b>No resampler in this process.</b> The recorder is asked for 16 kHz mono Float32 and PipeWire
/// produces it from whatever the device runs at. So <c>capture.start</c> reports
/// <c>resampled=false</c>: it means "nothing in this process resamples", which is the thing that
/// event exists to answer. The conversion still happens - in the server, with the device's clock.
/// </para>
/// <para>
/// <b>One session at a time, and each one on its own.</b> Everything a recording owns lives in a
/// <see cref="Session"/> that its own reader closes over. A reader still blocked on a pipe that
/// will not close is therefore harmless: it hands frames to a session that is finished and refuses
/// them, instead of to whichever recording started next.
/// </para>
/// </remarks>
public sealed class PipeWireCapture : IMicrophoneDevice
{
    /// <summary>What recognition waits for. Fixed by the model, not by the device.</summary>
    private static readonly AudioStreamFormat RecordingFormat = new(16_000, 1);

    /// <summary>
    /// The in-memory cap, in samples: ten minutes of 16 kHz mono float. Past it the WAV stays
    /// complete and recognition falls back to reading it.
    /// </summary>
    private const int MaximumBufferedSamples = 16_000 * 60 * 10;

    /// <summary>
    /// How long a failed read waits for the recorder to be gone before quoting it.
    /// </summary>
    /// <remarks>
    /// Not a budget of the stop - it is spent on a dictation that has already failed, and only to
    /// make the failure say something a person can act on.
    /// </remarks>
    private static readonly TimeSpan DeathRattle = TimeSpan.FromMilliseconds(250);

    private readonly Lock gate = new();
    private readonly string takesDirectory;
    private readonly RecorderProcessLauncher launch;

    /// <summary>The recording that is accepting frames, if one is.</summary>
    private Session? current;

    /// <summary>
    /// The most recent recording, live or just finished.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="current"/> because the session reads two things <i>after</i> the
    /// stop: the buffered samples it will recognize, and the startup latency it will report. The
    /// stop has to clear <see cref="current"/> at once - so the next dictation can start while
    /// this one is still finishing - and those two reads must not come back empty because of it.
    /// </remarks>
    private Session? latest;

    /// <param name="takesDirectory">
    /// Where the take is written. The file lives exactly until the end of recognition - a
    /// person's voice has no business staying on disk.
    /// </param>
    public PipeWireCapture(string takesDirectory)
        : this(takesDirectory, () => PwRecordProcess.Launch(RecordingFormat))
    {
    }

    /// <summary>The same capture with the recorder replaced - the seam the tests drive.</summary>
    internal PipeWireCapture(string takesDirectory, RecorderProcessLauncher launch)
    {
        this.takesDirectory = takesDirectory;
        this.launch = launch;
    }

    /// <summary>
    /// The recorder this capture runs, by the name a person would type.
    /// </summary>
    /// <remarks>
    /// Published for the one caller that has to know whether recording is possible <i>without</i>
    /// recording: the shell's microphone reading is asked once a second, and the honest probe for
    /// it opens the device - which a person who is not dictating must never have happen. Asking
    /// whether the tool exists is the cheap half of that question, and it must be asked about this
    /// exact tool rather than a second copy of the name that can drift from it.
    /// </remarks>
    public static string RecorderExecutable => PwRecordProcess.Executable;

    /// <inheritdoc/>
    public Action<float>? OnInputLevel { get; set; }

    /// <inheritdoc/>
    public Action<AudioCaptureError>? Failed { get; set; }

    /// <inheritdoc/>
    public (long Frames, long Samples) DeliveredCounts
    {
        get
        {
            Session? session;
            lock (gate) { session = latest; }
            return session is null ? (0, 0) : (session.Frames, session.Samples);
        }
    }

    public Task<string> StartRecording()
    {
        Session session;
        lock (gate)
        {
            if (current is not null)
            {
                throw new AudioCaptureError.EngineUnavailable("already recording");
            }

            session = BuildSession();
            current = session;
            latest = session;
        }

        try
        {
            session.Attach(launch());
        }
        catch (Exception error)
        {
            lock (gate)
            {
                if (ReferenceEquals(current, session)) { current = null; }
            }

            session.Close();
            session.Sink.Cancel();
            session.Writer.Discard();
            // The full exception, not just its type: a missing tool and a refused device say
            // everything useful in their messages, and this line is the only place it survives.
            DiagnosticLog.EmitException(
                "capture.start",
                error,
                ("session", session.Number),
                ("ok", false));
            throw new AudioCaptureError.EngineUnavailable(error.Message);
        }

        // A thread of its own, not a pool one: this blocks on a pipe for the whole dictation, and
        // the pool grows by one thread per second under starvation - the frame queue's drain runs
        // on that pool, and it is what the stop waits for.
        session.Reader = Task.Factory.StartNew(
            () => Pump(session),
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

        DiagnosticLog.Emit(
            "capture.start",
            ("session", session.Number),
            ("ok", true),
            ("device_rate", (int)RecordingFormat.SampleRate),
            ("device_channels", RecordingFormat.Channels),
            ("resampled", false));
        return Task.FromResult(session.Writer.FilePath);
    }

    public Task<bool> WaitForFirstFrame()
    {
        lock (gate)
        {
            return current?.FirstFrame.Task ?? Task.FromResult(false);
        }
    }

    /// <summary>
    /// Close the take and give it to the session, within a bounded amount of time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing here waits on the audio stack to behave.</b> The order is deliberate and each
    /// step is the reason the next one cannot hang:
    /// </para>
    /// <list type="number">
    /// <item><description>The session stops being the current one, so the next dictation is free
    /// to start whatever this one turns out to be waiting for.</description></item>
    /// <item><description>The recorder is asked to leave with SIGTERM, which it answers by closing
    /// its stream. Sending a signal does not wait for anything.</description></item>
    /// <item><description>The reader is given what is left of
    /// <see cref="CaptureStopBudget.Drain"/> to reach the end of the pipe. This is the last pull:
    /// the tail of the dictation is in that pipe, and it is the word the person released the key
    /// on.</description></item>
    /// <item><description>The session is closed to frames - a reader that has not reached the end
    /// hands its bytes to a session that refuses them.</description></item>
    /// <item><description>The frame queue is completed immediately and waited on for whatever
    /// remains of the same budget, so the two waits together can never exceed it and the total
    /// stop stays exactly the one <see cref="CaptureStopBudget"/> describes.</description></item>
    /// <item><description>The WAV is closed. From this line the dictation is safe on disk.</description></item>
    /// <item><description>The recorder is reaped on a worker with its own budget: a grace period
    /// for the SIGTERM, then SIGKILL. A process cannot ignore the second one, which is why this
    /// path is bounded by something stronger than a timeout.</description></item>
    /// </list>
    /// </remarks>
    public async Task<(string Url, TimeSpan Duration)> StopRecording()
    {
        Session session;
        lock (gate)
        {
            session = current ?? throw new AudioCaptureError.NotRecording();
            current = null;
        }

        long began = Stopwatch.GetTimestamp();
        DiagnosticLog.Emit("capture.stop.begin", ("session", session.Number));

        StopDevice(session);

        bool tailRead = await Bounded(session.Reader, CaptureStopBudget.Drain).ConfigureAwait(false);
        session.Close();

        // Completing the writer happens inside Finish() before it awaits anything, so the queue
        // is closed the moment this line runs whether or not the wait below succeeds.
        Task drain = session.Sink.Finish();
        TimeSpan left = CaptureStopBudget.Drain - Stopwatch.GetElapsedTime(began);
        bool drained = tailRead && await Bounded(drain, left).ConfigureAwait(false);
        if (drained)
        {
            // Rethrows a recorder failure - a full disk has to reach the person, not the log.
            await drain.ConfigureAwait(false);
        }
        else
        {
            // Abandoned, and its eventual failure with it: the writer is closed under it below,
            // so its next append throws into a task nobody is left to await.
            Forget(drain);
            // And the in-memory copy goes with it. It is missing whatever the queue still held,
            // and a recognition run on a truncated buffer would silently drop the end of the
            // sentence; the WAV is short by the same tail but is at least the honest record.
            session.Buffered.Discard();
            DiagnosticLog.Emit(
                "capture.stop.drain-abandoned",
                ("session", session.Number),
                ("budget_ms", CaptureStopBudget.Drain));
        }

        TimeSpan duration = session.Writer.Duration;
        string path = session.Writer.Close();

        bool released = await ReleaseDevice(session).ConfigureAwait(false);
        DiagnosticLog.Emit(
            "capture.stop.end",
            ("session", session.Number),
            ("frames", session.Frames),
            ("samples", session.Samples),
            ("audio_ms", duration),
            ("ms", Stopwatch.GetElapsedTime(began)),
            ("drained", drained),
            ("device_released", released));
        return (path, duration);
    }

    public Task<float[]?> TakeBufferedSamples()
    {
        Session? session;
        lock (gate) { session = latest; }

        return Task.FromResult(session?.Buffered.Take());
    }

    public async Task AbortRecording()
    {
        Session? session;
        lock (gate)
        {
            session = current;
            current = null;
        }

        if (session is null) { return; }

        DiagnosticLog.Emit("capture.abort", ("session", session.Number), ("frames", session.Frames));
        session.Buffered.Discard();
        StopDevice(session);
        session.Close();
        session.Sink.Cancel();
        session.Writer.Discard();
        _ = await ReleaseDevice(session).ConfigureAwait(false);
    }

    public Task<TimeSpan?> StartupLatency()
    {
        Session? session;
        lock (gate) { session = latest; }

        return Task.FromResult(session?.StartupLatency);
    }

    public void Dispose() => AbortRecording().GetAwaiter().GetResult();

    /// <summary>
    /// Assemble one recording: take, queue and the memory the recognition will read.
    /// </summary>
    /// <remarks>
    /// The gate must be held: the session becomes the current one the moment this returns, and
    /// two of these built at once would be two microphones open. The recorder itself is started
    /// outside the gate, the way the Windows peer starts its device outside it.
    /// </remarks>
    private Session BuildSession()
    {
        Directory.CreateDirectory(takesDirectory);
        string path = Path.Combine(takesDirectory, $"take-{Guid.NewGuid():N}.wav");
        var writer = new WAVWriter(path, (int)RecordingFormat.SampleRate, RecordingFormat.Channels);
        writer.Open();

        // The reader hands frames over and goes back to the pipe; one task in a row works the
        // queue through, and that is the ordering guarantee. Writing each frame from the reader
        // would put a disk write between two reads of a pipe the audio server is filling in real
        // time, and closing the file without draining would lose the last word - the one the
        // person releases the key on.
        var queue = new FrameSink();
        var memory = new RecognitionSampleBuffer(MaximumBufferedSamples);
        var session = new Session(
            // The dictation the take belongs to, not a counter of our own: the kernel publishes
            // the number before it asks for a recording, and every event of that session - the
            // recognition and the insertion included - is written against it.
            DiagnosticLog.Session,
            writer,
            queue,
            memory);

        session.Deliver = queue.Start(frame =>
        {
            writer.Append(frame);
            memory.Append(frame);
            return Task.CompletedTask;
        });
        return session;
    }

    /// <summary>
    /// Read the recorder's output for as long as it has one.
    /// </summary>
    /// <remarks>
    /// The end of the stream is the recorder's death, and only a stop is allowed to have caused
    /// it. Anything else - a killed process, an audio server that went away, a device that was
    /// unplugged - is reported to the session rather than left to look like a short recording.
    /// </remarks>
    private void Pump(Session session)
    {
        IRecorderProcess? recorder = session.Recorder;
        if (recorder is null) { return; }

        try
        {
            RawSampleStream.Pump(
                recorder.Output,
                CaptureDrainPolicy.ChunkSamples,
                frame => Accept(session, frame));
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException
            or InvalidOperationException)
        {
            // The pipe went away under the read. During a stop that is what a stop looks like.
            if (session.IsFinishing) { return; }

            ReportFailure(session, new AudioCaptureError.EngineUnavailable(error.Message));
            return;
        }

        if (session.IsFinishing) { return; }

        // A moment for the recorder to finish dying, so the report carries its exit status and
        // its last words instead of "it closed the pipe". Stdout ends first and the complaint on
        // stderr - "Host is down", the whole answer to why there is no dictation - arrives just
        // after it. Bounded, and only on a path that has already failed.
        recorder.WaitForExit(DeathRattle);
        ReportFailure(session, new AudioCaptureError.EngineUnavailable(recorder.ExitReport));
    }

    /// <summary>One frame from the pipe into the take, the memory and the waveform.</summary>
    private void Accept(Session session, float[] frame)
    {
        if (!session.Accept(frame, out float peak, out bool first)) { return; }

        if (first)
        {
            DiagnosticLog.Emit(
                "capture.first-frame",
                ("session", session.Number),
                ("ms", session.StartupLatency ?? TimeSpan.Zero));
        }

        // Outside the session's own lock, always: this reaches the overlay, which is the
        // interface, and the reader must never be inside somebody else's lock while it is there.
        OnInputLevel?.Invoke(peak);
    }

    /// <summary>
    /// A stream can fail in several places at once. One cause must not raise a dozen alarms, and
    /// it must not be swallowed either: whoever is waiting for the first frame is told that there
    /// will not be one.
    /// </summary>
    private void ReportFailure(Session session, AudioCaptureError error)
    {
        session.Failure.Run(() =>
        {
            DiagnosticLog.Emit(
                "capture.failed",
                ("session", session.Number),
                ("kind", error.GetType().Name),
                ("message", error.Message));
            // False, not an exception: "there is no recording any more" is what the port promises
            // here, and promising "speak" where nothing is being written is the one thing this
            // must never do.
            session.FirstFrame.TrySetResult(false);
            Failed?.Invoke(error);
        });
    }

    /// <summary>
    /// Ask the recorder to leave, and record that the end of its stream is now expected.
    /// </summary>
    /// <remarks>
    /// The flag has to be set before the signal, not after: SIGTERM can be honoured between the
    /// two statements, and a stop that reported its own recorder's exit as a failure would end
    /// every dictation with an error the person never caused.
    /// </remarks>
    private static void StopDevice(Session session)
    {
        session.BeginFinishing();
        try
        {
            session.Recorder?.RequestStop();
        }
        catch (Exception error)
        {
            DiagnosticLog.Emit(
                "capture.stop.request-failed",
                ("session", session.Number),
                ("kind", error.GetType().Name));
        }
    }

    /// <summary>
    /// Let go of the recorder, on a worker, within <see cref="CaptureStopBudget.DeviceRelease"/>.
    /// </summary>
    /// <remarks>
    /// Half the budget for the SIGTERM already sent to be honoured - a recorder that is mid-read
    /// of a device deserves the chance to close it properly - then SIGKILL and the rest of the
    /// budget to be reaped. The wait runs on a worker with nobody blocked on it, so even a process
    /// that survives both signals costs the stop nothing beyond the budget. Returning
    /// <c>false</c> means a child process may still be holding the microphone; it is the honest
    /// outcome, and it is bounded, which is what the stop has to be.
    /// </remarks>
    private static async Task<bool> ReleaseDevice(Session session)
    {
        IRecorderProcess? recorder = session.Detach();
        if (recorder is null) { return true; }

        TimeSpan grace = CaptureStopBudget.DeviceRelease / 2;
        Task release = Task.Run(() =>
        {
            try
            {
                if (!recorder.WaitForExit(grace))
                {
                    recorder.Kill();
                    // Nothing survives SIGKILL, so this second wait is for the kernel to reap the
                    // child rather than for the recorder to decide anything. It is the whole
                    // budget rather than what is left of it because nobody is waiting on this
                    // worker: the deadline is the Bounded below, and it started before the grace.
                    recorder.WaitForExit(CaptureStopBudget.DeviceRelease);
                }
            }
            catch (Exception error)
            {
                DiagnosticLog.Emit(
                    "capture.release-failed",
                    ("session", session.Number),
                    ("kind", error.GetType().Name));
            }
            finally
            {
                recorder.Dispose();
            }
        });

        bool released = await Bounded(release, CaptureStopBudget.DeviceRelease).ConfigureAwait(false);
        if (!released)
        {
            Forget(release);
            DiagnosticLog.Emit(
                "capture.stop.device-abandoned",
                ("session", session.Number),
                ("budget_ms", CaptureStopBudget.DeviceRelease));
        }

        return released;
    }

    /// <summary>Wait for a task, but never past <paramref name="budget"/>.</summary>
    private static async Task<bool> Bounded(Task task, TimeSpan budget)
    {
        // Asked first, because a budget spent by an earlier wait would otherwise report a task
        // that is already finished as abandoned.
        if (task.IsCompleted) { return true; }

        if (budget <= TimeSpan.Zero) { return false; }

        using var expiry = new CancellationTokenSource();
        Task finished = await Task.WhenAny(task, Task.Delay(budget, expiry.Token)).ConfigureAwait(false);
        if (ReferenceEquals(finished, task))
        {
            await expiry.CancelAsync().ConfigureAwait(false);
            return true;
        }

        return false;
    }

    /// <summary>Observe an abandoned task's failure so it does not surface as an unhandled one.</summary>
    private static void Forget(Task task) =>
        _ = task.ContinueWith(
            static finished => DiagnosticLog.Emit(
                "capture.abandoned-failed",
                ("kind", finished.Exception?.GetBaseException().GetType().Name ?? "none")),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    /// <summary>
    /// Everything one recording owns, and the only thing its reader can reach.
    /// </summary>
    /// <remarks>
    /// The Windows peer learned this the hard way: one set of fields on the capture, reused by
    /// every session, let a device whose thread would not die deliver its bytes into whichever
    /// recording happened to be running - one dictation's microphone writing into the next
    /// dictation's file. Here a reader that outlived its stop finds <see cref="IsClosed"/> and
    /// returns.
    /// </remarks>
    private sealed class Session
    {
        /// <summary>
        /// Serializes delivery and the close that ends it.
        /// </summary>
        /// <remarks>
        /// One reader delivers and one stop closes, so this guards a race of exactly two - but it
        /// is the race that decides whether the last frame of a dictation reaches the file or a
        /// finished session's queue.
        /// </remarks>
        private readonly Lock deliveryGate = new();

        private long frames;
        private long samples;
        private bool closed;
        private volatile bool finishing;

        public Session(int number, WAVWriter writer, FrameSink sink, RecognitionSampleBuffer buffered)
        {
            Number = number;
            Writer = writer;
            Sink = sink;
            Buffered = buffered;
            StartedAt = Stopwatch.GetTimestamp();
        }

        public int Number { get; }

        public WAVWriter Writer { get; }

        public FrameSink Sink { get; }

        public RecognitionSampleBuffer Buffered { get; }

        /// <summary>The recorder, from the moment it starts until the stop takes it away.</summary>
        public IRecorderProcess? Recorder { get; private set; }

        /// <summary>The one task reading the recorder's output.</summary>
        public Task Reader { get; set; } = Task.CompletedTask;

        public long StartedAt { get; }

        public TaskCompletionSource<bool> FirstFrame { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FailureOnce Failure { get; } = new();

        public Action<float[]> Deliver { get; set; } = static _ => { };

        /// <summary>Time from the start of the recorder to the first sample that reached the file.</summary>
        public TimeSpan? StartupLatency { get; private set; }

        public long Frames => Interlocked.Read(ref frames);

        public long Samples => Interlocked.Read(ref samples);

        /// <summary>A stop or an abort has begun: the end of the stream is expected from here on.</summary>
        public bool IsFinishing => finishing;

        public bool IsClosed { get { lock (deliveryGate) { return closed; } } }

        public void Attach(IRecorderProcess recorder) => Recorder = recorder;

        /// <summary>Take the recorder away, so only the release can reach it.</summary>
        public IRecorderProcess? Detach()
        {
            IRecorderProcess? recorder = Recorder;
            Recorder = null;
            return recorder;
        }

        public void BeginFinishing() => finishing = true;

        /// <summary>
        /// Take one frame from the reader into the take, the memory and the level.
        /// </summary>
        /// <returns><c>false</c> when the session is closed - the frame belongs to nobody.</returns>
        public bool Accept(float[] frame, out float peak, out bool firstFrame)
        {
            peak = 0;
            firstFrame = false;
            lock (deliveryGate)
            {
                if (closed || frame.Length == 0) { return false; }

                if (StartupLatency is null)
                {
                    StartupLatency = Stopwatch.GetElapsedTime(StartedAt);
                    firstFrame = true;
                    // Continuations are asynchronous by construction, so releasing whoever waits
                    // for "speak" is safe under this lock.
                    FirstFrame.TrySetResult(true);
                }

                peak = Peak(frame);
                Deliver(frame);
                Interlocked.Increment(ref frames);
                Interlocked.Add(ref samples, frame.Length);
                return true;
            }
        }

        /// <summary>Refuse every further frame. Irreversible, and part of every stop.</summary>
        public void Close()
        {
            lock (deliveryGate) { closed = true; }
        }

        private static float Peak(float[] frame)
        {
            float peak = 0;
            foreach (float sample in frame)
            {
                float magnitude = Math.Abs(sample);
                if (magnitude > peak)
                {
                    peak = magnitude;
                }
            }

            return Math.Clamp(peak, 0f, 1f);
        }
    }
}
