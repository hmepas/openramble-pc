using DictationCore;
using Xunit;
using static DictationCore.MeetingTranscriptionQueue;

namespace DictationCore.Tests;

public class MeetingTranscriptionQueueTests
{
    private sealed class Recorder
    {
        private readonly Lock gate = new();
        private readonly List<(MeetingSegmentRef Segment, Outcome Outcome)> items = [];

        public Task Record(MeetingSegmentRef segment, Outcome outcome)
        {
            lock (gate) { items.Add((segment, outcome)); }
            return Task.CompletedTask;
        }

        public List<Outcome> Outcomes
        {
            get { lock (gate) { return items.Select(item => item.Outcome).ToList(); } }
        }

        public List<MeetingSegmentRef> Segments
        {
            get { lock (gate) { return items.Select(item => item.Segment).ToList(); } }
        }
    }

    private static MeetingSegmentRef Ref(MeetingChannel channel, int start, int count = 16_000) =>
        new(channel, start, count);

    /// <summary>
    /// The samples "read" encode which segment they came from, so the decode can prove the queue
    /// handed it the right audio.
    /// </summary>
    private static float[] Read(MeetingSegmentRef segment) => [segment.StartFrame];

    private static Task NoWait(CancellationToken _) => Task.CompletedTask;

    [Fact]
    public async Task SegmentsAreDecodedInOrderWithTheirOwnAudio()
    {
        var recorder = new Recorder();
        var queue = new MeetingTranscriptionQueue(
            Read, (samples, _) => Task.FromResult($"text@{(int)samples[0]}"), NoWait, recorder.Record);
        foreach (int start in new[] { 0, 16_000, 32_000 }) { queue.Submit(Ref(MeetingChannel.Microphone, start)); }
        await queue.Drain();
        Assert.Equal(
            [new Outcome.Decoded("text@0"), new Outcome.Decoded("text@16000"), new Outcome.Decoded("text@32000")],
            recorder.Outcomes);
        Assert.Equal(48_000, queue.DecodedFrames[MeetingChannel.Microphone]);
        Assert.Equal(0, queue.Backlog);
    }

    [Fact]
    public async Task OneFailureIsOneFailedSegmentAndTheNextProceeds()
    {
        var recorder = new Recorder();
        var queue = new MeetingTranscriptionQueue(
            Read,
            (samples, _) => (int)samples[0] == 16_000
                ? throw new InvalidDataException("corrupt")
                : Task.FromResult("ok"),
            NoWait,
            recorder.Record);
        foreach (int start in new[] { 0, 16_000, 32_000 }) { queue.Submit(Ref(MeetingChannel.Microphone, start)); }
        await queue.Drain();
        List<Outcome> outcomes = recorder.Outcomes;
        Assert.Equal(3, outcomes.Count);
        Assert.Equal(new Outcome.Decoded("ok"), outcomes[0]);
        Assert.IsType<Outcome.Failed>(outcomes[1]);
        Assert.Equal(new Outcome.Decoded("ok"), outcomes[2]); // the queue did not stop
        Assert.False(queue.IsPaused);
    }

    [Fact]
    public async Task ThreeFailuresInARowPauseTheQueueAndResumeTakesTheHeldOnesUp()
    {
        var recorder = new Recorder();
        bool failing = true;
        var queue = new MeetingTranscriptionQueue(
            Read,
            (_, _) => Volatile.Read(ref failing) ? throw new InvalidDataException("corrupt") : Task.FromResult("ok"),
            NoWait,
            recorder.Record);
        foreach (int start in new[] { 0, 16_000, 32_000, 48_000, 64_000 })
        {
            queue.Submit(Ref(MeetingChannel.Microphone, start));
        }

        await queue.Drain();
        Assert.True(queue.IsPaused);
        Assert.Equal(3, recorder.Outcomes.Count); // the fourth and fifth are held, not failed
        Assert.Equal(2, queue.Backlog);

        Volatile.Write(ref failing, false);
        queue.Resume();
        await queue.Drain();
        Assert.Equal(5, recorder.Outcomes.Count);
        Assert.Equal([new Outcome.Decoded("ok"), new Outcome.Decoded("ok")], recorder.Outcomes.TakeLast(2));
        // in their original order
        Assert.Equal([48_000, 64_000], recorder.Segments.TakeLast(2).Select(segment => segment.StartFrame));
    }

    /// <summary>
    /// Upstream gates on <c>EngineArbiter.awaitMeetingTurn</c>; the port's turn is
    /// <c>DictationPriority.WaitForTurn</c> (LocalASR, a file lock), stood in for here by a gate
    /// that opens when "dictation" ends.
    /// </summary>
    [Fact]
    public async Task ADecodeWaitsForItsTurn()
    {
        var recorder = new Recorder();
        var dictationEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new MeetingTranscriptionQueue(
            Read, (_, _) => Task.FromResult("ok"), _ => dictationEnded.Task, recorder.Record);
        queue.Submit(Ref(MeetingChannel.System, 0));
        await Task.Delay(50);
        Assert.Empty(recorder.Outcomes); // nothing decodes while dictation holds the engine
        dictationEnded.SetResult();
        await queue.Drain();
        Assert.Equal([new Outcome.Decoded("ok")], recorder.Outcomes);
    }

    [Fact]
    public async Task AWedgedDecodeIsCutOffAtTheDeadline()
    {
        var recorder = new Recorder();
        var queue = new MeetingTranscriptionQueue(
            Read,
            async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token);
                return "never";
            },
            NoWait,
            recorder.Record,
            deadline: _ => TimeSpan.FromMilliseconds(50));
        queue.Submit(Ref(MeetingChannel.Microphone, 0));
        await queue.Drain();
        Assert.Single(recorder.Outcomes);
        Assert.IsType<Outcome.Failed>(recorder.Outcomes[0]);
    }

    [Fact]
    public async Task AReadFailureIsAFailedSegmentNotACrash()
    {
        var recorder = new Recorder();
        var queue = new MeetingTranscriptionQueue(
            _ => throw new FileNotFoundException("gone"), (_, _) => Task.FromResult("ok"), NoWait, recorder.Record);
        queue.Submit(Ref(MeetingChannel.Microphone, 0));
        await queue.Drain();
        Assert.IsType<Outcome.Failed>(recorder.Outcomes.FirstOrDefault());
    }

    [Fact]
    public async Task AnEmptySegmentIsIgnored()
    {
        var recorder = new Recorder();
        var queue = new MeetingTranscriptionQueue(Read, (_, _) => Task.FromResult("ok"), NoWait, recorder.Record);
        queue.Submit(Ref(MeetingChannel.Microphone, 0, 0));
        await queue.Drain();
        Assert.Empty(recorder.Outcomes);
    }

    /// <summary>Port addition: the default deadline is max(60 s, 8x the segment).</summary>
    [Fact]
    public void TheDefaultDeadlineIsGenerousAndGrowsWithTheSegment()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), DefaultDeadline(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(160), DefaultDeadline(TimeSpan.FromSeconds(20)));
    }
}
