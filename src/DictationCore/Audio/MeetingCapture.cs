using System.Diagnostics;

namespace DictationCore.Audio;

/// <summary>
/// What the app drives a long recording through (upstream <c>MeetingCapturing</c>): the real
/// <see cref="MeetingCapture"/>, or a test's stand-in.
/// </summary>
public interface IMeetingCapturing
{
    string Directory { get; }

    long FrameCount { get; }

    MeetingCapture.Levels CurrentLevels { get; }

    MeetingCapture.ChannelHealth Health(MeetingChannel channel);

    void Start();

    void Pause();

    void Resume();

    MeetingCapture.Summary Stop();

    void RecoverMicrophone();
}

/// <summary>
/// A long recording: two sources in, one stereo file out, for as long as it takes.
///
/// Nothing here holds audio in memory beyond a jitter window. Blocks arrive from the sources'
/// own threads, are placed on the shared timeline, and go to disk; the file on disk is the
/// recording, and everything else - levels for the meters, health for the honesty strip, the
/// summary at the end - is derived from what passed through.
///
/// Recording is structurally separate from transcription. Its only edge toward the engine is the
/// frame count it exposes and the segments it hands out. A stalled decoder cannot stop a
/// recording.
///
/// Thread-safe (the Swift original is an actor). Source failures and disk/write failures are
/// handled off the reporting thread, as the original hops onto the actor with a <c>Task</c>.
/// macOS's inaudible probe tone is not ported: on Linux there is no permission that can leave a
/// tap silent, so "no buffers within the grace" is the whole evidence.
/// </summary>
public sealed class MeetingCapture : IMeetingCapturing
{
    public enum CaptureState
    {
        Idle,
        Recording,
        Paused,
        Stopped,
    }

    /// <summary>Why the capture refused or stopped (the Swift original is an error enum).</summary>
    public abstract record Failure
    {
        private Failure() { }

        public sealed record NotIdle : Failure;

        public sealed record NotRecording : Failure;

        public sealed record CannotCreateDirectory(string Detail) : Failure;

        public sealed record WriteFailed(string Detail) : Failure;

        public sealed record DiskFull : Failure;

        public sealed record Microphone(MeetingSourceFailure SourceFailure) : Failure;

        public sealed record SystemAudio(MeetingSourceFailure SourceFailure) : Failure;
    }

    /// <summary>The loudest sample per channel over the last stretch, 0...1, for meters.</summary>
    public readonly record struct Levels(float Microphone, float System)
    {
        public static Levels Silent => new(0, 0);
    }

    /// <summary>
    /// Per-channel evidence for "is audio actually arriving?". The instants are
    /// <see cref="Stopwatch.GetTimestamp"/> readings (the Swift original uses
    /// <c>ContinuousClock.Instant</c>).
    /// </summary>
    public readonly record struct ChannelHealth(
        bool EverDeliveredBuffers,
        bool EverDeliveredAudio,
        long? LastBlockAt = null,
        long? LastAudibleAt = null)
    {
        public static ChannelHealth None => new(false, false);
    }

    /// <summary>What the recording turned out to be, handed back at stop.</summary>
    public sealed record Summary(
        long FrameCount,
        double Duration,
        string? MicrophoneDeviceName,
        bool MicrophoneEverDeliveredBuffers,
        bool MicrophoneEverDeliveredAudio,
        SystemAudioSummary SystemAudio,
        IReadOnlyList<MeetingGap> Gaps,
        IReadOnlyList<MeetingInterval> Pauses,
        MeetingEndReason EndReason);

    /// <summary>
    /// Anything above this counts as audio rather than line noise. Well below speech (the
    /// segmenter's floor is 0.005 on the far side) so a quiet voice there still proves the
    /// monitor alive.
    /// </summary>
    public const float AudibleThreshold = 0.0005f;

    private readonly Lock gate = new();
    private readonly IMeetingAudioSource? microphone;
    private readonly IMeetingAudioSource? systemAudio;
    private readonly MeetingPipeline pipeline;
    private readonly Action<Failure> onFailure;
    private CaptureState state = CaptureState.Idle;
    private DateTimeOffset? startedAt;
    private readonly List<MeetingInterval> pauses = [];
    private DateTimeOffset? pausedAt;
    private MeetingEndReason? endReason;
    private bool microphoneRecoveryAttempted;

    /// <summary>Restarts after a configuration change, per channel, within <see cref="RestartWindow"/>.</summary>
    private readonly Dictionary<MeetingChannel, Queue<long>> restarts = [];

    /// <summary>
    /// Bumped by every start of a channel, so a backed-off restart that a pause, a resume or a
    /// recovery overtook does nothing when its wait ends.
    /// </summary>
    private readonly Dictionary<MeetingChannel, int> startGeneration = [];

    /// <summary>
    /// A recorder that ends on its own reads as a moved device, so a server that is down would
    /// otherwise be respawned as fast as it can fail. Waits double from half a second; past this
    /// many restarts within <see cref="RestartWindow"/> the side is reported down instead.
    /// </summary>
    internal const int MaximumRestarts = 5;

    internal static readonly TimeSpan RestartWindow = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan FirstRestartDelay = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan LongestRestartDelay = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Failures noticed under <see cref="gate"/>, raised after it is released (like the pipeline's
    /// segment and level events) so a handler may call <see cref="Stop"/> synchronously.
    /// </summary>
    private readonly List<Failure> pendingFailures = [];

    /// <param name="directory">Where <c>audio.wav</c> and <c>peaks.bin</c> go; created if needed.</param>
    /// <param name="microphone">The person's side. <c>null</c> only in tests.</param>
    /// <param name="systemAudio">
    /// <c>null</c> records the microphone only, deliberately. The file still has two channels so
    /// the format never changes.
    /// </param>
    /// <param name="segmentParameters">
    /// <c>null</c> selects each channel's capture default; an explicit value applies unchanged to
    /// both channels.
    /// </param>
    /// <param name="onLevels">Meter updates, about ten a second, from a background thread.</param>
    /// <param name="onSegment">
    /// A stretch of one channel that is on disk and worth a decode. Delivered from a background
    /// thread, in file order per channel, including the tails at pause and stop. Both callbacks run
    /// with no capture lock held, so they may call <see cref="Pause"/>, <see cref="Stop"/> or
    /// <see cref="RecoverMicrophone"/>.
    /// </param>
    /// <param name="onFailure">
    /// The recording stopped itself, or one side did. Never called for something the caller asked
    /// for.
    /// </param>
    /// <param name="freeBytes">Free space at a path; <c>null</c> when the volume does not say.</param>
    /// <param name="audioSink">Receives exactly the PCM appended to the file, on the file's clock.</param>
    public MeetingCapture(
        string directory,
        IMeetingAudioSource? microphone,
        IMeetingAudioSource? systemAudio,
        MeetingSegmentPolicy.Parameters? segmentParameters = null,
        Action<Levels>? onLevels = null,
        Action<MeetingSegmentRef>? onSegment = null,
        Action<Failure>? onFailure = null,
        Func<string, long?>? freeBytes = null,
        IMeetingAudioBlockSink? audioSink = null)
    {
        Directory = directory;
        this.microphone = microphone;
        this.systemAudio = systemAudio;
        this.onFailure = onFailure ?? (_ => { });
        var active = new HashSet<MeetingChannel>();
        if (microphone is not null) { active.Add(MeetingChannel.Microphone); }
        if (systemAudio is not null) { active.Add(MeetingChannel.System); }
        Func<string, long?> query = freeBytes ?? FreeBytes;
        pipeline = new MeetingPipeline(
            new MeetingWriter(directory),
            active,
            segmentParameters,
            onLevels ?? (_ => { }),
            onSegment ?? (_ => { }),
            () => query(directory),
            audioSink);
        pipeline.OnDiskOrWriteFailure = failure => Task.Run(() => PipelineFailed(failure));
    }

    public string Directory { get; }

    public CaptureState State
    {
        get { lock (gate) { return state; } }
    }

    // MARK: - Reading

    /// <summary>Frames on disk: the recording's own clock, pauses excluded.</summary>
    public long FrameCount => pipeline.FrameCount;

    public double Duration => (double)pipeline.FrameCount / MeetingWriter.SampleRate;

    public Levels CurrentLevels => pipeline.Levels;

    public ChannelHealth Health(MeetingChannel channel) => pipeline.Health(channel);

    public string AudioPath => pipeline.AudioPath;

    /// <summary>The wait before a backed-off restart; tests replace it.</summary>
    internal Func<TimeSpan, Task> RestartDelay { get; init; } = Task.Delay;

    // MARK: - Lifecycle

    /// <exception cref="MeetingCaptureException">Not idle, disk too full, or the file or a voice
    /// note's microphone would not open.</exception>
    public void Start()
    {
        try
        {
            StartLocked();
        }
        finally
        {
            pipeline.DeliverPending();
            DeliverFailures();
        }
    }

    private void StartLocked()
    {
        lock (gate)
        {
            if (state != CaptureState.Idle) { throw new MeetingCaptureException(new Failure.NotIdle()); }
            if (pipeline.FreeBytes() is long free
                && MeetingDiskPolicy.VerdictToStart(free) is MeetingDiskPolicy.Verdict.TooLowToStart)
            {
                throw new MeetingCaptureException(new Failure.DiskFull());
            }
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new MeetingCaptureException(new Failure.CannotCreateDirectory(error.Message));
            }
            try
            {
                pipeline.Open();
            }
            catch (MeetingWriter.Failure error)
            {
                pipeline.Abandon();
                throw new MeetingCaptureException(new Failure.WriteFailed(error.Message));
            }
            pipeline.Anchor();
            try
            {
                StartSources();
            }
            catch
            {
                // The writer is already open when a source reports a device error. Close every
                // resource before propagating it so a retry never inherits an open descriptor or
                // partial state.
                StopSources();
                _ = pipeline.Finish();
                throw;
            }
            startedAt = DateTimeOffset.UtcNow;
            state = CaptureState.Recording;
        }
    }

    public void Pause()
    {
        lock (gate)
        {
            if (state != CaptureState.Recording) { throw new MeetingCaptureException(new Failure.NotRecording()); }
            StopSources();
            pipeline.Settle();
            pausedAt = DateTimeOffset.UtcNow;
            state = CaptureState.Paused;
        }
        pipeline.DeliverPending();
    }

    public void Resume()
    {
        lock (gate)
        {
            if (state != CaptureState.Paused) { throw new MeetingCaptureException(new Failure.NotRecording()); }
            // The timeline is recording time, not wall time: re-anchor so the next block lands
            // right after the last one written.
            pipeline.Anchor();
            StartSources();
            // Only once the sources run: a resume that throws leaves the pause open, and the
            // capture still paused.
            ClosePause();
            state = CaptureState.Recording;
        }
        DeliverFailures();
    }

    /// <summary>
    /// End the recording and make the file whole.
    ///
    /// The sources stop first, so nothing new arrives; then the pipeline flushes its jitter
    /// window and seals the file. That order is what keeps the last word: stopping the writer
    /// first would discard whatever was still in flight.
    /// </summary>
    public Summary Stop()
    {
        Summary summary = StopLocked();
        pipeline.DeliverPending();
        return summary;
    }

    private Summary StopLocked()
    {
        lock (gate)
        {
            if (state is not (CaptureState.Recording or CaptureState.Paused))
            {
                throw new MeetingCaptureException(new Failure.NotRecording());
            }
            StopSources();
            if (state == CaptureState.Paused) { ClosePause(); }
            state = CaptureState.Stopped;
            MeetingPipeline.Result result = pipeline.Finish();
            // A sealing failure is still a recording: whatever reached disk is kept, and the
            // reason travels in the summary rather than as a thrown error that would lose the
            // rest of it.
            MeetingEndReason reason = endReason
                ?? (result.Error is null ? MeetingEndReason.StoppedByUser : MeetingEndReason.WriteFailed);
            ChannelHealth microphoneHealth = pipeline.Health(MeetingChannel.Microphone);
            ChannelHealth systemHealth = pipeline.Health(MeetingChannel.System);
            return new Summary(
                result.FrameCount,
                (double)result.FrameCount / MeetingWriter.SampleRate,
                microphone?.DeviceName,
                microphoneHealth.EverDeliveredBuffers,
                microphoneHealth.EverDeliveredAudio,
                new SystemAudioSummary(
                    wasRequested: systemAudio is not null,
                    everDeliveredBuffers: systemHealth.EverDeliveredBuffers,
                    everDeliveredAudio: systemHealth.EverDeliveredAudio,
                    outputTransport: systemAudio?.DeviceName),
                result.Gaps,
                [.. pauses],
                reason);
        }
    }

    private void ClosePause()
    {
        if (pausedAt is { } paused && startedAt is { } started)
        {
            pauses.Add(new MeetingInterval(
                (paused - started).TotalSeconds,
                (DateTimeOffset.UtcNow - started).TotalSeconds));
        }
        pausedAt = null;
    }

    // MARK: - Sources

    private void StartSources()
    {
        if (microphone is not null)
        {
            try
            {
                StartSource(microphone, MeetingChannel.Microphone);
            }
            catch (MeetingSourceException error)
            {
                // A meeting still has the other side. Aborting would throw away everyone else
                // because our microphone would not start. A voice note has no other side, so it
                // remains a hard error.
                if (systemAudio is null)
                {
                    throw new MeetingCaptureException(new Failure.Microphone(error.Failure));
                }
                pipeline.MarkDown(MeetingChannel.Microphone, MeetingGap.GapReason.MicrophoneUnavailable);
                pendingFailures.Add(new Failure.Microphone(error.Failure));
            }
        }
        if (systemAudio is null) { return; }
        try
        {
            StartSource(systemAudio, MeetingChannel.System);
        }
        catch (MeetingSourceException error)
        {
            // The other side could not be started. The microphone is already recording and stays
            // that way: it costs the person a channel, never the recording, and that is said.
            pipeline.MarkDown(MeetingChannel.System, MeetingGap.GapReason.SystemAudioStalled);
            pendingFailures.Add(new Failure.SystemAudio(error.Failure));
        }
    }

    private void StartSource(IMeetingAudioSource source, MeetingChannel channel)
    {
        startGeneration[channel] = startGeneration.GetValueOrDefault(channel) + 1;
        source.Start(
            block => pipeline.Ingest(channel, block),
            failure => Task.Run(() => SourceFailed(channel, failure)));
    }

    /// <summary>Restart after a recovery or a device change; a failure is reported, not thrown.</summary>
    private void RestartSource(IMeetingAudioSource source, MeetingChannel channel)
    {
        try
        {
            StartSource(source, channel);
        }
        catch (Exception error)
        {
            MeetingSourceFailure failure = error is MeetingSourceException sourceError
                ? sourceError.Failure
                : new MeetingSourceFailure.StartFailed(error.Message);
            pendingFailures.Add(channel == MeetingChannel.Microphone
                ? new Failure.Microphone(failure)
                : new Failure.SystemAudio(failure));
        }
    }

    /// <summary>Raise the queued failures with no capture lock held.</summary>
    private void DeliverFailures()
    {
        Failure[] batch;
        lock (gate)
        {
            if (pendingFailures.Count == 0) { return; }
            batch = [.. pendingFailures];
            pendingFailures.Clear();
        }
        foreach (Failure failure in batch) { onFailure(failure); }
    }

    private void StopSources()
    {
        microphone?.Stop();
        systemAudio?.Stop();
    }

    /// <summary>
    /// The microphone has been delivering silence while the rest of the recording is clearly
    /// alive. Stop it, drop a pinned device, and start again on the default input. Once per
    /// recording: a second attempt that also hears nothing is a fact, not a loop.
    /// </summary>
    public void RecoverMicrophone()
    {
        lock (gate)
        {
            if (state != CaptureState.Recording || microphoneRecoveryAttempted || microphone is null) { return; }
            microphoneRecoveryAttempted = true;
            microphone.Stop();
            pipeline.MarkDown(MeetingChannel.Microphone, MeetingGap.GapReason.MicrophoneUnavailable);
            microphone.PrepareForRecovery();
            RestartSource(microphone, MeetingChannel.Microphone);
        }
        DeliverFailures();
    }

    private void SourceFailed(MeetingChannel channel, MeetingSourceFailure failure)
    {
        SourceFailedLocked(channel, failure);
        DeliverFailures();
    }

    private void SourceFailedLocked(MeetingChannel channel, MeetingSourceFailure failure)
    {
        lock (gate)
        {
            if (state != CaptureState.Recording) { return; }
            bool moved = failure is MeetingSourceFailure.ConfigurationChanged;
            pipeline.MarkDown(
                channel,
                channel == MeetingChannel.Microphone
                    ? MeetingGap.GapReason.MicrophoneUnavailable
                    : (moved ? MeetingGap.GapReason.SystemAudioRouteChange : MeetingGap.GapReason.SystemAudioStalled));
            IMeetingAudioSource? source = channel == MeetingChannel.Microphone ? microphone : systemAudio;
            if (!moved)
            {
                pendingFailures.Add(channel == MeetingChannel.Microphone
                    ? new Failure.Microphone(failure)
                    : new Failure.SystemAudio(failure));
                return;
            }
            // The device moved - unplugged, or the default changed. Start again on whatever is
            // default now, after a wait that grows while it keeps happening. The aligner fills the
            // seconds in between with silence and the gap is recorded, so the recording continues
            // rather than ending.
            if (source is null) { return; }
            source.Stop();
            long now = Stopwatch.GetTimestamp();
            Queue<long> recent = restarts.TryGetValue(channel, out Queue<long>? queued) ? queued : restarts[channel] = new();
            while (recent.Count > 0 && Stopwatch.GetElapsedTime(recent.Peek(), now) > RestartWindow) { _ = recent.Dequeue(); }
            if (recent.Count >= MaximumRestarts)
            {
                // It keeps going away: treat the side as down, as a source that failed outright.
                pendingFailures.Add(channel == MeetingChannel.Microphone
                    ? new Failure.Microphone(failure)
                    : new Failure.SystemAudio(failure));
                return;
            }
            TimeSpan delay = FirstRestartDelay * Math.Pow(2, recent.Count);
            if (delay > LongestRestartDelay) { delay = LongestRestartDelay; }
            recent.Enqueue(now);
            int generation = startGeneration.GetValueOrDefault(channel);
            _ = Task.Run(async () =>
            {
                await RestartDelay(delay).ConfigureAwait(false);
                RestartAfterBackoff(source, channel, generation);
            });
        }
    }

    private void RestartAfterBackoff(IMeetingAudioSource source, MeetingChannel channel, int generation)
    {
        lock (gate)
        {
            // Paused, stopped, or started again by someone else while this waited.
            if (state != CaptureState.Recording || startGeneration.GetValueOrDefault(channel) != generation) { return; }
            RestartSource(source, channel);
        }
        DeliverFailures();
    }

    private void PipelineFailed(MeetingPipeline.Failure failure)
    {
        PipelineFailedLocked(failure);
        DeliverFailures();
    }

    private void PipelineFailedLocked(MeetingPipeline.Failure failure)
    {
        lock (gate)
        {
            if (state != CaptureState.Recording) { return; }
            switch (failure)
            {
                case MeetingPipeline.Failure.DiskFull:
                    endReason = MeetingEndReason.DiskFull;
                    pendingFailures.Add(new Failure.DiskFull());
                    break;
                case MeetingPipeline.Failure.Write write:
                    endReason = MeetingEndReason.WriteFailed;
                    pendingFailures.Add(new Failure.WriteFailed(write.Detail));
                    break;
            }
        }
    }

    /// <summary>
    /// Free space on the volume the recording lives on: the nearest existing ancestor of
    /// <paramref name="path"/>, since the directory itself is created only at start.
    /// </summary>
    public static long? FreeBytes(string path)
    {
        try
        {
            string? existing = Path.GetFullPath(path);
            while (existing is not null && !System.IO.Directory.Exists(existing))
            {
                existing = Path.GetDirectoryName(existing);
            }
            return existing is null ? null : new DriveInfo(existing).AvailableFreeSpace;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>What <see cref="MeetingCapture"/> throws; carries the <see cref="MeetingCapture.Failure"/> value.</summary>
public sealed class MeetingCaptureException(MeetingCapture.Failure failure) : Exception(failure.ToString())
{
    public MeetingCapture.Failure Failure { get; } = failure;
}

/// <summary>
/// The part that runs off the capture's lock: owns the clocks, the aligner and the writer, so
/// blocks from two threads are placed in the order they are received and nothing is reordered by
/// scheduling. The Swift original is a serial dispatch queue; here one lock serialises the same
/// work on the delivering thread.
/// </summary>
internal sealed class MeetingPipeline
{
    internal abstract record Failure
    {
        private Failure() { }

        public sealed record DiskFull : Failure;

        public sealed record Write(string Detail) : Failure;
    }

    internal sealed record Result(long FrameCount, IReadOnlyList<MeetingGap> Gaps, MeetingWriter.Failure? Error);

    /// <summary>
    /// Microphone frames wait a fifth of a second before reaching the segmenter. The gate decides
    /// over a window and its decision belongs to the whole window, but blocks arrive a few
    /// milliseconds at a time and the first of a remote sentence are judged before there is
    /// anything to match against. Held back, they are still here to be told silence when the
    /// match arrives; released too soon, one of them is enough for the segmenter to bring the
    /// sentence back as a paragraph. Frames reach the segmenter in order and in full; only later.
    /// </summary>
    internal const int MicrophoneLookaheadFrames = 3_200;

    private readonly Lock queue = new();
    private readonly MeetingWriter writer;
    private readonly HashSet<MeetingChannel> activeChannels;
    private readonly Action<MeetingCapture.Levels> onLevels;
    private readonly Action<MeetingSegmentRef> onSegment;
    private readonly Func<long?> freeBytesQuery;
    private readonly IMeetingAudioBlockSink? audioSink;

    /// <summary>
    /// One per active channel, fed with what was written - so a segment is a position in the
    /// file, never a position in memory.
    /// </summary>
    private readonly Dictionary<MeetingChannel, MeetingSegmentPolicy> policies = [];
    private readonly CrossTalkGate crossTalk = new();
    private readonly List<(float Peak, int Count)> heldMicrophone = [];
    private int heldMicrophoneFrames;

    // Owned by `queue`.
    private readonly Dictionary<MeetingChannel, ChannelClock> clocks = [];
    private readonly DualChannelAligner aligner;
    private ulong anchorNanoseconds;
    private long anchorFrame;
    private bool failed;
    private readonly List<MeetingGap> gaps = [];
    private readonly Dictionary<MeetingChannel, (long Frame, MeetingGap.GapReason Reason)> downSince = [];
    private long nextDiskCheckFrame = MeetingDiskPolicy.CheckEveryFrames;
    private float levelMicrophone;
    private float levelSystem;
    private int levelWindowFrames;

    // Read from any thread.
    private readonly Lock published = new();
    private MeetingCapture.Levels publishedLevels = MeetingCapture.Levels.Silent;
    private readonly Dictionary<MeetingChannel, MeetingCapture.ChannelHealth> channelHealth = [];

    // Segments and levels wait here, in the order the queue produced them, and are raised with no
    // lock held: a consumer may stop, pause or recover the capture from inside a callback. One
    // thread delivers at a time, so a channel's segments never overtake each other.
    private readonly Lock outboxGate = new();
    private readonly Queue<Action> outbox = new();
    private bool delivering;

    internal MeetingPipeline(
        MeetingWriter writer,
        HashSet<MeetingChannel> activeChannels,
        MeetingSegmentPolicy.Parameters? segmentParameters,
        Action<MeetingCapture.Levels> onLevels,
        Action<MeetingSegmentRef> onSegment,
        Func<long?> freeBytes,
        IMeetingAudioBlockSink? audioSink)
    {
        this.writer = writer;
        this.activeChannels = activeChannels;
        this.onLevels = onLevels;
        this.onSegment = onSegment;
        freeBytesQuery = freeBytes;
        this.audioSink = audioSink;
        aligner = new DualChannelAligner(activeChannels);
        foreach (MeetingChannel channel in activeChannels)
        {
            policies[channel] = new MeetingSegmentPolicy(
                channel,
                segmentParameters ?? MeetingSegmentPolicy.Parameters.Capture(channel));
        }
    }

    internal Action<Failure>? OnDiskOrWriteFailure { get; set; }

    internal string AudioPath => writer.AudioPath;

    internal long FrameCount => writer.FrameCount;

    internal MeetingCapture.Levels Levels
    {
        get { lock (published) { return publishedLevels; } }
    }

    internal MeetingCapture.ChannelHealth Health(MeetingChannel channel)
    {
        lock (published)
        {
            return channelHealth.GetValueOrDefault(channel, MeetingCapture.ChannelHealth.None);
        }
    }

    internal long? FreeBytes() => freeBytesQuery();

    internal void Open() => writer.Open();

    /// <summary>Close descriptors after an open failure without attempting to publish an incomplete recording.</summary>
    internal void Abandon()
    {
        lock (queue) { writer.Abandon(); }
    }

    /// <summary>
    /// Set "now" to be the frame after the last one written. Called at start and at every
    /// resume, so the timeline is recording time.
    /// </summary>
    internal void Anchor()
    {
        ulong now = MeetingAudioBlock.HostNanosecondsNow();
        lock (queue)
        {
            anchorNanoseconds = now;
            anchorFrame = writer.FrameCount;
            foreach (MeetingChannel channel in Enum.GetValues<MeetingChannel>())
            {
                clocks[channel] = new ChannelClock(startFrame: anchorFrame);
            }
            aligner.Reset(anchorFrame);
            crossTalk.Reset();
            heldMicrophone.Clear();
            heldMicrophoneFrames = 0;
            audioSink?.Anchor(now, anchorFrame);
        }
    }

    /// <summary>Let the pipeline run dry before a pause: what is pending is written and segmented.</summary>
    internal void Settle()
    {
        lock (queue)
        {
            if (aligner.Flush() is { } emission) { Write(emission); }
            FlushSegments();
        }
    }

    internal void Ingest(MeetingChannel channel, MeetingAudioBlock block)
    {
        lock (queue)
        {
            if (failed) { return; }
            float peak = Peak(block.Samples);
            NoteHealth(channel, peak);
            long? hostFrame = null;
            if (block.HostNanoseconds is ulong ns)
            {
                double elapsed = (double)unchecked((long)(ns - anchorNanoseconds)) / 1_000_000_000;
                hostFrame = anchorFrame + (long)Math.Round(elapsed * MeetingWriter.SampleRate);
            }
            if (!clocks.TryGetValue(channel, out ChannelClock? clock))
            {
                clock = new ChannelClock();
                clocks[channel] = clock;
            }
            ChannelClock.Placement placement = clock.Place(hostFrame, block.Samples.Length);
            CloseGapIfDown(channel);
            aligner.Ingest(channel, placement.StartFrame, block.Samples);
            if (aligner.Drain() is { } emission) { Write(emission); }
            AccumulateLevels(channel, peak, block.Samples.Length);
        }
        DeliverPending();
    }

    /// <summary>
    /// Raise the queued segments and levels. Call with no lock held. Returns at once when another
    /// thread - or an outer frame of this one, a callback that paused - is already delivering:
    /// that delivery picks up whatever was queued meanwhile.
    /// </summary>
    internal void DeliverPending()
    {
        lock (outboxGate)
        {
            if (delivering) { return; }
            delivering = true;
        }
        try
        {
            while (true)
            {
                Action next;
                lock (outboxGate)
                {
                    if (!outbox.TryDequeue(out Action? queued))
                    {
                        delivering = false;
                        return;
                    }
                    next = queued;
                }
                next();
            }
        }
        catch
        {
            lock (outboxGate) { delivering = false; }
            throw;
        }
    }

    internal void MarkDown(MeetingChannel channel, MeetingGap.GapReason reason)
    {
        lock (queue)
        {
            _ = downSince.TryAdd(channel, (writer.FrameCount, reason));
        }
    }

    internal Result Finish()
    {
        lock (queue)
        {
            // A writer that already failed is not written to again: the flush would only re-raise
            // the same failure.
            if (!failed && aligner.Flush() is { } emission) { Write(emission); }
            FlushSegments();
            foreach ((MeetingChannel channel, (long frame, MeetingGap.GapReason reason)) in downSince)
            {
                gaps.Add(new MeetingGap(channel, Seconds(frame), Seconds(writer.FrameCount), reason));
            }
            downSince.Clear();
            MeetingWriter.Failure? writeError = null;
            try
            {
                writer.Finish();
            }
            catch (MeetingWriter.Failure failure)
            {
                writeError = failure;
            }
            return new Result(writer.FrameCount, [.. gaps], writeError);
        }
    }

    // MARK: - Under the queue lock

    private void Write(DualChannelAligner.Emission emission)
    {
        long startFrame = writer.FrameCount;
        try
        {
            writer.Append(emission.Microphone, emission.System);
        }
        catch (MeetingWriter.Failure error)
        {
            failed = true;
            OnDiskOrWriteFailure?.Invoke(new Failure.Write(error.Message));
            return;
        }
        audioSink?.Receive(emission.Microphone, emission.System, startFrame);
        // Only after the append: a segment must never name frames that are not on disk yet.
        //
        // With both sides recorded, the speakers leak into the microphone; a block where the
        // system side is clearly the louder one is not the person speaking, and the microphone
        // segmenter is told silence for it. The file keeps the audio either way.
        bool bothSides = activeChannels.Contains(MeetingChannel.System);
        bool microphoneIsEcho = bothSides
            && crossTalk.Classify(emission.Microphone, emission.System).IsEcho;
        foreach (MeetingChannel channel in activeChannels)
        {
            float[] samples = channel == MeetingChannel.Microphone ? emission.Microphone : emission.System;
            float peak = Peak(samples);
            if (channel == MeetingChannel.Microphone && bothSides)
            {
                HoldMicrophone(peak, samples.Length, microphoneIsEcho);
            }
            else if (policies[channel].Observe(peak, samples.Length) is { } segment)
            {
                Post(() => onSegment(segment));
            }
        }
        if (writer.FrameCount >= nextDiskCheckFrame)
        {
            nextDiskCheckFrame = writer.FrameCount + MeetingDiskPolicy.CheckEveryFrames;
            if (freeBytesQuery() is long free && MeetingDiskPolicy.MustStop(free))
            {
                failed = true;
                OnDiskOrWriteFailure?.Invoke(new Failure.DiskFull());
            }
        }
    }

    private void FlushSegments()
    {
        ReleaseMicrophone(keeping: 0);
        foreach (MeetingChannel channel in activeChannels)
        {
            if (policies[channel].Flush() is { } tail) { Post(() => onSegment(tail)); }
        }
    }

    private void HoldMicrophone(float peak, int count, bool isEcho)
    {
        heldMicrophone.Add((isEcho ? 0 : peak, count));
        heldMicrophoneFrames += count;
        if (isEcho)
        {
            for (int index = 0; index < heldMicrophone.Count; index++)
            {
                heldMicrophone[index] = (0, heldMicrophone[index].Count);
            }
        }
        ReleaseMicrophone(MicrophoneLookaheadFrames);
    }

    private void ReleaseMicrophone(int keeping)
    {
        while (heldMicrophone.Count > 0 && heldMicrophoneFrames - heldMicrophone[0].Count >= keeping)
        {
            (float peak, int count) = heldMicrophone[0];
            heldMicrophone.RemoveAt(0);
            heldMicrophoneFrames -= count;
            if (policies.TryGetValue(MeetingChannel.Microphone, out MeetingSegmentPolicy? policy)
                && policy.Observe(peak, count) is { } segment)
            {
                Post(() => onSegment(segment));
            }
        }
    }

    private void CloseGapIfDown(MeetingChannel channel)
    {
        if (!downSince.Remove(channel, out (long Frame, MeetingGap.GapReason Reason) down)) { return; }
        gaps.Add(new MeetingGap(channel, Seconds(down.Frame), Seconds(writer.FrameCount), down.Reason));
    }

    private static double Seconds(long frame) => (double)frame / MeetingWriter.SampleRate;

    private static float Peak(float[] samples)
    {
        float peak = 0;
        foreach (float sample in samples) { peak = Math.Max(peak, Math.Abs(sample)); }
        return peak;
    }

    private void NoteHealth(MeetingChannel channel, float peak)
    {
        long now = Stopwatch.GetTimestamp();
        lock (published)
        {
            MeetingCapture.ChannelHealth health = channelHealth.GetValueOrDefault(channel, MeetingCapture.ChannelHealth.None);
            health = health with { EverDeliveredBuffers = true, LastBlockAt = now };
            if (peak >= MeetingCapture.AudibleThreshold)
            {
                health = health with { EverDeliveredAudio = true, LastAudibleAt = now };
            }
            channelHealth[channel] = health;
        }
    }

    /// <summary>Publish the loudest sample per channel about ten times a second.</summary>
    private void AccumulateLevels(MeetingChannel channel, float peak, int frames)
    {
        if (channel == MeetingChannel.Microphone)
        {
            levelMicrophone = Math.Max(levelMicrophone, peak);
        }
        else
        {
            levelSystem = Math.Max(levelSystem, peak);
        }
        levelWindowFrames += frames;
        if (levelWindowFrames < PeakFile.DefaultFramesPerBucket) { return; }
        var levels = new MeetingCapture.Levels(levelMicrophone, levelSystem);
        levelMicrophone = 0;
        levelSystem = 0;
        levelWindowFrames = 0;
        lock (published) { publishedLevels = levels; }
        Post(() => onLevels(levels));
    }

    private void Post(Action raise)
    {
        lock (outboxGate) { outbox.Enqueue(raise); }
    }
}
