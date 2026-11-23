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

    private MeetingTranscriptionQueue? transcriptionQueue;
    private Guid? transcribingRecordingId;
    private MeetingUtteranceAssembler utteranceAssembler = new();
    private List<MeetingUtterance> closedUtterances = [];
    private double transcriptBacklogSeconds;
    private bool isTranscriptionPaused;
    private bool transcriptWriteFailureReported;

    /// <summary>
    /// A recording or its transcript needs the engine: the idle unload waits. Read under
    /// <c>gate</c> by the countdown, like the importer's activity.
    /// </summary>
    private volatile bool recordingHoldsEngine;

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

    /// <summary>Which recording's transcript is being written live (during and just after it).</summary>
    public Guid? TranscribingRecordingId
    {
        get { lock (gate) { return transcribingRecordingId; } }
        private set => Set(ref transcribingRecordingId, value);
    }

    /// <summary>Seconds of speech waiting for the engine, measured from what went in and came back.</summary>
    public double TranscriptBacklogSeconds
    {
        get { lock (gate) { return transcriptBacklogSeconds; } }
        private set => Set(ref transcriptBacklogSeconds, value);
    }

    /// <summary>The queue stopped itself after repeated failures. The audio is safe.</summary>
    public bool IsTranscriptionPaused
    {
        get { lock (gate) { return isTranscriptionPaused; } }
        private set => Set(ref isTranscriptionPaused, value);
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
            recordingHoldsEngine = true;
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
                recordingHoldsEngine = false;
                RescheduleIdleUnload();
                Notify(new DictationNotice(DictationNoticeKind.Failure, $"Couldn't start recording: {error.Message}"));
                return;
            }

            IMeetingCapturing capture = make(
                directory,
                InputDeviceUid,
                system,
                levels => LiveLevels = levels,
                SegmentReady,
                // Off the reporting thread: a failure may stop the recording, which waits for
                // the lifecycle this start may still hold.
                failure => _ = Task.Run(() => RecordingFailed(failure)));
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
                AbandonTranscription();
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
            lock (meetingGate) { hasQueue = transcriptionQueue is not null; }
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

    /// <summary>Take the held segments up again after failures paused the queue.</summary>
    public void ResumeTranscription()
    {
        MeetingTranscriptionQueue? queue;
        lock (meetingGate) { queue = transcriptionQueue; }
        if (queue is null) { return; }
        IsTranscriptionPaused = false;
        queue.Resume();
    }

    private void BeginTranscription(Guid id, string directory)
    {
        if (meetingStore is not { } store || importRecognizer is not { } recognizer) { return; }
        string incompleteAudio = Path.Combine(directory, MeetingWriter.AudioFileName);
        // Fillers go and the dictionary applies, as in an import; "scratch that" stays, because
        // in a meeting it is something a person said about their own words.
        ITextProcessing pipeline = MakeRecordingPipeline();
        DictationPriority? priority = dictationPriority;
        var queue = new MeetingTranscriptionQueue(
            read: segment =>
            {
                // The file moves out of .incomplete/ when the recording ends: resolve where it is
                // now, every time, and not while it is moving.
                lock (meetingGate)
                {
                    return new MeetingPcmWindowReader(store.RawAudioPath(id) ?? incompleteAudio).Read(segment);
                }
            },
            decode: async (samples, token) =>
                pipeline.Run(await recognizer.Transcribe(samples, token).ConfigureAwait(false)).Output.Text,
            // Dictation always wins the engine; a decode also waits for a model to decode with.
            awaitTurn: async token =>
            {
                if (priority is not null) { await priority.WaitForTurn(token).ConfigureAwait(false); }
                while (!(IsEngineReady && ModelStateValue.IsReady))
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                }
            },
            emit: (segment, outcome) =>
            {
                SegmentDecoded(id, segment, outcome);
                return Task.CompletedTask;
            });
        lock (meetingGate)
        {
            closedUtterances = [];
            utteranceAssembler = new MeetingUtteranceAssembler();
            transcriptWriteFailureReported = false;
            transcriptionQueue = queue;
        }

        lock (gate) { loadedTranscripts[id] = []; }
        TranscriptBacklogSeconds = 0;
        IsTranscriptionPaused = false;
        TranscribingRecordingId = id;
        // The engine is wanted now and stays until this is done.
        RewarmEngineIfCold(RewarmTrigger.Import);
    }

    private void AbandonTranscription()
    {
        Guid? id;
        lock (meetingGate)
        {
            transcriptionQueue = null;
            id = transcribingRecordingId;
        }

        if (id is { } gone) { lock (gate) { loadedTranscripts.Remove(gone); } }
        TranscribingRecordingId = null;
        TranscriptBacklogSeconds = 0;
        IsTranscriptionPaused = false;
        recordingHoldsEngine = false;
        RescheduleIdleUnload();
    }

    private void SegmentReady(MeetingSegmentRef segment)
    {
        MeetingTranscriptionQueue? queue;
        lock (meetingGate) { queue = transcriptionQueue; }
        if (queue is null) { return; }
        lock (gate) { transcriptBacklogSeconds += (double)segment.FrameCount / MeetingWriter.SampleRate; }
        Raise(nameof(TranscriptBacklogSeconds));
        queue.Submit(segment);
    }

    private void SegmentDecoded(Guid id, MeetingSegmentRef segment, MeetingTranscriptionQueue.Outcome outcome)
    {
        double rate = MeetingWriter.SampleRate;
        lock (gate)
        {
            transcriptBacklogSeconds = Math.Max(0, transcriptBacklogSeconds - segment.FrameCount / rate);
        }

        Raise(nameof(TranscriptBacklogSeconds));
        var start = TimeSpan.FromSeconds(segment.StartFrame / rate);
        var end = TimeSpan.FromSeconds(segment.EndFrame / rate);
        MeetingUtteranceAssembler.Segment piece = outcome is MeetingTranscriptionQueue.Outcome.Decoded decoded
            ? new(segment.Channel, start, end, decoded.Text)
            : new(segment.Channel, start, end, string.Empty, IsFailed: true);
        bool paused;
        lock (meetingGate)
        {
            if (transcriptionQueue is not { } queue || transcribingRecordingId != id) { return; }
            closedUtterances.AddRange(utteranceAssembler.Append(piece));
            PublishLiveTranscriptLocked(id, queue);
            paused = queue.IsPaused;
        }

        TranscriptChanged?.Invoke(id);
        IsTranscriptionPaused = paused;
    }

    /// <summary>Caller holds <see cref="meetingGate"/>.</summary>
    private void PublishLiveTranscriptLocked(Guid id, MeetingTranscriptionQueue queue)
    {
        List<MeetingUtterance> live = [.. closedUtterances];
        if (utteranceAssembler.Pending is { } pending) { live.Add(pending); }
        lock (gate) { loadedTranscripts[id] = live; }
        if (meetingStore is not { } store) { return; }
        try
        {
            store.Write(
                new MeetingTranscript(live, new Dictionary<MeetingChannel, int>(queue.DecodedFrames)),
                id,
                incomplete: !store.IsPublished(id));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Text that cannot be written is text that would be lost at quit. Said once; the audio
            // is still being recorded regardless.
            if (transcriptWriteFailureReported) { return; }
            transcriptWriteFailureReported = true;
            _ = Task.Run(() => Notify(new DictationNotice(
                DictationNoticeKind.Warning,
                "The transcript can't be saved. The recording continues.")));
        }
    }

    /// <summary>
    /// After the recording is filed: let the backlog drain, close the last paragraph, say on disk
    /// how it went, and compact the WAV once the transcript is whole.
    /// </summary>
    private void FinishTranscription(Guid id)
    {
        MeetingTranscriptionQueue? queue;
        lock (meetingGate) { queue = transcribingRecordingId == id ? transcriptionQueue : null; }
        if (queue is null || meetingStore is not { } store)
        {
            recordingHoldsEngine = false;
            RescheduleIdleUnload();
            return;
        }

        _ = Task.Run(async () =>
        {
            await queue.Drain().ConfigureAwait(false);
            bool paused = queue.IsPaused;
            lock (meetingGate)
            {
                if (transcribingRecordingId != id) { return; }
                closedUtterances.AddRange(utteranceAssembler.Flush());
                PublishLiveTranscriptLocked(id, queue);
                transcriptionQueue = null;
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

            TranscribingRecordingId = null;
            IsTranscriptionPaused = false;
            TranscriptBacklogSeconds = 0;
            ReloadRecordings();
            if (!paused && importer is { } compactor)
            {
                // The importer's compaction: the same archive, verified the same way.
                await compactor.Compact(id).ConfigureAwait(false);
                ReloadRecordings();
                TranscriptChanged?.Invoke(id);
            }

            recordingHoldsEngine = false;
            RescheduleIdleUnload();
        });
    }
}
