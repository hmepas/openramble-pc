namespace DictationCore;

/// <summary>
/// Decodes the segments of a recording, one at a time, in the order each channel produced them -
/// and keeps going when one fails.
/// </summary>
/// <remarks>
/// <para>
/// Holds no audio. A submission is a <see cref="MeetingSegmentRef"/>, and the samples are read
/// back from the file when the decode's turn comes (production passes
/// <c>MeetingPcmWindowReader.Read</c>), so a backlog of a hundred segments behind a dictation
/// costs a few kilobytes rather than a hundred megabytes. That is the whole reason the transcript
/// is a projection over the WAV.
/// </para>
/// <para>
/// Not <see cref="StreamedSegmentRecognizer"/>: its first failure stops the stream and asks the
/// owner to re-decode the whole take, which is right for twenty seconds and catastrophic for
/// ninety minutes. Here a failed decode becomes one failed paragraph and the next segment
/// proceeds. Three failures in a row pause the queue - something is wrong with the engine, not
/// the audio - and hold what follows until someone asks to resume.
/// </para>
/// <para>
/// The seams are the importer's: <c>decode</c> has the shape of
/// <c>IImportRecognizer.Transcribe</c> and <c>awaitTurn</c> that of
/// <c>DictationPriority.WaitForTurn</c>, so dictation always goes first.
/// </para>
/// <para>
/// Thread-safe (the Swift original is an actor). Callbacks run on background threads, one segment
/// at a time; <c>emit</c> must not throw.
/// </para>
/// </remarks>
public sealed class MeetingTranscriptionQueue
{
    /// <summary>What became of one segment (the Swift original is an enum with associated values).</summary>
    public abstract record Outcome
    {
        private Outcome()
        {
        }

        public sealed record Decoded(string Text) : Outcome;

        public sealed record Failed(string Reason) : Outcome;
    }

    public const int FailuresBeforePause = 3;

    private readonly Lock gate = new();
    private readonly Func<MeetingSegmentRef, float[]> read;
    private readonly Func<float[], CancellationToken, Task<string>> decode;
    private readonly Func<CancellationToken, Task> awaitTurn;
    private readonly Func<MeetingSegmentRef, Outcome, Task> emit;
    private readonly Func<TimeSpan, TimeSpan> deadline;
    private readonly int sampleRate;
    private readonly List<MeetingSegmentRef> held = [];
    private readonly Dictionary<MeetingChannel, int> decodedFrames = [];
    private Task tail = Task.CompletedTask;
    private int pending;
    private int consecutiveFailures;
    private bool isPaused;

    /// <param name="deadline">
    /// How long a decode may take, given the segment's length. Generous because nobody is waiting
    /// for it, bounded because a wedged engine must not stall the queue forever. Defaults to
    /// max(60 s, 8x the segment).
    /// </param>
    public MeetingTranscriptionQueue(
        Func<MeetingSegmentRef, float[]> read,
        Func<float[], CancellationToken, Task<string>> decode,
        Func<CancellationToken, Task> awaitTurn,
        Func<MeetingSegmentRef, Outcome, Task> emit,
        Func<TimeSpan, TimeSpan>? deadline = null,
        int sampleRate = 16_000)
    {
        this.read = read;
        this.decode = decode;
        this.awaitTurn = awaitTurn;
        this.emit = emit;
        this.deadline = deadline ?? DefaultDeadline;
        this.sampleRate = sampleRate;
    }

    public bool IsPaused
    {
        get { lock (gate) { return isPaused; } }
    }

    /// <summary>The end of the last decoded segment per channel - where a relaunch would resume.</summary>
    public IReadOnlyDictionary<MeetingChannel, int> DecodedFrames
    {
        get { lock (gate) { return new Dictionary<MeetingChannel, int>(decodedFrames); } }
    }

    /// <summary>Segments submitted and not yet finished.</summary>
    public int Backlog
    {
        get { lock (gate) { return pending + held.Count; } }
    }

    public static TimeSpan DefaultDeadline(TimeSpan segment) =>
        TimeSpan.FromSeconds(Math.Max(60, segment.TotalSeconds * 8));

    public void Submit(MeetingSegmentRef segment)
    {
        if (segment.FrameCount <= 0) { return; }
        lock (gate)
        {
            if (isPaused)
            {
                held.Add(segment);
                return;
            }

            Enqueue(segment);
        }
    }

    /// <summary>Take the held segments up again after failures paused the queue.</summary>
    public void Resume()
    {
        lock (gate)
        {
            if (!isPaused) { return; }
            isPaused = false;
            consecutiveFailures = 0;
            List<MeetingSegmentRef> resumed = [.. held];
            held.Clear();
            foreach (MeetingSegmentRef segment in resumed) { Enqueue(segment); }
        }
    }

    /// <summary>Wait for everything submitted so far.</summary>
    public async Task Drain()
    {
        while (true)
        {
            Task task;
            lock (gate) { task = tail; }
            await task.ConfigureAwait(false);
            lock (gate)
            {
                if (ReferenceEquals(task, tail)) { return; }
            }
        }
    }

    /// <summary>Caller holds <see cref="gate"/>.</summary>
    private void Enqueue(MeetingSegmentRef segment)
    {
        pending++;
        Task previous = tail;
        tail = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            await Process(segment).ConfigureAwait(false);
        });
    }

    private async Task Process(MeetingSegmentRef segment)
    {
        try
        {
            lock (gate)
            {
                if (isPaused)
                {
                    held.Add(segment);
                    return;
                }
            }

            await awaitTurn(CancellationToken.None).ConfigureAwait(false);
            var seconds = TimeSpan.FromSeconds((double)segment.FrameCount / sampleRate);
            Outcome outcome;
            try
            {
                float[] samples = read(segment);
                string text = await TranscriptionDeadline.WithTranscriptionDeadline(
                    deadline(seconds), token => decode(samples, token)).ConfigureAwait(false);
                outcome = new Outcome.Decoded(text.Trim());
                lock (gate) { consecutiveFailures = 0; }
            }
            catch (Exception error)
            {
                outcome = new Outcome.Failed(error.Message);
                lock (gate)
                {
                    consecutiveFailures++;
                    if (consecutiveFailures >= FailuresBeforePause) { isPaused = true; }
                }
            }

            lock (gate)
            {
                decodedFrames[segment.Channel] = Math.Max(
                    decodedFrames.GetValueOrDefault(segment.Channel), segment.EndFrame);
            }

            await emit(segment, outcome).ConfigureAwait(false);
        }
        finally
        {
            lock (gate) { pending--; }
        }
    }
}
