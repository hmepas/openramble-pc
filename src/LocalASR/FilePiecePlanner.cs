namespace LocalASR;

/// <summary>
/// A stretch of a recording, in samples of 16 kHz mono audio: <see cref="Start"/> inclusive,
/// <see cref="End"/> exclusive. Stands in for Swift's <c>Range&lt;Int&gt;</c>.
/// </summary>
public readonly record struct PieceRange(int Start, int End)
{
    /// <summary>How many samples (or envelope frames, where the range counts frames) it covers.</summary>
    public int Length => End - Start;

    public bool Overlaps(PieceRange other) => Start < other.End && other.Start < End;

    public bool Contains(int position) => position >= Start && position < End;
}

/// <summary>
/// Where a whole file is cut for the engine, fed as the file is read.
///
/// The same cuts <see cref="FileAudioChunks"/> decodes by: silence left out, each piece at
/// most <c>seconds</c> long, and a file of thirty seconds or less in one piece. A caller that
/// keeps the audio somewhere of its own pushes the samples it stores and asks for the pieces at
/// the end, so the audio is read once.
/// </summary>
/// <remarks>
/// The Swift original is a mutable <c>struct</c> used in place; a small class keeps that meaning
/// without the copy semantics a C# struct would add.
/// </remarks>
public sealed class FilePiecePlanner
{
    public const int SampleRate = 16_000;

    /// <summary>The longest piece, in seconds: what the Linux server sends its engine.</summary>
    public const int DefaultSeconds = 20;

    /// <summary>A file this short goes to the engine whole.</summary>
    internal const double WholeSeconds = 30.0;

    private readonly LoudnessEnvelope envelope = new();
    private readonly int seconds;
    private int total;

    public FilePiecePlanner(int seconds = DefaultSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(seconds, 15);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(seconds, 60);
        this.seconds = seconds;
    }

    /// <summary>Mono samples at 16 kHz, in order.</summary>
    public void Push(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples) { envelope.Push(sample); }
        total += samples.Length;
    }

    /// <summary>The pieces, as sample ranges: in order, never overlapping.</summary>
    public IReadOnlyList<PieceRange> Finish()
    {
        if (total <= 0) { return []; }
        if ((double)total / SampleRate <= WholeSeconds) { return [new PieceRange(0, total)]; }
        return PiecePlan.Pieces(envelope.Finish(), total, seconds);
    }
}
