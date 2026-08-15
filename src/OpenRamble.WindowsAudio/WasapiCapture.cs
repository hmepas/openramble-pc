using System.Diagnostics;
using DictationCore;
using DictationCore.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OpenRamble.AppCore;
using OpenRamble.AppCore.Audio;

namespace OpenRamble.WindowsAudio;

/// <summary>
/// Record from the default microphone to a 16 kHz mono file.
///
/// This is the driver half of <c>DictationAudio/MicrophoneCapture.swift</c> - the half that
/// needed AVFoundation there and needs WASAPI here. Everything the driver does not need a
/// microphone for was already ported into <c>DictationCore.Audio</c> and is reused rather than
/// rewritten: the frame queue that guarantees ordering, the in-memory recognition cap, and the
/// conversion policy that decides whether a resampler is needed at all.
///
/// <para>
/// The promises this type has to keep come from <see cref="IAudioCapturing"/>, and each one
/// exists because breaking it costs a word:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="StartRecording"/> brings the engine up synchronously. Any delay
/// here cuts off the first word.</description></item>
/// <item><description><see cref="WaitForFirstFrame"/> waits for real audio, not for the engine.
/// Between the two lies about a tenth of a second, and a person who starts speaking at the signal
/// loses their first word in it.</description></item>
/// <item><description>The WAV on disk is the durable copy and is finished before
/// <see cref="StopRecording"/> returns. <see cref="TakeBufferedSamples"/> is an optimisation on
/// top of it, never a replacement: past the cap it returns <c>null</c> and recognition reads the
/// file.</description></item>
/// <item><description><b>The stop returns.</b> Whatever the audio stack does - a driver that
/// stops answering, a virtual device that never releases its thread - the take is closed and the
/// session goes on. See <see cref="StopRecording"/>.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Resampling.</b> <c>WdlResamplingSampleProvider</c>, not <c>MediaFoundationResampler</c>.
/// Media Foundation is a COM subsystem that has to be started per process and per thread, is
/// absent on N/KN editions of Windows until the Media Feature Pack is installed, and answers a
/// missing codec with an <c>HRESULT</c> in the middle of a dictation. The WDL resampler is
/// managed, deterministic, has no install-time dependency, and its quality is far beyond what a
/// 16 kHz speech model can tell apart. A dictation product cannot afford an audio path that works
/// on most Windows installs.
/// </para>
/// <para>
/// <b>One session at a time, and each one on its own.</b> Everything a recording owns lives in a
/// <see cref="Session"/> that its own device's callbacks close over. A device whose thread will
/// not die is therefore harmless: it keeps handing frames to a session that is finished and
/// refuses them, instead of to whichever recording started next.
/// </para>
/// </remarks>
public sealed class WasapiCapture : IMicrophoneDevice
{
    /// <summary>What recognition waits for. Fixed by the model, not by the device.</summary>
    private static readonly AudioStreamFormat RecordingFormat = new(16_000, 1);

    /// <summary>
    /// The in-memory cap, in samples: ten minutes of 16 kHz mono float. Past it the WAV stays
    /// complete and recognition falls back to reading it.
    /// </summary>
    private const int MaximumBufferedSamples = 16_000 * 60 * 10;

    private readonly Lock gate = new();
    private readonly string takesDirectory;

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
    public WasapiCapture(string takesDirectory)
    {
        this.takesDirectory = takesDirectory;
    }

    /// <summary>
    /// Raised for every pull from the device with its peak level in 0…1, for the waveform.
    /// </summary>
    /// <remarks>
    /// A number, never samples. The overlay draws the shape of the signal and must not be handed
    /// anything from which speech could be reconstructed.
    /// </remarks>
    public Action<float>? OnInputLevel { get; set; }

    /// <summary>The stream died mid-dictation. Never silent: the session ends and says why.</summary>
    public Action<AudioCaptureError>? Failed { get; set; }

    /// <summary>
    /// What the latest session handed to the recorder: frames and samples, counts only.
    /// </summary>
    /// <remarks>
    /// The one number that tells "the microphone is delivering nothing" apart from "the microphone
    /// is delivering silence", and the bench tool's whole answer to "is the device alive". Never
    /// the samples themselves.
    /// </remarks>
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
            session.Device.StartRecording();
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
            _ = ReleaseDevice(session);
            // The full exception, not just its type: a WASAPI refusal says everything useful in
            // its HRESULT message, and this line is the only place it survives.
            DiagnosticLog.EmitException(
                "capture.start",
                error,
                ("session", session.Number),
                ("ok", false));
            throw new AudioCaptureError.EngineUnavailable(error.Message);
        }

        DiagnosticLog.Emit(
            "capture.start",
            ("session", session.Number),
            ("ok", true),
            ("device_rate", session.DeviceFormat.SampleRate),
            ("device_channels", session.DeviceFormat.Channels),
            ("resampled", session.IsResampled));
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
    /// <item><description>The device is asked to stop. NAudio only flips a flag here; its
    /// blocking half is <c>Dispose</c>, which comes last and on another thread.</description></item>
    /// <item><description>One last pull, then the session is closed to frames - a capture thread
    /// that has not noticed the stop hands its bytes to a session that refuses them.</description></item>
    /// <item><description>The frame queue is completed immediately and waited on for
    /// <see cref="CaptureStopBudget.Drain"/> - long enough for the last word, not long enough to
    /// matter.</description></item>
    /// <item><description>The WAV is closed. From this line the dictation is safe on disk.</description></item>
    /// <item><description>The device is released on a worker with its own budget. NAudio's
    /// <c>Dispose</c> joins the capture thread with no timeout, and on a virtual audio path that
    /// thread may never return; the worker is abandoned to it and the process outlives it.</description></item>
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
        session.PullOnce(out _, out _);
        session.Close();

        // Completing the writer happens inside Finish() before it awaits anything, so the queue
        // is closed the moment this line runs whether or not the wait below succeeds.
        Task drain = session.Sink.Finish();
        bool drained = await Bounded(drain, CaptureStopBudget.Drain).ConfigureAwait(false);
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
    /// Assemble one recording: device, take, queue and the provider chain between them.
    /// </summary>
    /// <remarks>
    /// The gate must be held: the session becomes the current one the moment this returns, and
    /// two of these built at once would be two microphones open.
    /// </remarks>
    private Session BuildSession()
    {
        MMDevice endpoint;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }
        catch (Exception error)
        {
            // No microphone, or the audio service is down. Both are "the engine is not
            // available", and neither may be reported as an empty recording.
            DiagnosticLog.EmitException(
                "capture.start",
                error,
                ("session", DiagnosticLog.Session),
                ("ok", false),
                ("stage", "device-lookup"));
            throw new AudioCaptureError.EngineUnavailable(error.Message);
        }

        var device = new NAudio.CoreAudioApi.WasapiCapture(endpoint)
        {
            // Shared mode: exclusive mode would take the microphone away from every other
            // application, and a dictation tool is not entitled to that.
            ShareMode = AudioClientShareMode.Shared,
        };

        WaveFormat deviceFormat = device.WaveFormat;
        var source = new AudioStreamFormat(deviceFormat.SampleRate, deviceFormat.Channels);

        Directory.CreateDirectory(takesDirectory);
        string path = Path.Combine(takesDirectory, $"take-{Guid.NewGuid():N}.wav");
        var writer = new WAVWriter(path, (int)RecordingFormat.SampleRate, RecordingFormat.Channels);
        writer.Open();

        // The audio thread hands bytes over and returns; one task in a row works the queue
        // through, and that is the ordering guarantee. Writing each frame from the callback
        // would mix the recording with itself, and closing the file without draining would
        // lose the last word - the one the person releases the key on.
        var buffer = new BufferedWaveProvider(deviceFormat)
        {
            DiscardOnBufferOverflow = false,
            BufferDuration = TimeSpan.FromSeconds(5),

            // The one line this whole file turns on. NAudio's default is `true`, which answers a
            // read of an empty buffer with the full count of zeroes - so a pull loop that stops
            // when it is given less than it asked for never stops. It manufactured silence at
            // processor speed, filled the frame queue with it, and the stop that had to drain
            // that queue outlived the session's deadline: "the microphone didn't stop when the
            // key was released". See CaptureDrainPolicy.
            ReadFully = false,
        };

        var queue = new FrameSink();
        var memory = new RecognitionSampleBuffer(MaximumBufferedSamples);
        var session = new Session(
            // The dictation the take belongs to, not a counter of our own: the kernel publishes
            // the number before it asks for a recording, and every event of that session - the
            // recognition and the insertion included - is written against it. A second counter
            // here would agree with that one until the first session that ends before it
            // records, and disagree with it silently for the rest of the run.
            DiagnosticLog.Session,
            device,
            writer,
            queue,
            memory,
            buffer,
            BuildPipeline(buffer, source),
            source);

        Action<float[]> push = queue.Start(frame =>
        {
            writer.Append(frame);
            memory.Append(frame);
            return Task.CompletedTask;
        });
        session.Deliver = push;

        session.OnData = (_, args) => OnDataAvailable(session, args);
        session.OnStopped = (_, args) => OnRecordingStopped(session, args);
        device.DataAvailable += session.OnData;
        device.RecordingStopped += session.OnStopped;
        return session;
    }

    /// <summary>
    /// Device format in, 16 kHz mono float out.
    /// </summary>
    /// <remarks>
    /// The fork is <see cref="MicrophoneCaptureFormat.Converter"/>'s: a resampler exists only when
    /// one is genuinely needed, and a conversion that cannot be performed throws rather than
    /// dropping frames. A frame quietly dropped here is a word missing from the recording with
    /// nothing to show for it.
    /// </remarks>
    private static ISampleProvider BuildPipeline(BufferedWaveProvider buffer, AudioStreamFormat source)
    {
        ISampleProvider samples = buffer.ToSampleProvider();
        if (source.Channels > 1)
        {
            // Averaged, not picked: taking one channel of a stereo microphone throws away half
            // the voice, and on some laptops that half is the one facing the person.
            samples = new StereoToMonoSampleProvider(samples) { LeftVolume = 0.5f, RightVolume = 0.5f };
        }

        IAudioSampleConverter? converter = MicrophoneCaptureFormat.Converter(
            source,
            RecordingFormat,
            (_, target) => new WdlConverter(samples, (int)target.SampleRate));

        return converter is WdlConverter wdl ? wdl.Provider : samples;
    }

    /// <summary>
    /// Adapts the port's conversion policy to NAudio's provider chain.
    /// </summary>
    /// <remarks>
    /// The port's <see cref="IAudioSampleConverter"/> is frame-in / frame-out because
    /// <c>AVAudioConverter</c> is; NAudio pulls instead of pushing. The policy that matters -
    /// "a conversion that cannot be performed must be visible" - is kept by constructing the
    /// resampler here and letting a failure throw out of the factory.
    /// </remarks>
    private sealed class WdlConverter : IAudioSampleConverter
    {
        public WdlConverter(ISampleProvider source, int targetSampleRate)
        {
            Provider = new WdlResamplingSampleProvider(source, targetSampleRate);
        }

        public ISampleProvider Provider { get; }

        public float[] Convert(float[] samples) => samples;
    }

    private void OnDataAvailable(Session session, WaveInEventArgs args)
    {
        if (args.BytesRecorded == 0) { return; }

        bool accepted = session.Accept(args.Buffer, args.BytesRecorded, out float peak, out bool first);
        if (!accepted)
        {
            // The queue backed up beyond five seconds, which means the drain has stopped. Losing
            // audio silently is not an option; report it once and end the session.
            ReportFailure(session, new AudioCaptureError.WriteFailed("the capture buffer overflowed"));
            return;
        }

        if (session.PullFailure is { } failure)
        {
            ReportFailure(session, failure);
            return;
        }

        if (first)
        {
            DiagnosticLog.Emit(
                "capture.first-frame",
                ("session", session.Number),
                ("ms", session.StartupLatency ?? TimeSpan.Zero));
        }

        // Outside the pull's own lock, always: this reaches the overlay, which is the interface,
        // and the audio thread must never be inside somebody else's lock while it is there.
        OnInputLevel?.Invoke(peak);
    }

    private void OnRecordingStopped(Session session, StoppedEventArgs args)
    {
        if (args.Exception is null || session.IsClosed) { return; }

        ReportFailure(session, new AudioCaptureError.EngineUnavailable(args.Exception.Message));
    }

    /// <summary>
    /// A stream can send several frames before it stops. One cause must not raise a dozen alarms,
    /// and it must not be swallowed either: whoever is waiting for the first frame is told that
    /// there will not be one.
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

    /// <summary>Ask the device to stop. Non-blocking by NAudio's contract, guarded anyway.</summary>
    private static void StopDevice(Session session)
    {
        try
        {
            session.Device.StopRecording();
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
    /// Let go of the device, on a worker, within <see cref="CaptureStopBudget.DeviceRelease"/>.
    /// </summary>
    /// <remarks>
    /// The callbacks come off first, so a thread that outlives the budget can no longer reach
    /// anything of ours. Then the disposal - which is a thread join with no timeout - runs where
    /// nobody is waiting on it. Returning <c>false</c> means a background thread is still holding
    /// a microphone this process will not get back; it is the honest outcome, and it is bounded,
    /// which the alternative was not.
    /// </remarks>
    private static async Task<bool> ReleaseDevice(Session session)
    {
        if (session.OnData is { } data) { session.Device.DataAvailable -= data; }
        if (session.OnStopped is { } stopped) { session.Device.RecordingStopped -= stopped; }

        Task release = Task.Run(() =>
        {
            try
            {
                session.Device.Dispose();
            }
            catch (Exception error)
            {
                DiagnosticLog.Emit(
                    "capture.release-failed",
                    ("session", session.Number),
                    ("kind", error.GetType().Name));
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
    /// Everything one recording owns, and the only thing its device's callbacks can reach.
    /// </summary>
    /// <remarks>
    /// The previous shape was one set of fields on the capture, reused by every session. A device
    /// whose thread would not die then delivered its bytes into whichever recording happened to be
    /// running - one dictation's microphone writing into the next dictation's file. Here a zombie
    /// finds <see cref="IsClosed"/> and returns.
    /// </remarks>
    private sealed class Session
    {
        /// <summary>
        /// Serializes every pull.
        /// </summary>
        /// <remarks>
        /// The capture thread pulls on each callback and the stop pulls one last time; the
        /// resampler and the stereo fold behind them keep mutable buffers and are not
        /// thread-safe. Two pulls at once corrupt the conversion of the person's voice, which is
        /// the kind of defect that shows up as a bad transcript and is never traced back here.
        /// </remarks>
        private readonly Lock pullGate = new();

        private readonly float[] scratch = new float[CaptureDrainPolicy.ChunkSamples];
        private long frames;
        private long samples;
        private bool closed;

        public Session(
            int number,
            NAudio.CoreAudioApi.WasapiCapture device,
            WAVWriter writer,
            FrameSink sink,
            RecognitionSampleBuffer buffered,
            BufferedWaveProvider incoming,
            ISampleProvider pipeline,
            AudioStreamFormat deviceFormat)
        {
            Number = number;
            Device = device;
            Writer = writer;
            Sink = sink;
            Buffered = buffered;
            Incoming = incoming;
            Pipeline = pipeline;
            DeviceFormat = deviceFormat;
            StartedAt = Stopwatch.GetTimestamp();
        }

        public int Number { get; }

        public NAudio.CoreAudioApi.WasapiCapture Device { get; }

        public WAVWriter Writer { get; }

        public FrameSink Sink { get; }

        public RecognitionSampleBuffer Buffered { get; }

        public BufferedWaveProvider Incoming { get; }

        public ISampleProvider Pipeline { get; }

        public AudioStreamFormat DeviceFormat { get; }

        public bool IsResampled => DeviceFormat.SampleRate != RecordingFormat.SampleRate
            || DeviceFormat.Channels != RecordingFormat.Channels;

        public long StartedAt { get; }

        public TaskCompletionSource<bool> FirstFrame { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FailureOnce Failure { get; } = new();

        public Action<float[]> Deliver { get; set; } = static _ => { };

        public EventHandler<WaveInEventArgs>? OnData { get; set; }

        public EventHandler<StoppedEventArgs>? OnStopped { get; set; }

        /// <summary>Time from the start of the engine to the first sample that reached the file.</summary>
        public TimeSpan? StartupLatency { get; private set; }

        /// <summary>What the last pull could not convert, if anything.</summary>
        public AudioCaptureError? PullFailure { get; private set; }

        public long Frames => Interlocked.Read(ref frames);

        public long Samples => Interlocked.Read(ref samples);

        public bool IsClosed { get { lock (pullGate) { return closed; } } }

        /// <summary>
        /// Take the device's bytes and pull whatever the conversion can produce from them.
        /// </summary>
        /// <returns><c>false</c> when the buffer overflowed - the drain has stopped.</returns>
        public bool Accept(byte[] buffer, int count, out float peak, out bool firstFrame)
        {
            peak = 0;
            firstFrame = false;
            lock (pullGate)
            {
                if (closed) { return true; }

                try
                {
                    Incoming.AddSamples(buffer, 0, count);
                }
                catch (InvalidOperationException)
                {
                    return false;
                }

                Pull(out peak, out firstFrame);
            }

            return true;
        }

        /// <summary>One last pull, from the stop.</summary>
        public void PullOnce(out float peak, out bool firstFrame)
        {
            lock (pullGate)
            {
                if (closed)
                {
                    peak = 0;
                    firstFrame = false;
                    return;
                }

                Pull(out peak, out firstFrame);
            }
        }

        /// <summary>
        /// Refuse every further frame. Irreversible, and the first thing a stop does.
        /// </summary>
        public void Close()
        {
            lock (pullGate) { closed = true; }
        }

        /// <summary>The gate must be held.</summary>
        private void Pull(out float peak, out bool firstFrame)
        {
            peak = 0;
            firstFrame = false;
            int chunks = 0;
            while (true)
            {
                int read;
                try
                {
                    read = Pipeline.Read(scratch, 0, CaptureDrainPolicy.ChunkSamples);
                }
                catch (Exception error)
                {
                    PullFailure = new AudioCaptureError.UnsupportedAudioFormat(error.Message);
                    return;
                }

                if (read > 0)
                {
                    if (StartupLatency is null)
                    {
                        StartupLatency = Stopwatch.GetElapsedTime(StartedAt);
                        firstFrame = true;
                        // Here rather than at the call site: the last pull of a stop can be the
                        // one that carries the first sample, and whoever is waiting to be told
                        // "speak" has to be released by that one too. Continuations are
                        // asynchronous by construction, so this is safe under the pull's lock.
                        FirstFrame.TrySetResult(true);
                    }

                    var frame = new float[read];
                    Array.Copy(scratch, frame, read);
                    peak = Math.Max(peak, Peak(frame));
                    Deliver(frame);
                    Interlocked.Increment(ref frames);
                    Interlocked.Add(ref samples, read);
                }

                chunks += 1;
                if (CaptureDrainPolicy.Next(read, CaptureDrainPolicy.ChunkSamples, chunks)
                    is CaptureDrainStop stop and not CaptureDrainStop.Continue)
                {
                    if (stop == CaptureDrainStop.BudgetSpent)
                    {
                        // The provider answered every read in full past the point where the device
                        // could possibly have held that much. Not a wedge any more - just a pull
                        // that ends, and a line saying it did.
                        DiagnosticLog.Emit(
                            "capture.pull-budget-spent",
                            ("session", Number),
                            ("chunks", chunks));
                    }

                    return;
                }
            }
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
