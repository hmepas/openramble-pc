namespace DictationCore.Tests;

/// <summary>
/// The voice recording must not stay on disk after the text is recognized.
///
/// The defect these tests guard against was real: the files were never deleted.
/// Half an hour of dictation a day is about twenty gigabytes a year and, more
/// importantly, an archive of everything said out loud - from a product that
/// promises privacy.
/// </summary>
[Collection(DictationControllerCollection.Name)]
public sealed class RecordingCleanupTests : IDisposable
{
    private readonly string directory;

    /// <summary>
    /// A capture that creates a real file - otherwise there is nothing to check.
    ///
    /// Only an unclosed recording can be aborted, like a real one: after
    /// <c>StopRecording</c> the file is already closed and handed outside, and
    /// nobody but the session owner may delete it now.
    /// </summary>
    private sealed class FileCapture : IAudioCapturing
    {
        private readonly Lock gate = new();
        private readonly string directory;
        private readonly TimeSpan duration;
        private string? lastFile;
        private bool isRecording;

        /// <summary>
        /// The delay sits exactly where a real capture has it: the file is
        /// already closed and on disk, but the call has not returned yet. From
        /// this moment aborting the recording deletes nothing - only the owner
        /// may delete it.
        /// </summary>
        private readonly Gate? stopGate;

        public FileCapture(string directory, TimeSpan? duration = null, Gate? stopGate = null)
        {
            this.directory = directory;
            this.duration = duration ?? TimeSpan.FromSeconds(3);
            this.stopGate = stopGate;
        }

        public Task<string> StartRecording()
        {
            string url = Path.Combine(directory, $"take-{Guid.NewGuid()}.wav");
            File.WriteAllText(url, "звук");
            lock (gate)
            {
                lastFile = url;
                isRecording = true;
            }

            return Task.FromResult(url);
        }

        public async Task<(string Url, TimeSpan Duration)> StopRecording()
        {
            string url;
            lock (gate)
            {
                if (lastFile is not { } file || !isRecording) { throw new AudioCaptureError.NotRecording(); }
                isRecording = false;
                url = file;
            }

            if (stopGate is not null) { await stopGate.Pass(); }
            return (url, duration);
        }

        public Task AbortRecording()
        {
            string url;
            lock (gate)
            {
                if (!isRecording || lastFile is not { } file) { return Task.CompletedTask; }
                isRecording = false;
                url = file;
            }

            try
            {
                File.Delete(url);
            }
            catch (IOException)
            {
            }

            return Task.CompletedTask;
        }
    }

    public RecordingCleanupTests()
    {
        directory = Path.Combine(Path.GetTempPath(), $"takes-{Guid.NewGuid()}");
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task Settle(int iterations = 15)
    {
        for (int i = 0; i < iterations; i += 1)
        {
            await Task.Yield();
            await Task.Delay(5);
        }
    }

    private static async Task WaitForIdle(DictationController controller, int limit = 400)
    {
        for (int i = 0; i < limit && controller.State != DictationState.Idle; i += 1)
        {
            await Task.Yield();
            await Task.Delay(5);
        }
    }

    private static DictationController MakeController(
        FileCapture capture,
        Exception? transcribeError = null,
        TimeSpan transcribeDelay = default,
        Gate? transcribeGate = null,
        Gate? transcribeEntered = null,
        TextInsertionError? insertError = null,
        IOverlayPresenting? overlay = null,
        IRecordingRecoveryStoring? recordingRecovery = null)
    {
        var inserter = new FakeInserter();
        if (insertError is not null) { inserter.SetError(insertError); }

        return new DictationController(
            capture: capture,
            transcribe: async (_, token) =>
            {
                if (transcribeDelay > TimeSpan.Zero) { await Task.Delay(transcribeDelay, token); }
                transcribeEntered?.Open();
                if (transcribeGate is not null) { await transcribeGate.Pass(); }
                if (transcribeError is not null) { throw transcribeError; }
                return new ASRResult(
                    "распознано",
                    audioDuration: TimeSpan.FromSeconds(3),
                    processingDuration: TimeSpan.FromSeconds(0.1));
            },
            inserter: inserter,
            overlay: overlay ?? new FakeOverlay(),
            sounds: new FakeSounds(),
            recordingRecovery: recordingRecovery ?? new DiscardingRecordingRecovery());
    }

    [Fact]
    public async Task TestRecordingIsDeletedAfterSuccessfulInsertion()
    {
        var capture = new FileCapture(directory);
        var controller = MakeController(capture);

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        // After the insertion the voice recording must be deleted.
        Assert.Empty(Directory.GetFileSystemEntries(directory));
    }

    [Fact]
    public async Task TestRecordingIsPreservedWhenRecognitionFails()
    {
        // If ASR fails, the WAV is the only way to repeat the dictation without loss.
        var capture = new FileCapture(directory);
        string recovered = Path.Combine(directory, "RecoveredAudio");
        var controller = MakeController(
            capture,
            transcribeError: new ASREngineError.InferenceFailed("сбой"),
            recordingRecovery: new RecordingRecoveryStore(recovered));

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        Assert.Single(Directory.GetFiles(recovered, "*.wav"));
    }

    [Fact]
    public async Task TestRecordingIsDeletedWhenInsertionFails()
    {
        // The text is kept in memory, but there is no need to keep the voice itself.
        var capture = new FileCapture(directory);
        var controller = MakeController(
            capture,
            insertError: new TextInsertionError.AccessibilityPermissionDenied());

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        // After a failed insertion the recording is deleted.
        Assert.Empty(Directory.GetFileSystemEntries(directory));
    }

    [Fact]
    public async Task TestRecordingIsDeletedOnCancel()
    {
        var capture = new FileCapture(directory);
        var controller = MakeController(capture);

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Cancel();
        await Settle();

        // A cancelled dictation leaves no recording.
        Assert.Empty(Directory.GetFileSystemEntries(directory));
    }

    [Fact]
    public async Task TestAccidentalTapLeavesNoRecording()
    {
        // An accidental touch of the key: there is nothing to recognize - and
        // that is exactly why the recording is easy to forget. The file is real,
        // with a voice in it, and it stays until the next launch of an
        // application that lives in the menu bar for weeks.
        var capture = new FileCapture(directory, duration: TimeSpan.FromSeconds(0.1));
        var controller = MakeController(capture);

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        await Settle();

        // A recording that is too short is deleted too.
        Assert.Empty(Directory.GetFileSystemEntries(directory));
    }

    [Fact]
    public async Task TestCancelBeforeRecognitionFailureLeavesNeitherFileNorNotice()
    {
        // The person pressed Escape, and at that moment the engine managed to
        // fall over. The cancellation has already happened: the WAV of a
        // cancelled dictation has no right to settle in the retry folder, and the
        // failure has no right to be reported. Otherwise Escape leaves exactly
        // the trace it is meant to get rid of.
        var gate = new Gate();
        var entered = new Gate();
        var capture = new FileCapture(directory);
        string recovered = Path.Combine(directory, "RecoveredAudio");
        var overlay = new CollectingOverlay();
        var controller = MakeController(
            capture,
            transcribeError: new ASREngineError.InferenceFailed("сбой"),
            transcribeGate: gate,
            transcribeEntered: entered,
            overlay: overlay,
            recordingRecovery: new RecordingRecoveryStore(recovered));
        List<DictationNotice> notices = [];
        controller.OnNotice = notices.Add;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        // We wait not for the state but for the engine to actually start working:
        // the state switches synchronously, the task does not, and a cancellation
        // sent earlier than the first line of the task would check a completely
        // different branch.
        await entered.Pass();
        Assert.Equal(DictationState.Transcribing, controller.State);

        controller.Cancel();
        await Settle();
        gate.Open();
        await WaitForIdle(controller);
        await Settle();

        // A cancelled dictation is not kept for Retry.
        Assert.Empty(SafeWAVs(recovered));
        // And it does not stay in Takes either.
        Assert.Empty(Directory.GetFiles(directory, "*.wav"));

        // After a cancellation there is nothing to report.
        Assert.All(overlay.Notices, notice => Assert.NotEqual(DictationNoticeKind.Failure, notice.Kind));
        Assert.All(notices, notice => Assert.Null(notice.RecoveryAudio));
    }

    [Fact]
    public async Task TestCancelDuringRescueLeavesNeitherFileNorNotice()
    {
        // The device disappeared mid-speech: the controller closes the WAV and
        // prepares it for Retry. While the file is being closed the person
        // presses Escape - and from that moment the dictation has a single
        // outcome. A cancelled recording has no right to settle on disk "for a
        // replay", nor to appear with a failure message on top of the session the
        // person started next.
        var gate = new Gate();
        var capture = new FileCapture(directory, stopGate: gate);
        string recovered = Path.Combine(directory, "RecoveredAudio");
        var overlay = new CollectingOverlay();
        List<DictationNotice> notices = [];
        var controller = new DictationController(
            capture: capture,
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "распознано",
                audioDuration: TimeSpan.FromSeconds(3),
                processingDuration: TimeSpan.FromSeconds(0.1))),
            inserter: new FakeInserter(),
            overlay: overlay,
            sounds: new FakeSounds(),
            recordingRecovery: new RecordingRecoveryStore(recovered));
        controller.OnNotice = notices.Add;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        Assert.Equal(DictationState.Listening, controller.State);

        controller.PreserveActiveRecording(reason: "Устройство отключилось.");
        await Settle();
        // The rescue of the recording must have started.
        Assert.Equal(DictationState.Transcribing, controller.State);

        controller.Cancel();
        await Settle();
        gate.Open();
        await WaitForIdle(controller);
        await Settle();

        // A cancelled dictation does not stay in Takes.
        Assert.Empty(Directory.GetFiles(directory, "*.wav"));
        // And is not kept for Retry.
        Assert.Empty(SafeWAVs(recovered));

        // There is no reason to report a failed rescue after a cancellation.
        Assert.All(overlay.Notices, notice => Assert.NotEqual(DictationNoticeKind.Failure, notice.Kind));
        // A cancelled recording is not offered for Retry.
        Assert.All(notices, notice => Assert.Null(notice.RecoveryAudio));
        Assert.Equal(DictationState.Idle, controller.State);
    }

    /// <summary>
    /// The device vanished a moment after the start - nothing to rescue.
    ///
    /// A fragment shorter than the recognition minimum is silently deleted by
    /// the main path: the person lost nothing. Rescue must behave the same,
    /// otherwise a one-second device hiccup leaves a "recording for retry"
    /// whose retry forever produces an empty result.
    /// </summary>
    [Fact]
    public async Task TestRescueOfTooShortRecordingDiscardsInsteadOfSaving()
    {
        var capture = new FileCapture(directory, duration: TimeSpan.FromSeconds(0.1));
        string recovered = Path.Combine(directory, "RecoveredAudio");
        var overlay = new CollectingOverlay();
        List<DictationNotice> notices = [];
        var controller = new DictationController(
            capture: capture,
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "recognized",
                audioDuration: TimeSpan.FromSeconds(3),
                processingDuration: TimeSpan.FromSeconds(0.1))),
            inserter: new FakeInserter(),
            overlay: overlay,
            sounds: new FakeSounds(),
            recordingRecovery: new RecordingRecoveryStore(recovered));
        controller.OnNotice = notices.Add;

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        Assert.Equal(DictationState.Listening, controller.State);

        controller.PreserveActiveRecording(reason: "The device disconnected.");
        await WaitForIdle(controller, limit: 100);

        // A fragment below the minimum is not saved.
        Assert.Empty(SafeWAVs(recovered));
        // And it does not stay in Takes either.
        Assert.Empty(Directory.GetFiles(directory, "*.wav"));

        Assert.All(notices, notice => Assert.Null(notice.RecoveryAudio));
        // The reason of the interruption must still be named.
        Assert.Contains(notices, notice => notice.Message.Contains("The device disconnected.", StringComparison.Ordinal));
        // Nothing to say about rescuing a recording that does not exist.
        Assert.DoesNotContain(
            notices,
            notice => notice.Message.Contains("is kept on this Mac", StringComparison.Ordinal)
                || notice.Message.Contains("couldn't be kept", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TestRecordingIsDeletedWhenCancelledDuringTranscription()
    {
        // The cancellation arrives when the recording is already closed and on
        // disk: there is nothing to abort here, but it must be deleted.
        var capture = new FileCapture(directory);
        var controller = MakeController(capture, transcribeDelay: TimeSpan.FromMilliseconds(800));

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle();
        controller.Stop();
        // The moment "recognition is underway" is caught by polling: a fixed
        // sleep on an overloaded runner lasts longer than the whole recognition.
        for (int i = 0; i < 400 && controller.State != DictationState.Transcribing; i += 1)
        {
            await Task.Yield();
            await Task.Delay(2);
        }

        Assert.Equal(DictationState.Transcribing, controller.State);

        controller.Cancel();
        for (int i = 0; i < 400; i += 1)
        {
            if (Directory.GetFileSystemEntries(directory).Length == 0
                && controller.State == DictationState.Idle)
            {
                break;
            }

            await Task.Yield();
            await Task.Delay(5);
        }

        // A cancellation during recognition leaves no voice on disk.
        Assert.Empty(Directory.GetFileSystemEntries(directory));
    }

    /// <summary>The recovery directory may not exist at all - that is an empty result, not a failure.</summary>
    private static string[] SafeWAVs(string directory) =>
        Directory.Exists(directory) ? Directory.GetFiles(directory, "*.wav") : [];
}
