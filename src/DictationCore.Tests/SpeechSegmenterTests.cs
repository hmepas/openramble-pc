namespace DictationCore.Tests;

/// <summary>
/// The segmenter decides where a take may be cut so the engine can start early.
///
/// The tests that matter most here are the ones that prove a cut <i>does not</i> happen: the
/// previous attempt at chunking in this project cut phrases in half and lost three times more
/// words than the engine lost on its own.
/// </summary>
public sealed class SpeechSegmenterTests
{
    /// <summary>One frame of the size the microphone tap actually delivers.</summary>
    private const int Frame = 2048;

    private const float Speech = 0.5f;
    private const float Quiet = 0.0f;

    private static int Samples(double seconds) => (int)(seconds * 16_000);

    /// <summary>
    /// Parameters for the tests about the <i>rule</i>, with a short segment floor so a
    /// ten-second fixture can earn a cut.
    ///
    /// The shipped floor is deliberately far higher, and
    /// <see cref="TestTheShippedDefaultsRefuseToCutAnOrdinaryTake"/> is what pins it. Tests of
    /// the mechanism should not have to move every time that number is tuned, and tuning it
    /// should not be able to pass silently either.
    /// </summary>
    private static SpeechSegmenter.Parameters Mechanism =>
        new(minimumPause: TimeSpan.FromMilliseconds(700), minimumSegment: TimeSpan.FromSeconds(4));

    /// <summary>Feed <paramref name="seconds"/> of one level, returning every cut offered along the way.</summary>
    private static List<int> Feed(SpeechSegmenter segmenter, float level, double seconds)
    {
        List<int> cuts = [];
        int remaining = Samples(seconds);
        while (remaining > 0)
        {
            int count = Math.Min(Frame, remaining);
            if (segmenter.Observe(level, count) is { } cut) { cuts.Add(cut); }
            remaining -= count;
        }

        return cuts;
    }

    // MARK: - The rule that makes this safe

    [Fact]
    public void TestUnbrokenSpeechIsNeverCut()
    {
        var segmenter = new SpeechSegmenter();
        // Five minutes without a pause. Latency suffers; the words do not.
        List<int> cuts = Feed(segmenter, Speech, seconds: 300);
        Assert.True(cuts.Count == 0, "speech must never be cut, however long it runs");
    }

    [Fact]
    public void TestAPauseShorterThanRequiredDoesNotCut()
    {
        var segmenter = new SpeechSegmenter();
        Feed(segmenter, Speech, seconds: 10);
        List<int> cuts = Feed(segmenter, Quiet, seconds: 0.4);
        Assert.True(cuts.Count == 0, "400 ms is under the 700 ms default and is not a boundary");
    }

    [Fact]
    public void TestSilenceBeforeTheFirstWordIsNotAPause()
    {
        var segmenter = new SpeechSegmenter();
        // Someone holds the key and thinks for ten seconds before speaking.
        List<int> cuts = Feed(segmenter, Quiet, seconds: 10);
        Assert.True(cuts.Count == 0, "a take must not be cut before it has any speech in it");
    }

    [Fact]
    public void TestAPauseBeforeEnoughAudioHasAccumulatedDoesNotCut()
    {
        var segmenter = new SpeechSegmenter();
        Feed(segmenter, Speech, seconds: 1);
        List<int> cuts = Feed(segmenter, Quiet, seconds: 2);
        Assert.True(cuts.Count == 0, "one second of speech is not worth its own decode");
    }

    // MARK: - Cutting

    [Fact]
    public void TestAQualifyingPauseCutsInTheMiddleOfTheSilence()
    {
        var segmenter = new SpeechSegmenter(Mechanism);
        Feed(segmenter, Speech, seconds: 10);
        List<int> cuts = Feed(segmenter, Quiet, seconds: 1.0);

        Assert.Single(cuts);
        int cut = cuts[0];

        // The cut is offered as soon as the pause qualifies, so the silence seen at that
        // moment is the required 700 ms, not the full second. The seam sits half way through
        // it: ~10 s of speech plus ~350 ms.
        int expected = Samples(10) + Samples(0.35);
        Assert.True(
            Math.Abs(cut - expected) <= Frame,
            "the seam belongs in the middle of the silence that earned it");
    }

    [Fact]
    public void TestBothSidesOfTheSeamAreSilent()
    {
        var segmenter = new SpeechSegmenter(Mechanism);
        Feed(segmenter, Speech, seconds: 10);
        List<int> cuts = Feed(segmenter, Quiet, seconds: 1.0);
        Assert.NotEmpty(cuts);
        // Everything after 10 s was silence, so a seam past 10 s closes a segment that ends
        // quiet, and half the pause remains on the other side.
        Assert.True(cuts[0] > Samples(10) - Frame, "the closing segment must end in silence, not mid-word");
    }

    [Fact]
    public void TestOffsetsAreRelativeToTheSegmentSoTheCallerNeverTracksPosition()
    {
        var segmenter = new SpeechSegmenter(Mechanism);
        List<int> cuts = [];
        // Three sentences, each ten seconds, each followed by a full pause.
        for (int i = 0; i < 3; i += 1)
        {
            Feed(segmenter, Speech, seconds: 10);
            cuts.AddRange(Feed(segmenter, Quiet, seconds: 1.0));
        }

        Assert.True(cuts.Count == 3, "each sentence should have earned its own cut");
        // Every offset is measured from the start of its own segment, so they are all about
        // the same rather than growing.
        foreach (int cut in cuts)
        {
            Assert.True(
                Math.Abs(cut - (Samples(10) + Samples(0.35))) <= Samples(1.2),
                "offsets must not accumulate across segments");
        }
    }

    [Fact]
    public void TestTheShippedDefaultsRefuseToCutAnOrdinaryTake()
    {
        // The whole point of the shipped floor. A sixteen-second take with a clear pause in it
        // is one decode, because cutting it would buy no time anybody can feel and would spend
        // a draw on a language decision this engine sometimes gets wrong.
        var segmenter = new SpeechSegmenter();
        Feed(segmenter, Speech, seconds: 10);
        List<int> cuts = Feed(segmenter, Quiet, seconds: 1.5);
        cuts.AddRange(Feed(segmenter, Speech, seconds: 4.5));
        Assert.True(cuts.Count == 0, "an ordinary take must reach the engine whole");

        // And a long one still streams, or the feature would not exist.
        var longTake = new SpeechSegmenter();
        Feed(longTake, Speech, seconds: 20);
        Assert.True(
            Feed(longTake, Quiet, seconds: 1.5).Count == 1,
            "past the floor a real pause should still earn a cut");
    }

    // MARK: - The floor the decoder actually has

    [Fact]
    public void TestATailShorterThanTheSubmissionFloorIsNotCutOffOnItsOwn()
    {
        var segmenter = new SpeechSegmenter(new SpeechSegmenter.Parameters(
            minimumPause: TimeSpan.FromMilliseconds(700),
            minimumSegment: TimeSpan.FromMilliseconds(500),
            minimumSubmission: TimeSpan.FromSeconds(2)));
        Feed(segmenter, Speech, seconds: 0.6);
        List<int> cuts = Feed(segmenter, Quiet, seconds: 1.0);
        Assert.True(cuts.Count == 0, "below the submission floor the decoder is non-monotonic; keep it whole");
    }

    [Fact]
    public void TestPendingIsSubmittableTracksTheFloor()
    {
        var segmenter = new SpeechSegmenter();
        Feed(segmenter, Speech, seconds: 1);
        Assert.False(segmenter.PendingIsSubmittable);
        Feed(segmenter, Speech, seconds: 2);
        Assert.True(segmenter.PendingIsSubmittable);
    }

    // MARK: - Relaxation

    [Fact]
    public void TestTheThresholdRelaxesOnceTheSegmentIsLong()
    {
        var segmenter = new SpeechSegmenter(new SpeechSegmenter.Parameters(
            minimumPause: TimeSpan.FromMilliseconds(700),
            minimumSegment: TimeSpan.FromSeconds(4),
            minimumSubmission: TimeSpan.FromSeconds(2),
            relaxAfter: TimeSpan.FromSeconds(20),
            relaxedPause: TimeSpan.FromMilliseconds(350)));
        // Under the relaxation point a 400 ms pause is not enough.
        Feed(segmenter, Speech, seconds: 10);
        Assert.Empty(Feed(segmenter, Quiet, seconds: 0.4));

        // Past it, the same pause is.
        Feed(segmenter, Speech, seconds: 15);
        List<int> cuts = Feed(segmenter, Quiet, seconds: 0.4);
        Assert.True(cuts.Count == 1, "a long segment should take a shorter pause rather than keep growing");
    }

    [Fact]
    public void TestRelaxationStillRefusesToCutThroughSpeech()
    {
        var segmenter = new SpeechSegmenter(new SpeechSegmenter.Parameters(
            relaxAfter: TimeSpan.FromSeconds(5),
            relaxedPause: TimeSpan.FromMilliseconds(350)));
        List<int> cuts = Feed(segmenter, Speech, seconds: 120);
        Assert.True(cuts.Count == 0, "relaxation lowers the pause bar, it does not remove it");
    }

    // MARK: - Lifecycle

    [Fact]
    public void TestResetForgetsThePendingSegment()
    {
        var segmenter = new SpeechSegmenter();
        Feed(segmenter, Speech, seconds: 10);
        segmenter.Reset();
        Assert.Equal(0, segmenter.PendingSampleCount);
        // And the new take is again not cuttable before its first word.
        Assert.Empty(Feed(segmenter, Quiet, seconds: 5));
    }

    [Fact]
    public void TestABriefDipInsideAWordDoesNotAccumulateIntoAPause()
    {
        var segmenter = new SpeechSegmenter();
        Feed(segmenter, Speech, seconds: 10);
        // Ten stop closures, each well under the threshold, separated by speech.
        List<int> cuts = [];
        for (int i = 0; i < 10; i += 1)
        {
            cuts.AddRange(Feed(segmenter, Quiet, seconds: 0.12));
            cuts.AddRange(Feed(segmenter, Speech, seconds: 0.3));
        }

        Assert.True(cuts.Count == 0, "consonant closures are not pauses and must not add up");
    }
}
