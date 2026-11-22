namespace DictationCore.Audio;

/// <summary>
/// Everything one recording owns between the device and the controller: the WAV, the frame
/// queue that feeds it in order, the bounded in-memory copy and the segmenter.
///
/// Both platform captures used to carry their own copy of this, held together by flags -
/// "the disk failed", "the limit was reported", "the buffer still holds the take", "a stop has
/// begun", "the drain was abandoned" - whose combinations nobody had written down. Two of those
/// combinations were defects: a drain that overran its stop budget kept committing frames into
/// a file the stop had already closed, which read as a full disk and ended the <i>next</i>
/// dictation; and a frame the memory copy refused after a disk failure still went to the
/// segmenter, so the stream claimed more audio than the take held and the join refused it.
///
/// Here the two things that change are stated outright. <see cref="Storage"/> says where the
/// take lives; <see cref="Phase"/> says whether it is still being recorded. <see cref="Commit"/>
/// is the one transition function between them, and it can raise an <see cref="Event"/> only
/// while the take is <see cref="Phase.Recording"/>: once a stop has taken over, the stop's result
/// is the take's whole answer, and once it is <see cref="Phase.Stopped"/> a late frame belongs to
/// nobody.
/// </summary>
/// <remarks>
/// Device-free on purpose, like <see cref="FrameSink"/>: the cases worth testing are disk and
/// memory cases, and the test environment has neither a microphone nor a disk that fails on cue.
/// </remarks>
internal sealed class RecordingTake
{
    /// <summary>Where the take's audio lives.</summary>
    public enum Storage
    {
        /// <summary>The WAV and the bounded memory copy both hold the take from its first sample.</summary>
        DiskAndMemory,

        /// <summary>
        /// The memory copy was given up - the cap was crossed, or a stop abandoned its drain - and
        /// the healthy WAV carries the take.
        /// </summary>
        DiskOnly,

        /// <summary>The WAV failed; memory is the only copy.</summary>
        MemoryOnly,

        /// <summary>
        /// The WAV failed and memory reached its cap: the complete prefix is kept, every later
        /// frame is refused, and the take waits for its stop.
        /// </summary>
        MemoryFull,

        /// <summary>Neither copy holds the take any more.</summary>
        Lost,
    }

    /// <summary>Whether the take is still being recorded.</summary>
    public enum Phase
    {
        /// <summary>Frames are committed and failures are the take's to report.</summary>
        Recording,

        /// <summary>
        /// A stop has taken over. Frames still queued are the tail and are committed as before;
        /// whatever happens to them is answered by the stop's result, never by an event.
        /// </summary>
        Stopping,

        /// <summary>Closed. Every frame is refused, whoever delivers it.</summary>
        Stopped,
    }

    /// <summary>What a commit has to tell the capture. Raised only while recording.</summary>
    public enum Event
    {
        /// <summary>The WAV failed; memory carries the take on. For the log.</summary>
        DiskFailed,

        /// <summary>
        /// Memory is the only copy and it is full. The take has to end here, and what was kept is
        /// complete.
        /// </summary>
        MemoryLimitReached,

        /// <summary>The WAV failed after memory had already been given up: nothing holds the take.</summary>
        NothingKept,
    }

    /// <summary>What a finished stop hands to the session.</summary>
    /// <param name="Path">The WAV, closed - or whatever of it a failed writer kept.</param>
    /// <param name="Duration">What the take holds: the file's length, or memory's when the file failed.</param>
    /// <param name="Drained">Whether the frame queue was emptied within the stop's budget.</param>
    /// <param name="Storage">Where the take ended up, for the log.</param>
    public sealed record Stopped(string Path, TimeSpan Duration, bool Drained, Storage Storage);

    private readonly Lock gate = new();
    private readonly WAVWriter writer;
    private readonly FrameSink sink = new();
    private readonly RecognitionSampleBuffer memory;
    private readonly RecordingSegmentAttachment segments;
    private readonly Action<Event> report;
    private readonly int sampleRate;
    private Storage storage = Storage.DiskAndMemory;
    private Phase phase = Phase.Recording;

    /// <param name="writer">The take's WAV, already open.</param>
    /// <param name="maximumBufferedSamples">The memory cap.</param>
    /// <param name="segmentSink">Where finished pieces of the running take go, if anyone asked.</param>
    /// <param name="report">
    /// Called from the frame queue's own thread, under this type's lock - the price of "only
    /// while recording" being exact: a stop that begins meanwhile waits for the handler to
    /// return. The lock is reentrant, so a handler may stop or abort the take on its own thread;
    /// it must not block on another thread that is stopping, aborting or reading this take. The
    /// captures' handlers log, close their session and hand the news on to the controller, which
    /// decides under its own lock and finishes the take on a worker.
    /// </param>
    /// <param name="sampleRate">The rate of the samples, for the duration of a memory-only take.</param>
    /// <param name="segmenter">The cut rule; only tests choose another.</param>
    public RecordingTake(
        WAVWriter writer,
        int maximumBufferedSamples,
        Action<float[]>? segmentSink,
        Action<Event> report,
        int sampleRate = 16_000,
        SpeechSegmenter? segmenter = null)
    {
        segments = new RecordingSegmentAttachment(segmenter);
        this.writer = writer;
        this.report = report;
        this.sampleRate = sampleRate;
        memory = new RecognitionSampleBuffer(maximumBufferedSamples);
        segments.SetSink(segmentSink);
        Deliver = sink.Start(frame =>
        {
            Commit(frame);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Hands a frame to the ordered queue and returns at once - safe from the device's own thread.
    /// </summary>
    public Action<float[]> Deliver { get; }

    public string FilePath => writer.FilePath;

    public Storage CurrentStorage { get { lock (gate) { return storage; } } }

    public Phase CurrentPhase { get { lock (gate) { return phase; } } }

    public void SetSegmentSink(Action<float[]>? segmentSink) => segments.SetSink(segmentSink);

    /// <summary>
    /// One frame from the queue into the take, the memory and the segmenter, in that order.
    /// </summary>
    /// <remarks>
    /// The segmenter is fed the same committed frames, inside the ordered drain, because order is
    /// the whole contract: a segmenter fed out of order would place seams in the wrong places. It
    /// is fed only frames a copy of the take actually holds - otherwise the stream would claim
    /// audio no copy can give back. The memory cap is judged here too, and what it means depends
    /// on the disk: a healthy WAV carries the take past it, so dictation goes on for as long as
    /// the person speaks. A WAV that could not be written leaves memory as the only copy, so the
    /// take ends at the cap with its complete prefix kept for recognition.
    /// </remarks>
    internal void Commit(float[] frame)
    {
        List<Event>? events = null;
        bool held;
        lock (gate)
        {
            if (phase == Phase.Stopped || storage is Storage.MemoryFull or Storage.Lost) { return; }

            if (storage is Storage.DiskAndMemory or Storage.DiskOnly)
            {
                try
                {
                    writer.Append(frame);
                }
                catch (WAVWriter.Failure)
                {
                    storage = storage == Storage.DiskAndMemory ? Storage.MemoryOnly : Storage.Lost;
                    (events ??= []).Add(storage == Storage.Lost ? Event.NothingKept : Event.DiskFailed);
                }
            }

            switch (storage)
            {
                case Storage.DiskAndMemory:
                    // A healthy file carries the take on: the in-memory path is given up for the
                    // whole take and recognition reads the WAV.
                    if (!memory.Append(frame)) { storage = Storage.DiskOnly; }
                    break;
                case Storage.MemoryOnly:
                    if (!memory.Append(frame, preserveAtLimit: true))
                    {
                        storage = Storage.MemoryFull;
                        (events ??= []).Add(Event.MemoryLimitReached);
                    }

                    break;
            }

            held = storage is Storage.DiskAndMemory or Storage.DiskOnly or Storage.MemoryOnly;
        }

        // Outside the lock, so a stop is never held behind the segment sink. The order is still
        // the drain's, and a frame decided on here is already in a copy of the take.
        if (held) { segments.Submit(frame); }

        if (events is null) { return; }

        // Under the lock, and the phase asked again before each one. An event decided above can
        // meet a stop that began since - while the segment sink ran, or inside the handler of the
        // event before it - and once a stop has taken over, its result is the take's whole
        // answer. Deciding under the lock and raising after it left exactly that gap: a take
        // that had reached its memory cap could report it to a session already finishing it, and
        // a take with no listener would report as failed a stop that was about to succeed.
        // Raising here closes it rather than narrowing it: a stop that begins while a handler
        // runs waits for that handler, and the next event sees the stop.
        lock (gate)
        {
            foreach (Event happened in events)
            {
                if (phase != Phase.Recording) { return; }

                report(happened);
            }
        }
    }

    /// <summary>
    /// A stop has begun: from here on the take reports nothing, its stop answers for it.
    /// </summary>
    public void BeginStopping()
    {
        lock (gate)
        {
            if (phase == Phase.Recording) { phase = Phase.Stopping; }
        }
    }

    /// <summary>
    /// Close the queue, wait for it within <paramref name="budget"/>, and close the WAV.
    /// </summary>
    /// <remarks>
    /// A drain that does not finish in time is abandoned, and the take is
    /// <see cref="Phase.Stopped"/> before the file is closed under it: whatever it still commits
    /// afterwards is refused here rather than written into a closed file and mistaken for a full
    /// disk. The in-memory copy goes with it - it is missing whatever the queue still held, and a
    /// recognition run on a truncated buffer would silently drop the end of the sentence; the WAV
    /// is short by the same tail but is at least the honest record.
    /// </remarks>
    public async Task<Stopped> Stop(TimeSpan budget)
    {
        BeginStopping();

        // Completing the writer happens inside Finish() before it awaits anything, so the queue is
        // closed the moment this line runs whether or not the wait below succeeds.
        Task drain = sink.Finish();
        bool drained = await Bounded(drain, budget).ConfigureAwait(false);
        if (drained)
        {
            // Rethrows whatever broke the queue itself - that has to reach the person, not the log.
            await drain.ConfigureAwait(false);
        }
        else
        {
            _ = drain.ContinueWith(
                static _ => { },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        Storage final;
        int held;
        lock (gate)
        {
            phase = Phase.Stopped;
            if (!drained)
            {
                memory.Discard();
                storage = storage switch
                {
                    Storage.DiskAndMemory => Storage.DiskOnly,
                    Storage.MemoryOnly or Storage.MemoryFull => Storage.Lost,
                    _ => storage,
                };
            }

            final = storage;
            held = memory.Count;
        }

        if (final is Storage.DiskAndMemory or Storage.DiskOnly)
        {
            TimeSpan length = writer.Duration;
            return new Stopped(writer.Close(), length, drained, final);
        }

        // A take whose file failed lives in memory; its duration is what memory holds, and a
        // writer that already failed may fail its close too without costing the words.
        return new Stopped(CloseFailedWriter(), TimeSpan.FromSeconds((double)held / sampleRate), drained, final);
    }

    /// <summary>Drop the take: the queue, the memory and the file. Late frames are refused.</summary>
    public void Abort()
    {
        lock (gate)
        {
            phase = Phase.Stopped;
            memory.Discard();
        }

        sink.Cancel();
        writer.Discard();
    }

    /// <summary>Move the recognizer-ready copy out, if memory still holds the whole take.</summary>
    public float[]? TakeSamples()
    {
        lock (gate) { return memory.Take(); }
    }

    /// <summary>How much of the take left in segments. Closes the segmenter.</summary>
    public int ConsumedSampleCount() => segments.DrainConsumedSampleCount();

    private string CloseFailedWriter()
    {
        try
        {
            return writer.Close();
        }
        catch (WAVWriter.Failure)
        {
            return writer.FilePath;
        }
    }

    /// <summary>Wait for a task, but never past <paramref name="budget"/>.</summary>
    private static async Task<bool> Bounded(Task task, TimeSpan budget)
    {
        // Asked first, because a budget spent by an earlier wait would otherwise report a task
        // that is already finished as abandoned.
        if (task.IsCompleted) { return true; }

        if (budget <= TimeSpan.Zero) { return false; }

        using var expiry = new CancellationTokenSource();
        Task finished = await Task.WhenAny(task, Task.Delay(budget, expiry.Token)).ConfigureAwait(false);
        if (ReferenceEquals(finished, task))
        {
            await expiry.CancelAsync().ConfigureAwait(false);
            return true;
        }

        return false;
    }
}
