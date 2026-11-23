using System.Diagnostics;
using DictationCore;
using DictationCore.Audio;
using LocalASR;

namespace OpenRamble.AppCore;

/// <summary>What the recorder is doing. Orthogonal to dictation: both can be true at once.</summary>
public enum MeetingRecordingState
{
    Idle,
    Starting,
    Recording,
    Paused,
    Stopping,
}

/// <summary>
/// Live recordings - meetings (microphone and what the computer plays) and voice notes
/// (microphone only) - and their transcripts (upstream AppState.swift, "Recordings" and "Live
/// transcript").
/// </summary>
/// <remarks>
/// <para>
/// Threading. Start, stop, pause and resume are serialised by <see cref="recordingLifecycle"/>
/// and return a task; the UI may fire and forget. Capture callbacks (levels, segments, failures)
/// and transcription results arrive on background threads; properties are written under
/// <c>gate</c> and <see cref="PropertyChanged"/> is raised on that thread, as elsewhere. The live
/// transcript and every store write or move of the recording's directory happen under
/// <see cref="meetingGate"/>, so a transcript write never lands in a directory being published.
/// Nothing calls into the capture while holding either lock.
/// </para>
/// <para>
/// Not ported: the macOS system-audio intro sheet and its remembered "declined" choice, the probe
/// tone, screen recording, and the pending-model-switch hand-off (the port switches models
/// elsewhere).
/// </para>
/// </remarks>
public sealed partial class AppState
{
    private static readonly TimeSpan LiveTickInterval = TimeSpan.FromSeconds(1);

    private MeetingCaptureFactory? makeMeetingCapture;
    private bool supportsSystemAudio;
    private IAccessibilityAnnouncing? announcer;
    private IImportRecognizer? importRecognizer;
    private readonly SemaphoreSlim recordingLifecycle = new(1, 1);
    private readonly Lock meetingGate = new();

    private IMeetingCapturing? meetingCapture;
    private MeetingRecordingState recordingState = MeetingRecordingState.Idle;
    private MeetingRecordingMetadata? liveRecording;
    private TimeSpan liveDuration;
    private MeetingCapture.Levels liveLevels = MeetingCapture.Levels.Silent;
    private CaptureHealth liveCaptureHealth = new CaptureHealth.NotRequested();
    private MicrophoneHealth liveMicrophoneHealth = new MicrophoneHealth.Idle();
    private string? systemAudioStartFailure;
    private string? microphoneStartFailure;
    private string? lastAnnouncedHealth;
    private string? lastAnnouncedMicrophoneHealth;

    /// <summary>A <see cref="Stopwatch"/> reading: when the sources last started or resumed.</summary>
    private long liveRecordingStartedAt;
    private Timer? liveTimer;

    /// <summary>
    /// The transcripts being written, oldest first: the live recording's, and those of recordings
    /// already stopped whose backlog is still draining. Under <see cref="meetingGate"/>.
    /// </summary>
    private readonly List<TranscriptionJob> transcriptionJobs = [];
    private Guid? transcribingRecordingId;
    private double transcriptBacklogSeconds;
    private bool isTranscriptionPaused;

    /// <summary>
    /// Recordings, from their start until their transcript is done, that need the engine: one
    /// hold each, released exactly once.
    /// </summary>
    private int recordingEngineHolds;

    /// <summary>
    /// A recording or its transcript needs the engine: the idle unload waits. Read under
    /// <c>gate</c> by the countdown, like the importer's activity.
    /// </summary>
    private bool recordingHoldsEngine => Volatile.Read(ref recordingEngineHolds) > 0;

    /// <summary>Whether this build can record at all.</summary>
    public bool CanRecord => makeMeetingCapture is not null && meetingStore is not null;

    /// <summary>Whether a meeting can also capture what the computer plays.</summary>
    public bool SupportsSystemAudio => supportsSystemAudio;

    public MeetingRecordingState RecordingState
    {
        get { lock (gate) { return recordingState; } }
        private set
        {
            if (!Set(ref recordingState, value)) { return; }
            Raise(nameof(IsRecording));
        }
    }

    /// <summary>Recording or paused: what a quit has to ask about, and what the tray badges.</summary>
    public bool IsRecording => RecordingState is MeetingRecordingState.Recording or MeetingRecordingState.Paused;

    /// <summary>The recording in progress, once it has actually started.</summary>
    public MeetingRecordingMetadata? LiveRecording
    {
        get { lock (gate) { return liveRecording; } }
        private set
        {
            if (!Set(ref liveRecording, value)) { return; }
            Raise(nameof(LiveRecordingId));
        }
    }

    /// <summary>The Library's pinned row.</summary>
    public Guid? LiveRecordingId => LiveRecording?.Id;

    /// <summary>Time on disk so far, refreshed once a second while recording.</summary>
    public TimeSpan LiveDuration
    {
        get { lock (gate) { return liveDuration; } }
        private set => Set(ref liveDuration, value);
    }

    /// <summary>The loudest sample per channel over the last stretch, for the meters (~10 Hz).</summary>
    public MeetingCapture.Levels LiveLevels
    {
        get { lock (gate) { return liveLevels; } }
        private set => Set(ref liveLevels, value);
    }

    /// <summary>Whether the other side is actually arriving, refreshed once a second.</summary>
    public CaptureHealth LiveCaptureHealth
    {
        get { lock (gate) { return liveCaptureHealth; } }
        private set => Set(ref liveCaptureHealth, value);
    }

    /// <summary>Whether the microphone is actually arriving. Independent of the other side.</summary>
    public MicrophoneHealth LiveMicrophoneHealth
    {
        get { lock (gate) { return liveMicrophoneHealth; } }
        private set => Set(ref liveMicrophoneHealth, value);
    }

    /// <summary>
    /// Which recording's transcript is being written live (during and just after it): the oldest
    /// one still being written, when a stopped recording's backlog drains while the next records.
    /// </summary>
    public Guid? TranscribingRecordingId
    {
        get { lock (gate) { return transcribingRecordingId; } }
    }

    /// <summary>
    /// Seconds of speech waiting for the engine, measured from what went in and came back - over
    /// every transcript being written, since they wait for the same engine.
    /// </summary>
    public double TranscriptBacklogSeconds
    {
        get { lock (gate) { return transcriptBacklogSeconds; } }
    }

    /// <summary>A queue stopped itself after repeated failures. The audio is safe.</summary>
    public bool IsTranscriptionPaused
    {
        get { lock (gate) { return isTranscriptionPaused; } }
    }

    private void SetUpRecording(AppEnvironment environment)
    {
        makeMeetingCapture = environment.MakeMeetingCapture;
        supportsSystemAudio = environment.SupportsSystemAudio;
        announcer = environment.Announcer;
        importRecognizer = environment.ImportRecognizer;
    }

    // MARK: - Lifecycle

    /// <summary>
    /// Start recording: the microphone, and - unless <paramref name="includeSystemAudio"/> is
    /// false (a voice note) or this build cannot - what the computer plays. Refuses nothing
    /// silently: no microphone permission, a full disk, a microphone that will not start on a voice
    /// note each say why. A side that will not start on a meeting costs that side, not the recording.
    /// </summary>
    public async Task StartRecording(bool includeSystemAudio = true)
    {
        if (makeMeetingCapture is not { } make || meetingStore is not { } store) { return; }
        await recordingLifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (RecordingState != MeetingRecordingState.Idle) { return; }
            if (!MicrophoneGranted)
            {
                Notify(new DictationNotice(DictationNoticeKind.Warning, "Allow the microphone to record."));
                RequestMicrophone();
                return;
            }

            bool system = includeSystemAudio && supportsSystemAudio;
            if (MeetingCapture.FreeBytes(store.IncompleteRoot) is long free
                && MeetingDiskPolicy.VerdictToStart(free) is MeetingDiskPolicy.Verdict.Low low)
            {
                Notify(new DictationNotice(
                    DictationNoticeKind.Warning,
                    $"Space for about {low.MinutesLeft} minutes of recording is left."));
            }

            // Before any synchronous setup: the countdown must not unload the engine under it.
            Interlocked.Increment(ref recordingEngineHolds);
            CancelIdleUnload();
            MeetingRecordingMetadata metadata = MeetingRecordingMetadata.Create(
                now(),
                new SystemAudioSummary(wasRequested: system),
                transcriptionState: MeetingTranscriptionState.Live);
            string directory = store.IncompleteDirectory(metadata.Id);
            try
            {
                lock (meetingGate) { store.Write(metadata, incomplete: true); }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ReleaseRecordingEngineHold();
                Notify(new DictationNotice(DictationNoticeKind.Failure, $"Couldn't start recording: {error.Message}"));
                return;
            }

            // Off the caller's thread, which may be the UI's: resolving the microphone may start
            // a process.
            string? deviceUid = InputDeviceUid;
            IMeetingCapturing capture;
            try
            {
                capture = await Task.Run(() => make(
                    directory,
                    deviceUid,
                    system,
                    levels => LiveLevels = levels,
                    segment => SegmentReady(metadata.Id, segment),
                    // Off the reporting thread: a failure may stop the recording, which waits for
                    // the lifecycle this start may still hold.
                    failure => _ = Task.Run(() => RecordingFailed(failure)))).ConfigureAwait(false);
            }
            catch
            {
                // Nothing will finish this recording, so nothing else would release its hold.
                ReleaseRecordingEngineHold();
                throw;
            }
            lock (gate)
            {
                meetingCapture = capture;
                systemAudioStartFailure = null;
                microphoneStartFailure = null;
                lastAnnouncedHealth = null;
                lastAnnouncedMicrophoneHealth = null;
            }

            RecordingState = MeetingRecordingState.Starting;
            LiveCaptureHealth = system ? new CaptureHealth.Verifying() : new CaptureHealth.NotRequested();
            LiveMicrophoneHealth = new MicrophoneHealth.Verifying();
            BeginTranscription(metadata.Id, directory);
            try
            {
                await Task.Run(capture.Start).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                lock (gate) { meetingCapture = null; }
                RecordingState = MeetingRecordingState.Idle;
                LiveCaptureHealth = new CaptureHealth.NotRequested();
                LiveMicrophoneHealth = new MicrophoneHealth.Idle();
                AbandonTranscription(metadata.Id);
                // Nothing was recorded: the directory prepared for it is not a recording anyone
                // wants back.
                try { Directory.Delete(directory, recursive: true); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                Notify(new DictationNotice(DictationNoticeKind.Failure, RecordingStartFailureMessage(error)));
                return;
            }

            DiagnosticLog.Emit("recording.started", ("system_audio", system));
            Interlocked.Exchange(ref liveRecordingStartedAt, Stopwatch.GetTimestamp());
            LiveDuration = TimeSpan.Zero;
            LiveRecording = metadata;
            RecordingState = MeetingRecordingState.Recording;
            StartLiveTimer();
        }
        finally
        {
            recordingLifecycle.Release();
        }
    }

    /// <summary>
    /// End the recording and file it. Whatever the recorder reports - a clean stop, a full disk,
    /// a writer that failed - the audio that reached disk is kept and the reason travels with it.
    /// The transcript catches up afterwards, then the WAV is compacted.
    /// </summary>
    public Task StopRecording() => StopRecording(quitting: false);

    /// <summary>
    /// The app is quitting and the person chose to stop: file the recording as ended by the quit.
    /// Completes when it is filed; the transcript's tail is left for the next launch.
    /// </summary>
    public Task StopRecordingBeforeQuit() => StopRecording(quitting: true);

    private async Task StopRecording(bool quitting)
    {
        await recordingLifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            IMeetingCapturing? capture;
            MeetingRecordingMetadata? live;
            lock (gate)
            {
                capture = meetingCapture;
                live = liveRecording;
            }

            if (!IsRecording || capture is null || live is null || meetingStore is not { } store) { return; }
            RecordingState = MeetingRecordingState.Stopping;
            StopLiveTimer();
            MeetingRecordingMetadata metadata = live with { };
            bool hasQueue;
            lock (meetingGate) { hasQueue = JobLocked(live.Id) is not null; }
            metadata.TranscriptionState = !hasQueue
                ? MeetingTranscriptionState.None
                : IsEngineReady && ModelStateValue.IsReady
                    ? MeetingTranscriptionState.Live
                    : MeetingTranscriptionState.WaitingForModel;
            try
            {
                MeetingCapture.Summary summary = await Task.Run(capture.Stop).ConfigureAwait(false);
                metadata.Duration = summary.Duration;
                metadata.MicrophoneDeviceName = summary.MicrophoneDeviceName;
                metadata.MicrophoneEverDeliveredAudio = summary.MicrophoneEverDeliveredAudio;
                metadata.SystemAudio = summary.SystemAudio;
                metadata.Gaps = [.. summary.Gaps];
                metadata.Pauses = [.. summary.Pauses];
                metadata.EndReason = summary.EndReason;
            }
            catch (Exception error) when (error is MeetingCaptureException or IOException)
            {
                metadata.EndReason = MeetingEndReason.WriteFailed;
            }

            if (quitting && metadata.EndReason == MeetingEndReason.StoppedByUser)
            {
                metadata.EndReason = MeetingEndReason.ApplicationQuit;
            }

            try
            {
                lock (meetingGate)
                {
                    store.Write(metadata, incomplete: true);
                    store.Publish(metadata.Id);
                    // Its transcript follows it into the list, and is guarded there like the rest.
                    if (JobLocked(metadata.Id) is { } job) { job.IsPublished = true; }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Notify(new DictationNotice(
                    DictationNoticeKind.Failure,
                    $"The recording ended but couldn't be filed: {error.Message}"));
            }

            lock (gate) { meetingCapture = null; }
            LiveRecording = null;
            LiveLevels = MeetingCapture.Levels.Silent;
            LiveCaptureHealth = new CaptureHealth.NotRequested();
            LiveMicrophoneHealth = new MicrophoneHealth.Idle();
            RecordingState = MeetingRecordingState.Idle;
            DiagnosticLog.Emit("recording.stopped", ("end_reason", metadata.EndReason?.ToString() ?? "none"));
            ReloadRecordings();
            LastFinishedRecordingId = metadata.Id;
            FinishTranscription(metadata.Id);
            if (EndNotice(metadata.EndReason) is { } notice) { Notify(notice); }
        }
        finally
        {
            recordingLifecycle.Release();
        }
    }

    public async Task PauseRecording()
    {
        await recordingLifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            IMeetingCapturing? capture;
            lock (gate) { capture = meetingCapture; }
            if (RecordingState != MeetingRecordingState.Recording || capture is null) { return; }
            try
            {
                await Task.Run(capture.Pause).ConfigureAwait(false);
            }
            catch (MeetingCaptureException)
            {
                Notify(new DictationNotice(DictationNoticeKind.Failure, "Couldn't pause the recording."));
                return;
            }

            // Paused sources need no polling, and a requested silence is not a failure.
            StopLiveTimer();
            RecordingState = MeetingRecordingState.Paused;
            LiveDuration = TimeSpan.FromSeconds((double)capture.FrameCount / MeetingWriter.SampleRate);
            LiveLevels = MeetingCapture.Levels.Silent;
        }
        finally
        {
            recordingLifecycle.Release();
        }
    }

    public async Task ResumeRecording()
    {
        await recordingLifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            IMeetingCapturing? capture;
            lock (gate) { capture = meetingCapture; }
            if (RecordingState != MeetingRecordingState.Paused || capture is null) { return; }
            try
            {
                await Task.Run(capture.Resume).ConfigureAwait(false);
            }
            catch (Exception error) when (error is MeetingCaptureException or MeetingSourceException)
            {
                Notify(new DictationNotice(DictationNoticeKind.Failure, RecordingStartFailureMessage(error)));
                return;
            }

            // The health grace starts again: the sources were just restarted.
            Interlocked.Exchange(ref liveRecordingStartedAt, Stopwatch.GetTimestamp());
            RecordingState = MeetingRecordingState.Recording;
            StartLiveTimer();
        }
        finally
        {
            recordingLifecycle.Release();
        }
    }

    // MARK: - Failures

    private void RecordingFailed(MeetingCapture.Failure failure)
    {
        DiagnosticLog.Emit("recording.failed", ("failure", failure.GetType().Name));
        switch (failure)
        {
            case MeetingCapture.Failure.DiskFull or MeetingCapture.Failure.WriteFailed:
                // The recorder has stopped writing on its own. End the recording so what reached
                // disk is filed and listed; the notice that follows says why.
                _ = StopRecording();
                break;
            case MeetingCapture.Failure.Microphone microphone:
            {
                string reason = Describe(microphone.SourceFailure);
                bool isMeeting;
                lock (gate)
                {
                    microphoneStartFailure = reason;
                    isMeeting = liveRecording?.IsMeeting == true || liveCaptureHealth is not CaptureHealth.NotRequested;
                }

                Notify(new DictationNotice(
                    DictationNoticeKind.Warning,
                    isMeeting
                        ? $"Your microphone isn't being captured ({reason}). The other side is still being recorded."
                        : $"The microphone stopped ({reason})."));
                break;
            }
            case MeetingCapture.Failure.SystemAudio system:
            {
                string reason = Describe(system.SourceFailure);
                lock (gate) { systemAudioStartFailure = reason; }
                Notify(new DictationNotice(
                    DictationNoticeKind.Warning,
                    $"The other side isn't being recorded ({reason}). Only your microphone is."));
                break;
            }
            default:
                Notify(new DictationNotice(DictationNoticeKind.Failure, $"Recording failed: {failure}"));
                break;
        }
    }

    private static string Describe(MeetingSourceFailure reason) => reason switch
    {
        MeetingSourceFailure.Unavailable unavailable => unavailable.Detail,
        MeetingSourceFailure.StartFailed failed => failed.Detail,
        MeetingSourceFailure.ConversionFailed conversion => conversion.Detail,
        MeetingSourceFailure.ConfigurationChanged => "the device changed",
        _ => reason.ToString(),
    };

    private static string RecordingStartFailureMessage(Exception error)
    {
        MeetingCapture.Failure? failure = error switch
        {
            MeetingCaptureException capture => capture.Failure,
            MeetingSourceException source => new MeetingCapture.Failure.Microphone(source.Failure),
            _ => null,
        };
        return failure switch
        {
            MeetingCapture.Failure.DiskFull => "Not enough free space to record. Free up at least 500 MB and try again.",
            MeetingCapture.Failure.Microphone microphone => $"Couldn't start the microphone: {Describe(microphone.SourceFailure)}.",
            MeetingCapture.Failure.SystemAudio system => $"Couldn't record the other side: {Describe(system.SourceFailure)}.",
            MeetingCapture.Failure.CannotCreateDirectory directory => $"Couldn't start recording: {directory.Detail}",
            MeetingCapture.Failure.WriteFailed write => $"Couldn't start recording: {write.Detail}",
            MeetingCapture.Failure.NotIdle => "A recording is already running.",
            MeetingCapture.Failure.NotRecording => "No recording is running.",
            _ => $"Couldn't start recording: {error.Message}",
        };
    }

    private static DictationNotice? EndNotice(MeetingEndReason? reason) => reason switch
    {
        MeetingEndReason.DiskFull => new DictationNotice(
            DictationNoticeKind.Warning,
            "Recording stopped: this computer ran out of space. Everything up to that moment was kept."),
        MeetingEndReason.WriteFailed => new DictationNotice(
            DictationNoticeKind.Warning,
            "Recording stopped: it could no longer be written. Everything up to that moment was kept."),
        _ => null,
    };

    // MARK: - Live duration and health

    private void StartLiveTimer()
    {
        lock (gate)
        {
            liveTimer?.Dispose();
            liveTimer = new Timer(_ => LiveTick(), null, LiveTickInterval, LiveTickInterval);
        }
    }

    private void StopLiveTimer()
    {
        lock (gate)
        {
            liveTimer?.Dispose();
            liveTimer = null;
        }
    }

    private void LiveTick()
    {
        IMeetingCapturing? capture;
        Guid? id;
        lock (gate)
        {
            if (recordingState != MeetingRecordingState.Recording) { return; }
            capture = meetingCapture;
            id = liveRecording?.Id;
        }

        if (capture is null) { return; }
        LiveDuration = TimeSpan.FromSeconds((double)capture.FrameCount / MeetingWriter.SampleRate);
        RefreshCaptureHealth(capture, id);
    }

    /// <summary>
    /// Is each side arriving? Announced when the answer changes: a blind person cannot see a meter
    /// that stopped moving, and this is the one thing about a recorder that must not be discovered
    /// afterwards.
    /// </summary>
    private void RefreshCaptureHealth(IMeetingCapturing capture, Guid? id)
    {
        MeetingCapture.ChannelHealth system = capture.Health(MeetingChannel.System);
        MeetingCapture.ChannelHealth microphone = capture.Health(MeetingChannel.Microphone);
        long now = Stopwatch.GetTimestamp();
        CaptureHealth nextSystem;
        MicrophoneHealth nextMicrophone;
        lock (gate)
        {
            if (recordingState != MeetingRecordingState.Recording || liveRecording is not { } live || live.Id != id)
            {
                return;
            }

            double elapsed = CaptureHealth.Seconds(Interlocked.Read(ref liveRecordingStartedAt), now);
            nextSystem = CaptureHealth.Make(
                supportsSystemAudio, live.SystemAudio.WasRequested, systemAudioStartFailure, elapsed, system, now);
            nextMicrophone = MicrophoneHealth.Make(microphoneStartFailure, elapsed, microphone, now);
        }

        // Once per recording: drop a pinned device and start again on the default input.
        if (nextMicrophone is MicrophoneHealth.Unheard) { capture.RecoverMicrophone(); }
        if (nextMicrophone != LiveMicrophoneHealth)
        {
            LiveMicrophoneHealth = nextMicrophone;
            if (nextMicrophone.Announcement is { } said && ChangeAnnounced(ref lastAnnouncedMicrophoneHealth, said))
            {
                announcer?.Announce(said, urgent: true);
            }
        }

        if (nextSystem == LiveCaptureHealth) { return; }
        LiveCaptureHealth = nextSystem;
        if (nextSystem.Announcement is { } announcement && ChangeAnnounced(ref lastAnnouncedHealth, announcement))
        {
            announcer?.Announce(announcement, urgent: nextSystem.Role == StatusColorRole.Attention);
        }
    }

    private bool ChangeAnnounced(ref string? last, string announcement)
    {
        lock (gate)
        {
            if (last == announcement) { return false; }
            last = announcement;
            return true;
        }
    }

    // MARK: - Live transcript

    /// <summary>
    /// One recording's transcript, from its start until its backlog has drained. Each recording
    /// has its own, so a new recording never takes the place of one still catching up; they
    /// share the engine through the turn they wait for. Fields under <see cref="meetingGate"/>.
    /// </summary>
    private sealed class TranscriptionJob(Guid id)
    {
        public Guid Id { get; } = id;

        public MeetingTranscriptionQueue Queue { get; set; } = null!;

        public MeetingUtteranceAssembler Assembler { get; } = new();

        public List<MeetingUtterance> Closed { get; } = [];

        /// <summary>The recording went to the Trash: nothing more is read, decoded or written.</summary>
        public CancellationTokenSource Cancellation { get; } = new();

        /// <summary>Filed into the list: its transcript is written there, not under .incomplete.</summary>
        public bool IsPublished { get; set; }

        public double BacklogSeconds { get; set; }

        public bool WriteFailureReported { get; set; }
    }

    /// <summary>Caller holds <see cref="meetingGate"/>.</summary>
    private TranscriptionJob? JobLocked(Guid id) => transcriptionJobs.Find(job => job.Id == id);

    /// <summary>Take the held segments up again after failures paused the queue.</summary>
    public void ResumeTranscription()
    {
        List<MeetingTranscriptionQueue> queues;
        lock (meetingGate) { queues = [.. transcriptionJobs.Select(job => job.Queue)]; }
        foreach (MeetingTranscriptionQueue queue in queues) { queue.Resume(); }
        PublishTranscriptionSummary();
    }

    /// <summary>
    /// Bring the transcript properties in line with the jobs: computed and written in one step,
    /// so two threads can never leave an older answer behind.
    /// </summary>
    private void PublishTranscriptionSummary()
    {
        lock (meetingGate)
        {
            Guid? oldest = transcriptionJobs.Count > 0 ? transcriptionJobs[0].Id : null;
            double backlog = transcriptionJobs.Sum(job => job.BacklogSeconds);
            bool paused = transcriptionJobs.Any(job => job.Queue.IsPaused);
            lock (gate)
            {
                transcribingRecordingId = oldest;
                transcriptBacklogSeconds = backlog;
                isTranscriptionPaused = paused;
            }
        }

        Raise(nameof(TranscribingRecordingId));
        Raise(nameof(TranscriptBacklogSeconds));
        Raise(nameof(IsTranscriptionPaused));
    }

    private void ReleaseRecordingEngineHold()
    {
        Interlocked.Decrement(ref recordingEngineHolds);
        RescheduleIdleUnload();
    }

    private void BeginTranscription(Guid id, string directory)
    {
        if (meetingStore is not { } store || importRecognizer is not { } recognizer) { return; }
        string incompleteAudio = Path.Combine(directory, MeetingWriter.AudioFileName);
        // Fillers go and the dictionary applies, as in an import; "scratch that" stays, because
        // in a meeting it is something a person said about their own words.
        ITextProcessing pipeline = MakeRecordingPipeline();
        DictationPriority? priority = dictationPriority;
        var job = new TranscriptionJob(id);
        CancellationToken cancelled = job.Cancellation.Token;
        job.Queue = new MeetingTranscriptionQueue(
            read: segment =>
            {
                // The file moves out of .incomplete/ when the recording ends: resolve where it is
                // now, every time, and not while it is moving. A trashed one is not read: the
                // segment fails, and its outcome lands nowhere.
                lock (meetingGate)
                {
                    cancelled.ThrowIfCancellationRequested();
                    return new MeetingPcmWindowReader(store.RawAudioPath(id) ?? incompleteAudio).Read(segment);
                }
            },
            decode: async (samples, token) =>
                pipeline.Run(await recognizer.Transcribe(samples, token).ConfigureAwait(false)).Output.Text,
            // Dictation always wins the engine; a decode also waits for a model to decode with.
            awaitTurn: async _ =>
            {
                try
                {
                    if (priority is not null) { await priority.WaitForTurn(cancelled).ConfigureAwait(false); }
                    while (!(IsEngineReady && ModelStateValue.IsReady))
                    {
                        await Task.Delay(250, cancelled).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancelled.IsCancellationRequested)
                {
                    // Trashed: on to the read, which refuses.
                }
            },
            emit: (segment, outcome) =>
            {
                SegmentDecoded(job, segment, outcome);
                return Task.CompletedTask;
            });
        lock (meetingGate) { transcriptionJobs.Add(job); }
        lock (gate) { loadedTranscripts[id] = []; }
        PublishTranscriptionSummary();
        // The engine is wanted now and stays until this is done.
        RewarmEngineIfCold(RewarmTrigger.Import);
    }

    /// <summary>The recording never started: its transcript and its hold on the engine go.</summary>
    private void AbandonTranscription(Guid id)
    {
        CancelTranscription(id);
        lock (gate) { loadedTranscripts.Remove(id); }
        ReleaseRecordingEngineHold();
    }

    /// <summary>
    /// The recording is gone: its transcript stops here and is never written again. The engine
    /// hold is released by whoever owns it - the drain, or the stop that finds no job.
    /// </summary>
    private void CancelTranscription(Guid id)
    {
        TranscriptionJob? job;
        lock (meetingGate)
        {
            job = JobLocked(id);
            if (job is null) { return; }
            transcriptionJobs.Remove(job);
        }

        // Outside the lock: cancelling runs the waiting turn's continuation, which takes it.
        job.Cancellation.Cancel();
        PublishTranscriptionSummary();
    }

    private void SegmentReady(Guid id, MeetingSegmentRef segment)
    {
        TranscriptionJob? job;
        lock (meetingGate)
        {
            job = JobLocked(id);
            if (job is null) { return; }
            job.BacklogSeconds += (double)segment.FrameCount / MeetingWriter.SampleRate;
        }

        PublishTranscriptionSummary();
        job.Queue.Submit(segment);
    }

    private void SegmentDecoded(TranscriptionJob job, MeetingSegmentRef segment, MeetingTranscriptionQueue.Outcome outcome)
    {
        double rate = MeetingWriter.SampleRate;
        var start = TimeSpan.FromSeconds(segment.StartFrame / rate);
        var end = TimeSpan.FromSeconds(segment.EndFrame / rate);
        MeetingUtteranceAssembler.Segment piece = outcome is MeetingTranscriptionQueue.Outcome.Decoded decoded
            ? new(segment.Channel, start, end, decoded.Text)
            : new(segment.Channel, start, end, string.Empty, IsFailed: true);
        bool current;
        lock (meetingGate)
        {
            job.BacklogSeconds = Math.Max(0, job.BacklogSeconds - segment.FrameCount / rate);
            current = transcriptionJobs.Contains(job);
            if (current)
            {
                job.Closed.AddRange(job.Assembler.Append(piece));
                PublishLiveTranscriptLocked(job);
            }
        }

        PublishTranscriptionSummary();
        if (current) { TranscriptChanged?.Invoke(job.Id); }
    }

    /// <summary>Caller holds <see cref="meetingGate"/>.</summary>
    private void PublishLiveTranscriptLocked(TranscriptionJob job)
    {
        List<MeetingUtterance> live = [.. job.Closed];
        if (job.Assembler.Pending is { } pending) { live.Add(pending); }
        lock (gate) { loadedTranscripts[job.Id] = live; }
        if (meetingStore is not { } store) { return; }
        try
        {
            // Filed means written as filed: the store refuses a recording it has since removed.
            store.Write(
                new MeetingTranscript(live, new Dictionary<MeetingChannel, int>(job.Queue.DecodedFrames)),
                job.Id,
                incomplete: !job.IsPublished);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Text that cannot be written is text that would be lost at quit. Said once; the audio
            // is still being recorded regardless.
            if (job.WriteFailureReported) { return; }
            job.WriteFailureReported = true;
            _ = Task.Run(() => Notify(new DictationNotice(
                DictationNoticeKind.Warning,
                "The transcript can't be saved. The recording continues.")));
        }
    }

    /// <summary>
    /// After the recording is filed: let the backlog drain, close the last paragraph, say on disk
    /// how it went, and compact the WAV once the transcript is whole. Releases the recording's
    /// hold on the engine, whatever happens.
    /// </summary>
    private void FinishTranscription(Guid id)
    {
        TranscriptionJob? job;
        lock (meetingGate) { job = JobLocked(id); }
        if (job is null || meetingStore is not { } store)
        {
            ReleaseRecordingEngineHold();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await job.Queue.Drain().ConfigureAwait(false);
                bool paused = job.Queue.IsPaused;
                lock (meetingGate)
                {
                    // Trashed meanwhile: nothing more to say about it.
                    if (!transcriptionJobs.Remove(job)) { return; }
                    job.Closed.AddRange(job.Assembler.Flush());
                    PublishLiveTranscriptLocked(job);
                }

                TranscriptChanged?.Invoke(id);
                try
                {
                    store.Update(id, metadata => metadata.TranscriptionState = paused
                        ? MeetingTranscriptionState.Partial
                        : MeetingTranscriptionState.Complete);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }

                PublishTranscriptionSummary();
                ReloadRecordings();
                if (!paused && importer is { } compactor)
                {
                    // The importer's compaction: the same archive, verified the same way.
                    await compactor.Compact(id).ConfigureAwait(false);
                    ReloadRecordings();
                    TranscriptChanged?.Invoke(id);
                }
            }
            finally
            {
                ReleaseRecordingEngineHold();
            }
        });
    }
}
