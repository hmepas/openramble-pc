namespace DictationCore.Tests;

/// <summary>
/// Segments are recognized while the person is still speaking, so the two things that matter
/// are that they come back in the right order and that a failure never yields half a transcript.
/// </summary>
public sealed class StreamedSegmentRecognizerTests
{
    private static ASRResult Made(string text) =>
        new(text, audioDuration: TimeSpan.FromSeconds(1), processingDuration: TimeSpan.FromSeconds(0.01));

    [Fact]
    public async Task TestSegmentsComeBackInTheOrderTheyWereCut()
    {
        // Each segment takes longer than the one after it. Without ordering, the fast ones
        // would overtake and the sentence would come out scrambled - which is the whole reason
        // the chain exists.
        var recognizer = new StreamedSegmentRecognizer(async samples =>
        {
            int index = (int)samples[0];
            await Task.Delay(40 - index * 10);
            return Made($"segment{index}");
        });

        for (int index = 0; index < 4; index += 1) { recognizer.Submit([index]); }

        StreamedSegmentRecognizer.Outcome outcome = await recognizer.Finish();
        var recognized = Assert.IsType<StreamedSegmentRecognizer.Outcome.Recognized>(outcome);
        Assert.Equal(["segment0", "segment1", "segment2", "segment3"], recognized.Texts);
    }

    [Fact]
    public async Task TestOneDecodeRunsAtATime()
    {
        // The runtime allows only one recognition in flight across every session of a model,
        // so overlapping here would corrupt decodes rather than merely be untidy.
        var counter = new ConcurrencyCounter();
        var recognizer = new StreamedSegmentRecognizer(async _ =>
        {
            counter.Enter();
            try
            {
                await Task.Delay(20);
                return Made("x");
            }
            finally
            {
                counter.Leave();
            }
        });
        for (int i = 0; i < 5; i += 1) { recognizer.Submit([1]); }

        await recognizer.Finish();
        Assert.True(counter.Peak == 1, "two decodes must never overlap");
    }

    [Fact]
    public async Task TestAFailedSegmentPoisonsTheWholeStream()
    {
        // Better a slower whole-take decode than a transcript quietly missing its middle.
        var recognizer = new StreamedSegmentRecognizer(samples =>
        {
            if (samples[0] == 2) { throw new ASREngineError.ModelsNotLoaded(); }
            return Task.FromResult(Made("ok"));
        });
        for (int index = 0; index < 4; index += 1) { recognizer.Submit([index]); }

        StreamedSegmentRecognizer.Outcome outcome = await recognizer.Finish();
        Assert.True(
            outcome is StreamedSegmentRecognizer.Outcome.Failed,
            "a failed segment must fail the stream, not be skipped");
    }

    [Fact]
    public async Task TestTranscriptsStayAlignedWithTheSegmentsEvenWhenOneIsSilent()
    {
        // Alignment is load-bearing: the owner drops the last transcript and re-recognizes it
        // with the tail, and it finds that transcript by index. A silent segment that vanished
        // here would take the wrong one.
        var recognizer = new StreamedSegmentRecognizer(samples =>
            Task.FromResult(Made(samples[0] == 1 ? "" : "words")));
        recognizer.Submit([0, 0]);
        recognizer.Submit([1]);        // recognizes as nothing: all breath
        recognizer.Submit([0, 0, 0]);

        var recognized = Assert.IsType<StreamedSegmentRecognizer.Outcome.Recognized>(await recognizer.Finish());
        Assert.Equal(["words", "", "words"], recognized.Texts);
        Assert.Equal([2, 1, 3], recognizer.SubmittedSampleCounts);
    }

    /// <summary>
    /// Escape while a segment waits for a cold engine: the wait itself is told the take is gone,
    /// so the decode it was queued for never starts on audio nobody will insert.
    /// </summary>
    [Fact]
    public async Task TestCancellingReachesASegmentStillWaitingForTheEngine()
    {
        var waiting = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        int decodes = 0;
        var recognizer = new StreamedSegmentRecognizer(async (_, token) =>
        {
            waiting.TrySetResult(token);
            // The engine warming up: honours the token, like the transcriber's own wait.
            await Task.Delay(Timeout.Infinite, token);
            Interlocked.Increment(ref decodes);
            return Made("too late");
        });

        recognizer.Submit([1]);
        CancellationToken token = await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        recognizer.Cancel();

        Assert.True(token.IsCancellationRequested, "the segment's wait must be told the take is gone");
        Assert.IsType<StreamedSegmentRecognizer.Outcome.Failed>(await recognizer.Finish().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, decodes);
    }

    [Fact]
    public async Task TestCancellingForgetsTheWordsAsWellAsTheTexts()
    {
        var recognizer = new StreamedSegmentRecognizer(_ => Task.FromResult(new ASRResult(
            "one",
            words: [new ASRResult.Word("one", TimeSpan.Zero, TimeSpan.FromSeconds(0.5))],
            audioDuration: TimeSpan.FromSeconds(1),
            processingDuration: TimeSpan.FromSeconds(0.01))));

        recognizer.Submit([1]);
        await Task.Delay(100);
        Assert.Single(recognizer.RecognizedWords);
        recognizer.Cancel();

        Assert.Empty(recognizer.RecognizedWords);
        Assert.Equal(0, recognizer.RecognizedCount);
    }

    [Fact]
    public async Task TestSubmissionsAfterFinishAreIgnored()
    {
        var recognizer = new StreamedSegmentRecognizer(_ => Task.FromResult(Made("a")));
        recognizer.Submit([0]);
        await recognizer.Finish();

        recognizer.Submit([0]);
        var recognized = Assert.IsType<StreamedSegmentRecognizer.Outcome.Recognized>(await recognizer.Finish());
        Assert.True(recognized.Texts.SequenceEqual(["a"]), "a late segment must not land after the tail");
    }

    [Fact]
    public async Task TestNothingSubmittedIsAnEmptyStreamNotAFailure()
    {
        var recognizer = new StreamedSegmentRecognizer(_ => Task.FromResult(Made("never")));
        StreamedSegmentRecognizer.Outcome outcome = await recognizer.Finish();
        var recognized = Assert.IsType<StreamedSegmentRecognizer.Outcome.Recognized>(outcome);
        Assert.Empty(recognized.Texts);
    }

    /// <summary>Records the highest number of callers inside the block at once.</summary>
    private sealed class ConcurrencyCounter
    {
        private readonly Lock gate = new();
        private int current;

        public int Peak { get; private set; }

        public void Enter()
        {
            lock (gate)
            {
                current += 1;
                Peak = Math.Max(Peak, current);
            }
        }

        public void Leave()
        {
            lock (gate) { current -= 1; }
        }
    }
}
