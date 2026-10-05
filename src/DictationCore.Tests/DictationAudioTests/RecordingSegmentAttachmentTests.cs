using DictationCore.Audio;

namespace DictationCore.Tests.DictationAudioTests;

/// <summary>
/// The piece between the microphone and the segmenter.
///
/// It is fed from the ordered frame drain, so the two things worth proving are that it hands
/// back exactly the audio it was given, in order, and that the count it reports at the stop is
/// the truth - the tail the controller decodes starts at that number, so a wrong one either
/// loses words or says them twice.
/// </summary>
public sealed class RecordingSegmentAttachmentTests
{
    /// <summary>
    /// A segmenter with a short segment floor, so a ten-second fixture can earn a cut. The
    /// shipped floor is far higher and is pinned by <see cref="SpeechSegmenterTests"/>; these
    /// tests are about the plumbing around it, not about the number.
    /// </summary>
    private static SpeechSegmenter Mechanism =>
        new(new SpeechSegmenter.Parameters(
            minimumPause: TimeSpan.FromMilliseconds(700),
            minimumSegment: TimeSpan.FromSeconds(4)));

    /// <summary>Frames of the size the real tap delivers.</summary>
    private static void Feed(RecordingSegmentAttachment attachment, float level, double seconds)
    {
        int remaining = (int)(seconds * 16_000);
        while (remaining > 0)
        {
            int count = Math.Min(2048, remaining);
            attachment.Submit(Enumerable.Repeat(level, count).ToArray());
            remaining -= count;
        }
    }

    [Fact]
    public void TestWithoutASinkNothingIsSegmentedAtAll()
    {
        var attachment = new RecordingSegmentAttachment();
        Feed(attachment, 0.5f, seconds: 30);
        Feed(attachment, 0, seconds: 2);
        Assert.True(
            attachment.DrainConsumedSampleCount() == 0,
            "a capture nobody asked to segment must behave exactly as it did before");
    }

    [Fact]
    public void TestSegmentsCarryTheAudioTheyWereGivenAndTheCountMatches()
    {
        var collected = new Collected();
        var attachment = new RecordingSegmentAttachment(Mechanism);
        attachment.SetSink(collected.Append);

        // Ten seconds of speech, then a pause long enough to earn a cut.
        Feed(attachment, 0.5f, seconds: 10);
        Feed(attachment, 0, seconds: 1.5);
        // And more speech, which stays pending as the tail.
        Feed(attachment, 0.5f, seconds: 3);

        int consumed = attachment.DrainConsumedSampleCount();
        IReadOnlyList<float[]> segments = collected.Segments;

        Assert.True(segments.Count == 1, "one pause, one segment");
        Assert.True(
            segments[0].Length == consumed,
            "the reported count must be exactly what left in segments");
        // Everything shipped came from the speech at the start, so it is that level: proof the
        // bytes are the ones handed in, not a reconstruction.
        Assert.Equal(0.5f, segments[0][0]);
    }

    [Fact]
    public void TestTheReportedCountIsAConsistentPrefixOfTheTake()
    {
        var collected = new Collected();
        var attachment = new RecordingSegmentAttachment(Mechanism);
        attachment.SetSink(collected.Append);

        for (int i = 0; i < 3; i += 1)
        {
            Feed(attachment, 0.5f, seconds: 8);
            Feed(attachment, 0, seconds: 1.2);
        }

        int consumed = attachment.DrainConsumedSampleCount();
        int total = collected.Segments.Sum(segment => segment.Length);
        Assert.True(
            consumed == total,
            "the tail begins where the segments end; anything else double-counts or drops audio");
        Assert.True(collected.Segments.Count > 1);
    }

    [Fact]
    public void TestDrainingIsABarrierSoNothingArrivesAfterTheCount()
    {
        var collected = new Collected();
        var attachment = new RecordingSegmentAttachment(Mechanism);
        attachment.SetSink(collected.Append);

        Feed(attachment, 0.5f, seconds: 10);
        Feed(attachment, 0, seconds: 1.5);
        int consumed = attachment.DrainConsumedSampleCount();
        int after = collected.Segments.Sum(segment => segment.Length);

        Assert.True(consumed == after, "every submitted frame must be processed before the count is read");

        // And a frame that arrives late, after the take is over, is ignored rather than
        // appended behind the tail the controller already has.
        Feed(attachment, 0.5f, seconds: 10);
        Feed(attachment, 0, seconds: 1.5);
        Assert.Equal(consumed, attachment.DrainConsumedSampleCount());
    }

    private sealed class Collected
    {
        private readonly Lock gate = new();
        private readonly List<float[]> storage = [];

        public void Append(float[] samples)
        {
            lock (gate) { storage.Add(samples); }
        }

        public IReadOnlyList<float[]> Segments { get { lock (gate) { return storage.ToArray(); } } }
    }
}
