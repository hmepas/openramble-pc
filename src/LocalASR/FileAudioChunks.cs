using DictationCore;

namespace LocalASR;

/// <summary>One piece of a long recording. Frames are samples of 16 kHz mono audio.</summary>
internal sealed record FileAudioChunk(float[] Samples, int StartFrame, int EndFrame);

/// <summary>
/// A recording or an imported file in the pieces the Linux server would send its engine
/// (<c>apps/cli/src/plan.rs</c> upstream): silence left out, every piece at most
/// <c>seconds</c> long, no overlap. A plan needs the whole file's loudness, so a file is read
/// twice, never held whole.
///
/// The rule replaced a pause policy with a fixed speech level and 2 s of shared context at each
/// seam. Imported files have no known gain, so that level read quiet speech as a pause and cut
/// inside clauses, which the model then dropped; and the shared context pulled a mixed-language
/// piece toward its neighbour's language. On upstream's hour test files the planner lost no
/// English or Russian words and half as many on the mixed files.
/// </summary>
/// <remarks>
/// <para>
/// Two forms, as upstream has: samples already in memory (a long dictation, recognized in
/// <see cref="LocalTranscriber"/>), and a file (<see cref="Read"/>), streamed through
/// <see cref="AudioFileStream"/>. The Swift actor is a plain class that blocks: the file form's
/// calls read a disk or a decoder's pipe, and their caller runs them on a thread of their own.
/// </para>
/// <para>
/// <b>Deviation from the Swift original:</b> upstream knows a file's length from its header
/// before reading it, and reads a file of thirty seconds or less once, whole. A decoder's pipe
/// has no header, so the first pass learns the length: it keeps the samples while they are
/// thirty seconds or fewer - a short file is then not read again - and drops them past that.
/// The second pass also drops the silence between pieces as it is read, rather than holding it
/// until the next piece is complete, so a long pause is never held either.
/// </para>
/// </remarks>
internal sealed class FileAudioChunks : IDisposable
{
    /// <summary>
    /// The server's piece length. Pieces this short also never reach any long-form segmentation
    /// an engine might run past 30 s.
    /// </summary>
    public const int DefaultSeconds = FilePiecePlanner.DefaultSeconds;

    private const int WholeSamples = (int)(FilePiecePlanner.WholeSeconds * FilePiecePlanner.SampleRate);

    /// <summary>The whole recording when it is in memory, or when a file turned out short.</summary>
    private readonly float[]? samples;

    /// <summary>The file a second pass reads its pieces from; <c>null</c> for samples in memory.</summary>
    private readonly string? path;

    private readonly bool whole;
    private readonly int seconds;
    private Queue<PieceRange>? pieces;
    private bool ended;
    private AudioFileStream? stream;
    private readonly List<float> pending = [];
    private int pendingStart;
    private bool streamEnded;

    public FileAudioChunks(float[] samples, int seconds = DefaultSeconds)
    {
        this.samples = samples;
        Duration = (double)samples.Length / FilePiecePlanner.SampleRate;
        whole = Duration <= FilePiecePlanner.WholeSeconds;
        this.seconds = seconds;
    }

    private FileAudioChunks(string path, int total, float[]? head, IReadOnlyList<PieceRange> plan, int seconds)
    {
        this.path = path;
        samples = head;
        Duration = (double)total / FilePiecePlanner.SampleRate;
        whole = head is not null;
        pieces = new Queue<PieceRange>(plan);
        this.seconds = seconds;
    }

    /// <summary>
    /// The first pass over a file: its loudness and its length, then the cuts. Blocks; see the
    /// type remarks.
    /// </summary>
    /// <exception cref="AudioFileReader.Failure">The file cannot be read, or holds no audio.</exception>
    public static FileAudioChunks Read(string path, int seconds = DefaultSeconds, CancellationToken cancellationToken = default)
    {
        var envelope = new LoudnessEnvelope();
        var head = new List<float>();
        int total = 0;
        using (AudioFileStream stream = AudioFileStream.Open(path))
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                float[] part = stream.Read();
                if (part.Length == 0) { break; }
                foreach (float sample in part) { envelope.Push(sample); }
                total = checked(total + part.Length);
                if (total <= WholeSamples) { head.AddRange(part); }
                else if (head.Count > 0) { head = []; }
            }
        }

        if (total == 0) { throw new AudioFileReader.Failure.EmptyFile(); }

        return total <= WholeSamples
            ? new FileAudioChunks(path, total, [.. head], [], seconds)
            : new FileAudioChunks(path, total, null, PiecePlan.Pieces(envelope.Finish(), total, seconds), seconds);
    }

    /// <summary>The whole recording, in seconds.</summary>
    public double Duration { get; }

    /// <summary>
    /// The next piece, or <c>null</c> once every piece has been handed out. For a file it reads
    /// the file; see the type remarks.
    /// </summary>
    public FileAudioChunk? Next(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (whole)
        {
            if (ended) { return null; }
            ended = true;
            return new FileAudioChunk(samples!, 0, samples!.Length);
        }

        pieces ??= new Queue<PieceRange>(Plan());
        if (!pieces.TryDequeue(out PieceRange piece))
        {
            stream?.Dispose();
            stream = null;
            return null;
        }

        if (path is null)
        {
            return new FileAudioChunk(samples![piece.Start..piece.End], piece.Start, piece.End);
        }

        // The second pass: a fresh stream, read up to the end of this piece. Gaps between pieces
        // are the silence the plan leaves out, dropped as they arrive.
        stream ??= AudioFileStream.Open(path);
        SkipTo(piece.Start);
        while (pendingStart + pending.Count < piece.End && !streamEnded)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float[] part = stream.Read(FilePiecePlanner.SampleRate);
            if (part.Length == 0) { streamEnded = true; }
            else { pending.AddRange(part); }
            SkipTo(piece.Start);
        }

        int take = Math.Min(pending.Count, piece.End - pendingStart);
        float[] chunk = [.. pending.GetRange(0, take)];
        pending.RemoveRange(0, take);
        pendingStart += take;
        return new FileAudioChunk(chunk, piece.Start, piece.Start + take);
    }

    public void Dispose()
    {
        stream?.Dispose();
        stream = null;
    }

    private void SkipTo(int start)
    {
        int skip = Math.Min(pending.Count, Math.Max(0, start - pendingStart));
        pending.RemoveRange(0, skip);
        pendingStart += skip;
    }

    /// <summary>The loudness of the whole recording in memory, then the cuts.</summary>
    private IReadOnlyList<PieceRange> Plan()
    {
        var envelope = new LoudnessEnvelope();
        foreach (float sample in samples!) { envelope.Push(sample); }
        return PiecePlan.Pieces(envelope.Finish(), samples!.Length, seconds);
    }
}

/// <summary>
/// The server's loudness envelope (<c>apps/cli/src/decode.rs</c> upstream): the RMS of each
/// 20 ms after a second-order Butterworth high-pass at 100 Hz, so rumble, hum and the swell of
/// pink noise do not count as loudness.
/// </summary>
/// <remarks>A mutable Swift struct used in place; a small class here.</remarks>
internal sealed class LoudnessEnvelope
{
    private readonly List<float> frames = [];
    private float sum;
    private int count;
    private float x1, x2, y1, y2;

    public void Push(float sample)
    {
        float high = 0.9726139f * (sample - 2 * x1 + x2) + 1.9444777f * y1 - 0.945978f * y2;
        x2 = x1;
        x1 = sample;
        y2 = y1;
        y1 = high;
        sum += high * high;
        count += 1;
        if (count == PiecePlan.Frame) { Close(); }
    }

    private void Close()
    {
        frames.Add(MathF.Sqrt(sum / count));
        sum = 0;
        count = 0;
    }

    /// <summary>The frames, the last one possibly shorter than 20 ms.</summary>
    public float[] Finish()
    {
        if (count > 0) { Close(); }
        return [.. frames];
    }
}

/// <summary>
/// <c>apps/cli/src/plan.rs</c> upstream, kept in step with it: both test suites pin the same cut
/// positions. Pure, on 20 ms frames.
///
/// 1. Skip silence. The floor of each second is the 10th-percentile frame of the minute around
///    it (never below -80 dBFS), and a frame 6 dB above it is voiced. Voiced frames under 2 s
///    apart form an island, an island with under 0.1 s of voiced frames is a click, and each
///    kept island gets 1 s of audio on either side. A quiet speaker counts as long as they stand
///    clear of their own floor.
/// 2. Cut at pauses. A longer region becomes <c>ceil(d / (0.8 × cap))</c> nearly equal pieces,
///    and each cut moves to the quietest 200 ms within <c>min(2.5 s, (cap - d/n) / 2)</c> of the
///    even split, so no piece is longer than the cap.
/// </summary>
internal static class PiecePlan
{
    /// <summary>Samples per envelope frame: 20 ms at 16 kHz.</summary>
    public const int Frame = 320;
    public const int FramesPerSecond = 50;
    private const float Margin = 2;
    private const int Near = 60 * FramesPerSecond;
    private const float LowestFloor = 1e-4f;
    private const int Pad = FramesPerSecond;
    private const int MergeGap = 2 * FramesPerSecond;
    private const int MinVoiced = FramesPerSecond / 10;
    private const int Smooth = 5;
    private const int MaxShift = FramesPerSecond * 5 / 2;
    private const double Target = 0.8;

    /// <summary>The pieces of a whole recording, in samples.</summary>
    public static IReadOnlyList<PieceRange> Pieces(float[] envelope, int samples, float cap)
    {
        var frames = new PieceRange(0, (samples + Frame - 1) / Frame);
        var result = new List<PieceRange>();
        foreach (PieceRange piece in Plan(envelope, frames, cap, gate: true))
        {
            int end = Math.Min(piece.End * Frame, samples);
            if (piece.Start * Frame < end) { result.Add(new PieceRange(piece.Start * Frame, end)); }
        }

        return result;
    }

    /// <summary>
    /// The pieces of <paramref name="span"/>, in frames of <paramref name="envelope"/>, in order
    /// and never overlapping, each at most <paramref name="cap"/> seconds. Without the gate the
    /// whole span is covered.
    /// </summary>
    public static IReadOnlyList<PieceRange> Plan(float[] envelope, PieceRange span, float cap, bool gate)
    {
        span = new PieceRange(Math.Min(span.Start, envelope.Length), Math.Min(span.End, envelope.Length));
        if (span.Length <= 0) { return []; }
        IReadOnlyList<PieceRange> regions = gate ? VoicedRegions(envelope, span) : [span];
        int capFrames = Math.Max((int)(cap * FramesPerSecond), 1);
        return regions.SelectMany(region => Cut(envelope, region, capFrames)).ToList();
    }

    private static float Threshold(float[] envelope, int second)
    {
        int centre = second * FramesPerSecond + FramesPerSecond / 2;
        int from = Math.Max(0, centre - Near / 2);
        int to = Math.Min(centre + Near / 2, envelope.Length);
        float[] sorted = envelope[from..to];
        Array.Sort(sorted);
        return Math.Max(sorted[sorted.Length / 10], LowestFloor) * Margin;
    }

    private static List<PieceRange> VoicedRegions(float[] envelope, PieceRange span)
    {
        float threshold = 0;
        var islands = new List<(int Start, int End, int Voiced)>();
        for (int frame = span.Start; frame < span.End; frame++)
        {
            if (frame == span.Start || frame % FramesPerSecond == 0)
            {
                threshold = Threshold(envelope, frame / FramesPerSecond);
            }

            if (envelope[frame] <= threshold) { continue; }
            if (islands.Count > 0 && frame - islands[^1].End < MergeGap)
            {
                (int Start, int End, int Voiced) last = islands[^1];
                islands[^1] = (last.Start, frame + 1, last.Voiced + 1);
            }
            else
            {
                islands.Add((frame, frame + 1, 1));
            }
        }

        var regions = new List<PieceRange>();
        foreach ((int start, int end, int voiced) in islands)
        {
            if (voiced < MinVoiced) { continue; }
            var padded = new PieceRange(Math.Max(start - Pad, span.Start), Math.Min(end + Pad, span.End));
            if (regions.Count > 0 && padded.Start <= regions[^1].End)
            {
                PieceRange last = regions[^1];
                regions[^1] = new PieceRange(last.Start, Math.Max(last.End, padded.End));
            }
            else
            {
                regions.Add(padded);
            }
        }

        return regions;
    }

    private static List<PieceRange> Cut(float[] envelope, PieceRange region, int cap)
    {
        int length = region.Length;
        if (length <= cap) { return [region]; }
        int n = (int)Math.Ceiling(length / (Target * cap));
        int longestEven = (length + n - 1) / n;
        int reach = Math.Min(MaxShift, Math.Max(0, cap - longestEven) / 2);

        float Loudness(int frame)
        {
            float total = 0;
            int to = Math.Min(frame + Smooth + 1, region.End);
            for (int index = Math.Max(frame - Smooth, region.Start); index < to; index++)
            {
                total += envelope[index];
            }

            return total;
        }

        var pieces = new List<PieceRange>();
        int start = region.Start;
        for (int k = 1; k < n; k++)
        {
            int even = region.Start + k * length / n;
            int from = Math.Max(even - reach, start + 1);
            int to = Math.Min(even + reach, region.End - 1);
            // The quietest frame, then the one nearest the even split, then the earlier.
            int at = even;
            if (from <= to)
            {
                at = from;
                float best = Loudness(from);
                for (int candidate = from + 1; candidate <= to; candidate++)
                {
                    float loudness = Loudness(candidate);
                    if (loudness < best
                        || (loudness == best && Math.Abs(candidate - even) < Math.Abs(at - even)))
                    {
                        at = candidate;
                        best = loudness;
                    }
                }
            }

            pieces.Add(new PieceRange(start, at));
            start = at;
        }

        pieces.Add(new PieceRange(start, region.End));
        return pieces;
    }
}

/// <summary>
/// Pieces never overlap, so joining is appending: every word moves by its piece's start in the
/// recording, and the texts follow one another.
/// </summary>
/// <remarks>
/// <b>Deviation from the Swift original:</b> upstream throws when the engine returns text
/// without the word timings it asked for, and builds the text from the words. Here the text is
/// the pieces' texts joined, and a piece without words only contributes no words: the
/// sherpa-onnx adapter deliberately drops timestamps that are not sane seconds, so text without
/// words is a legitimate answer, not a broken one.
/// </remarks>
internal sealed class TimedTranscriptJoiner
{
    private readonly List<ASRResult.Word> words = [];
    private readonly List<string> texts = [];

    public IReadOnlyList<ASRResult.Word> Words => words;

    /// <summary>The pieces' texts, trimmed, blanks dropped, joined with a single space.</summary>
    public string Text => string.Join(' ', texts);

    public void Append(ASRResult result, FileAudioChunk chunk, TimeSpan duration)
    {
        string text = result.Text.Trim();
        if (text.Length > 0) { texts.Add(text); }

        TimeSpan offset = TimeSpan.FromSeconds((double)chunk.StartFrame / FilePiecePlanner.SampleRate);
        // Alignment jitter must not produce reversed intervals at a join.
        TimeSpan previous = words.Count > 0 ? words[^1].Start : TimeSpan.Zero;
        foreach (ASRResult.Word word in result.Words)
        {
            TimeSpan start = Max(previous, Min(duration, offset + word.Start));
            words.Add(word with { Start = start, End = Max(start, Min(duration, offset + word.End)) });
            previous = start;
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
