using System.Collections.Concurrent;
using DictationCore;
using DictationCore.Audio;
using OpenRamble.AppCore;

namespace OpenRamble.LinuxAudio;

/// <summary>
/// One side of a meeting, recorded by <c>pw-record</c>: the microphone, or what the default output
/// plays.
/// </summary>
/// <remarks>
/// <para>
/// The same recorder the dictation capture runs (<see cref="PwRecordProcess"/>), so the format,
/// the tool lookup and the stop by signal are shared rather than repeated. What differs is the
/// consumer: blocks go straight to the meeting pipeline, each stamped with the host clock at its
/// first frame, and a recorder that dies on its own is a changed device rather than a failed
/// recording - the meeting restarts the source and marks the gap.
/// </para>
/// <para>
/// <b>Timestamps.</b> <see cref="MeetingAudioBlock.HostNanosecondsNow"/> when a read returns,
/// minus the block's own duration, so they mark the first frame on the clock the capture anchors
/// on. The pipe adds a few milliseconds of jitter; <see cref="ChannelClock"/> absorbs it, and a
/// stall stamps late only for the burst that catches up, which it also absorbs.
/// </para>
/// <para>
/// <b>Threads.</b> The reader only reads and stamps; blocks and the failure go to a second
/// thread, in order, which runs the callbacks. A slow consumer - a disk write, a segment handed to
/// a busy queue - then delays delivery, never the read, so the stamps stay honest.
/// </para>
/// </remarks>
public sealed class PipeWireMeetingSource : IMeetingAudioSource
{
    public enum SourceKind
    {
        Microphone,
        SystemAudio,
    }

    /// <summary>The meeting's format: 16 kHz mono, asked of the server.</summary>
    private static readonly AudioStreamFormat Format = new(16_000, 1);

    /// <summary>How long a stop waits for SIGTERM before SIGKILL.</summary>
    private static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(1);

    /// <summary>How long a dead recorder gets to finish saying why, before it is quoted.</summary>
    private static readonly TimeSpan DeathRattle = TimeSpan.FromMilliseconds(250);

    private readonly Lock gate = new();
    private readonly RecorderProcessLauncher launch;
    private readonly Func<ulong> clock;
    private string? target;
    private string? deviceName;
    private Run? current;

    /// <summary>The same source with the recorder and the clock replaced - the seam tests drive.</summary>
    /// <param name="clock">Host nanoseconds, as <see cref="MeetingAudioBlock.HostNanosecondsNow"/> returns them.</param>
    internal PipeWireMeetingSource(
        SourceKind kind,
        string? target,
        string? deviceName,
        RecorderProcessLauncher launch,
        Func<ulong>? clock = null)
    {
        Kind = kind;
        this.target = target;
        this.deviceName = deviceName;
        this.launch = launch;
        this.clock = clock ?? MeetingAudioBlock.HostNanosecondsNow;
    }

    public SourceKind Kind { get; }

    public string? DeviceName
    {
        get { lock (gate) { return deviceName; } }
    }

    /// <summary>The microphone, through the same <c>pw-record</c> setup dictation uses.</summary>
    /// <param name="targetNodeName">The chosen input's <c>node.name</c>, or <c>null</c> for the default.</param>
    /// <param name="deviceName">The chosen input's name, for <c>meta.json</c>.</param>
    public static PipeWireMeetingSource Microphone(string? targetNodeName, string? deviceName) =>
        new(SourceKind.Microphone, targetNodeName, deviceName,
            chosen => LaunchRecorder(chosen, captureSink: false));

    /// <summary>The default output's monitor - the other side of the call. Follows the default sink.</summary>
    public static PipeWireMeetingSource SystemAudio() =>
        new(SourceKind.SystemAudio, target: null, deviceName: null,
            _ => LaunchRecorder(target: null, captureSink: true));

    /// <summary>
    /// <see cref="PwRecordProcess.Launch"/>, with its one error told apart: no tool is
    /// <see cref="MeetingSourceFailure.Unavailable"/>, a tool that would not start is
    /// <see cref="MeetingSourceFailure.StartFailed"/>.
    /// </summary>
    private static IRecorderProcess LaunchRecorder(string? target, bool captureSink)
    {
        try
        {
            return PwRecordProcess.Launch(Format, target, captureSink);
        }
        catch (AudioCaptureError.EngineUnavailable error)
        {
            MeetingSourceFailure failure = ExecutablePath.Find(PwRecordProcess.Executable) is null
                ? new MeetingSourceFailure.Unavailable(error.Message)
                : new MeetingSourceFailure.StartFailed(error.Message);
            throw new MeetingSourceException(failure);
        }
    }

    /// <exception cref="MeetingSourceException">
    /// <see cref="MeetingSourceFailure.Unavailable"/> without <c>pw-record</c>,
    /// <see cref="MeetingSourceFailure.StartFailed"/> when it would not start.
    /// </exception>
    public void Start(Action<MeetingAudioBlock> onBlock, Action<MeetingSourceFailure> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onBlock);
        ArgumentNullException.ThrowIfNull(onFailure);

        Run? ended;
        string? chosen;
        lock (gate)
        {
            if (current is { HasFailed: false })
            {
                throw new InvalidOperationException("The meeting source is already running.");
            }

            // A run that failed on its own has stopped delivering but still holds its recorder.
            ended = current;
            current = null;
            chosen = Kind == SourceKind.Microphone ? target : null;
        }

        if (ended is not null) { Release(ended); }

        IRecorderProcess recorder;
        try
        {
            recorder = launch(chosen);
        }
        catch (MeetingSourceException error)
        {
            Emit("meeting.source.unavailable", ("message", error.Message));
            throw;
        }
        catch (AudioCaptureError.EngineUnavailable error)
        {
            Emit("meeting.source.unavailable", ("message", error.Message));
            throw new MeetingSourceException(new MeetingSourceFailure.Unavailable(error.Message));
        }

        var run = new Run(recorder);
        lock (gate) { current = run; }

        run.Delivery = new Thread(() => Deliver(run))
        {
            IsBackground = true,
            Name = $"OpenRamble meeting {Kind} delivery",
        };
        run.Delivery.Start();
        run.Reader = new Thread(() => Read(run, onBlock, onFailure))
        {
            IsBackground = true,
            Name = $"OpenRamble meeting {Kind}",
        };
        run.Reader.Start();
        Emit("meeting.source.start", ("device", chosen is null ? "default" : "chosen"));
    }

    public void Stop()
    {
        Run? run;
        lock (gate)
        {
            run = current;
            current = null;
        }

        if (run is null) { return; }

        Release(run);
    }

    /// <summary>Forget the chosen microphone: the next start records from the default.</summary>
    public void PrepareForRecovery()
    {
        if (Kind != SourceKind.Microphone) { return; }

        lock (gate)
        {
            target = null;
            deviceName = null;
        }
    }

    /// <summary>
    /// Host time of a block's first frame, in nanoseconds: the clock at arrival minus the block's
    /// own duration.
    /// </summary>
    internal static ulong FirstFrameNanoseconds(ulong now, int samples)
    {
        ulong duration = (ulong)samples * 1_000_000_000UL / (ulong)Format.SampleRate;
        return now > duration ? now - duration : 0UL;
    }

    private void Read(Run run, Action<MeetingAudioBlock> onBlock, Action<MeetingSourceFailure> onFailure)
    {
        MeetingSourceFailure failure;
        try
        {
            RawSampleStream.Pump(
                run.Recorder.Output,
                CaptureDrainPolicy.ChunkSamples,
                samples =>
                {
                    ulong stamp = FirstFrameNanoseconds(clock(), samples.Length);
                    if (run.Stopping) { return; }

                    foreach (float sample in samples)
                    {
                        if (!float.IsFinite(sample))
                        {
                            throw new InvalidDataException("pw-record delivered a non-finite sample");
                        }
                    }

                    var block = new MeetingAudioBlock(stamp, samples);
                    run.Post(() => onBlock(block));
                });

            if (run.Stopping) { return; }

            // Not asked to stop, so the device went away under it - unplugged, the server
            // restarted. A moment to let it die, so the log quotes its own reason.
            run.Recorder.WaitForExit(DeathRattle);
            Emit("meeting.source.exited", ("message", run.Recorder.ExitReport));
            failure = new MeetingSourceFailure.ConfigurationChanged();
        }
        catch (InvalidDataException error)
        {
            if (run.Stopping) { return; }

            Emit("meeting.source.bad-data", ("message", error.Message));
            failure = new MeetingSourceFailure.ConversionFailed(error.Message);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException
            or InvalidOperationException)
        {
            if (run.Stopping) { return; }

            Emit("meeting.source.exited", ("message", error.Message));
            failure = new MeetingSourceFailure.ConfigurationChanged();
        }

        if (!run.MarkFailed()) { return; }

        run.Post(() => onFailure(failure));
    }

    /// <summary>Run the callbacks the reader queued, in order, until the run is released.</summary>
    private static void Deliver(Run run)
    {
        foreach (Action next in run.Outbox.GetConsumingEnumerable()) { next(); }
    }

    /// <summary>Signal, then force; never waits longer than the budget. Safe twice.</summary>
    private static void Release(Run run)
    {
        run.Stopping = true;
        try
        {
            run.Recorder.RequestStop();
            if (!run.Recorder.WaitForExit(StopBudget))
            {
                run.Recorder.Kill();
                run.Recorder.WaitForExit(DeathRattle);
            }
        }
        finally
        {
            run.Recorder.Dispose();
        }

        // Joining from the reader itself - a stop called from a block or failure callback - would
        // wait for this very frame to return.
        Thread? reader = run.Reader;
        if (reader is not null && reader != Thread.CurrentThread)
        {
            reader.Join(StopBudget);
        }

        // What was read before the stop is still delivered before it returns: the last word.
        run.Outbox.CompleteAdding();
        Thread? delivery = run.Delivery;
        if (delivery is not null && delivery != Thread.CurrentThread)
        {
            delivery.Join(StopBudget);
        }
    }

    private void Emit(string name, params (string Key, object? Value)[] fields) =>
        DiagnosticLog.Emit(name, [("kind", Kind), .. fields]);

    private sealed class Run(IRecorderProcess recorder)
    {
        private int failed;
        private volatile bool stopping;

        public IRecorderProcess Recorder { get; } = recorder;

        public Thread? Reader { get; set; }

        public Thread? Delivery { get; set; }

        public BlockingCollection<Action> Outbox { get; } = new();

        /// <summary>Queue a callback; one posted after the run was released is dropped.</summary>
        public void Post(Action callback)
        {
            try
            {
                Outbox.Add(callback);
            }
            catch (InvalidOperationException)
            {
            }
        }

        public bool Stopping
        {
            get => stopping;
            set => stopping = value;
        }

        public bool HasFailed => Volatile.Read(ref failed) != 0;

        public bool MarkFailed() => Interlocked.Exchange(ref failed, 1) == 0;
    }
}
