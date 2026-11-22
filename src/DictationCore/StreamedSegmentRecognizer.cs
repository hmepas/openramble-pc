namespace DictationCore;

/// <summary>
/// Recognizes the pieces of a running take, in the order they were cut.
///
/// Segments arrive from the capture's own queue while the person is still speaking. This type
/// does two things with them and nothing else: it keeps them in order, and it keeps at most one
/// decode in flight - which is not a style choice but the runtime's rule, since only one
/// recognition may run across all sessions of a model.
///
/// <b>A failed segment does not half-recognize a take.</b> The first error stops the stream and
/// is reported at the end, so the owner can fall back to recognizing the whole recording the way
/// it always did. Half a transcript silently missing its middle is exactly the failure this
/// project refuses to ship; a slower correct answer is the right trade every time.
/// </summary>
public sealed class StreamedSegmentRecognizer
{
    /// <summary>What the stream produced.</summary>
    public abstract record Outcome
    {
        private protected Outcome()
        {
        }

        /// <summary>Every segment recognized, in order.</summary>
        public sealed record Recognized(IReadOnlyList<string> Texts) : Outcome;

        /// <summary>
        /// A segment failed. Nothing here can be trusted; recognize the whole take instead. The
        /// error is carried so the caller can say why.
        /// </summary>
        public sealed record Failed(Exception Error) : Outcome;
    }

    private readonly Func<float[], Task<ASRResult>> transcribe;
    private readonly Lock gate = new();

    /// <summary>
    /// The end of the chain. Each submission waits for the previous one, which is what keeps
    /// both the order and the one-at-a-time rule without a queue of its own.
    /// </summary>
    private Task? tail;

    /// <summary>
    /// One entry per segment handed over, in order, including the ones that recognized as
    /// nothing. Kept aligned with <see cref="sampleCounts"/> so the owner can drop the last
    /// segment and re-recognize it with the tail.
    /// </summary>
    private readonly List<string> texts = [];
    private readonly List<int> sampleCounts = [];
    private Exception? failure;
    private bool stopped;
    private bool cancelled;

    public StreamedSegmentRecognizer(Func<float[], Task<ASRResult>> transcribe)
    {
        this.transcribe = transcribe;
    }

    /// <summary>
    /// Hand over one finished segment. Returns immediately.
    ///
    /// Safe to call from the capture's queue: nothing here blocks, and the work happens on the
    /// chained task rather than the caller's thread.
    /// </summary>
    public void Submit(float[] samples)
    {
        lock (gate)
        {
            if (stopped || failure is not null || samples.Length == 0) { return; }

            Task? previous = tail;
            sampleCounts.Add(samples.Length);
            tail = Task.Run(() => Recognize(previous, samples));
        }
    }

    private async Task Recognize(Task? previous, float[] samples)
    {
        if (previous is not null) { await previous.ConfigureAwait(false); }

        // A failure earlier in the chain makes every later segment pointless: the take is going
        // to be recognized whole regardless. A cancelled take has no use for any of them.
        if (IsAbandoned) { return; }

        try
        {
            ASRResult result = await transcribe(samples).ConfigureAwait(false);
            Append(result.Text.Trim());
        }
        catch (Exception error)
        {
            RecordFailure(error);
        }
    }

    private bool IsAbandoned { get { lock (gate) { return failure is not null || cancelled; } } }

    private void Append(string text)
    {
        lock (gate)
        {
            if (failure is not null || cancelled) { return; }

            // Appended even when empty. A segment of pure breath recognizes as nothing, and
            // dropping it here would misalign the transcripts from the sample counts - which is
            // what the owner uses to take the last segment back when the tail turns out too short
            // to stand alone. The blanks are filtered where the pieces are joined.
            texts.Add(text);
        }
    }

    private void RecordFailure(Exception error)
    {
        lock (gate)
        {
            if (!cancelled) { failure ??= error; }
        }
    }

    /// <summary>
    /// Unlike <see cref="Finish"/>, abandon queued work. Inference already in flight may finish,
    /// but it cannot publish its text or start the next old segment.
    /// </summary>
    /// <remarks>
    /// Escape, a microphone that never started, a session that ended: the segments still queued
    /// belong to a take nobody will insert, and running them would hold the engine against the
    /// next dictation - and could hand a late result to it.
    /// </remarks>
    public void Cancel()
    {
        lock (gate)
        {
            stopped = true;
            cancelled = true;
            texts.Clear();
        }
    }

    /// <summary>
    /// Wait for everything submitted so far, and say how it went.
    ///
    /// After this returns, no further submission is accepted - the take is over, and a segment
    /// arriving late would append text after the tail the caller is about to add.
    /// </summary>
    public async Task<Outcome> Finish()
    {
        Task? pending;
        lock (gate)
        {
            stopped = true;
            pending = tail;
        }

        if (pending is not null) { await pending.ConfigureAwait(false); }

        lock (gate)
        {
            if (cancelled) { return new Outcome.Failed(new OperationCanceledException()); }
            if (failure is { } error) { return new Outcome.Failed(error); }

            return new Outcome.Recognized(texts.ToArray());
        }
    }

    /// <summary>
    /// How many segments have finished. For the speed report, and for tests that need to know
    /// the stream actually ran.
    /// </summary>
    public int RecognizedCount { get { lock (gate) { return texts.Count; } } }

    /// <summary>
    /// Sample counts of the segments handed over, in order.
    ///
    /// The owner needs the last one: if the person releases the key just after a cut, the tail
    /// can be too short for the decoder to be trusted with, and the cure is to hand back the last
    /// segment and recognize it together with the tail rather than decode a fragment on its own.
    /// </summary>
    public IReadOnlyList<int> SubmittedSampleCounts { get { lock (gate) { return sampleCounts.ToArray(); } } }
}
