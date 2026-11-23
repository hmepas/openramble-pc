using DictationCore;

namespace LocalASR.Tests;

public sealed class FileAudioChunksTests : IDisposable
{
    private readonly string directory;

    public FileAudioChunksTests()
    {
        directory = Path.Combine(Path.GetTempPath(), $"chunks-{Guid.NewGuid():D}");
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
    }

    private static PieceRange R(int start, int end) => new(start, end);

    private static List<FileAudioChunk> All(FileAudioChunks source)
    {
        var chunks = new List<FileAudioChunk>();
        while (source.Next() is { } chunk) { chunks.Add(chunk); }
        return chunks;
    }

    [Fact]
    public void QuietSpeechFarBelowTheMicrophoneThresholdIsKept()
    {
        // -54 dBFS, a tenth of the live microphone's speech peak.
        FileAudioChunks source = Recording(61, index => Syllables(index, 0.002f));
        int covered = 0;
        foreach (FileAudioChunk chunk in All(source))
        {
            Assert.Equal(covered, chunk.StartFrame);
            covered = chunk.EndFrame;
        }

        // Imported audio has no guaranteed microphone gain.
        Assert.Equal(61 * 16_000, covered);
    }

    [Fact]
    public void ContinuousSpeechBecomesContiguousPiecesOfAtMostTwentySeconds()
    {
        FileAudioChunks source = Recording(51.13, index => Syllables(index, 0.1f));
        int end = 0;
        int count = 0;
        foreach (FileAudioChunk chunk in All(source))
        {
            Assert.Equal(end, chunk.StartFrame);
            Assert.Equal(chunk.EndFrame - chunk.StartFrame, chunk.Samples.Length);
            Assert.True(chunk.Samples.Length <= 20 * 16_000);
            end = chunk.EndFrame;
            count++;
        }

        Assert.Equal((int)(51.13 * 16_000), end);
        // 51 s is ceil(51.13 / 16) = 4 nearly equal pieces.
        Assert.Equal(4, count);
    }

    [Fact]
    public void SilenceBetweenSentencesIsLeftOut()
    {
        FileAudioChunks source = Recording(
            70,
            index => index < 20 * 16_000 || index >= 50 * 16_000 ? Syllables(index, 0.1f) : 0);
        List<PieceRange> pieces = All(source).Select(chunk => R(chunk.StartFrame, chunk.EndFrame)).ToList();
        Assert.Equal(0, pieces[0].Start);
        Assert.Equal(70 * 16_000, pieces[^1].End);
        Assert.DoesNotContain(pieces, piece => piece.Overlaps(R(22 * 16_000, 48 * 16_000)));
        // A second of audio after the last word.
        Assert.Contains(pieces, piece => piece.Contains(20 * 16_000));
    }

    [Fact]
    public void SamplesInMemoryArePlannedLikeAFile()
    {
        float[] samples = Enumerable.Range(0, (int)(51.13 * 16_000)).Select(index => Syllables(index, 0.1f)).ToArray();
        FileAudioChunks fromFile = Recording(51.13, index => Syllables(index, 0.1f));
        var fromMemory = new FileAudioChunks(samples);
        while (fromFile.Next() is { } chunk)
        {
            FileAudioChunk? same = fromMemory.Next();
            Assert.Equal(chunk.StartFrame, same?.StartFrame);
            Assert.Equal(chunk.Samples, same?.Samples);
        }

        Assert.Null(fromMemory.Next());
    }

    /// <summary>
    /// A caller that stores the audio itself asks only where to cut. The answer has to be the
    /// cuts a recording is decoded by, or the two would transcribe one file differently.
    /// </summary>
    [Fact]
    public void ThePlannerCutsWhereAFileIsCut()
    {
        float Sample(int index) => index < 20 * 16_000 || index >= 50 * 16_000 ? Syllables(index, 0.1f) : 0;
        FileAudioChunks source = Recording(70, Sample);
        List<PieceRange> fromFile = All(source).Select(chunk => R(chunk.StartFrame, chunk.EndFrame)).ToList();

        var planner = new FilePiecePlanner();
        // In uneven blocks, as a reader hands them over.
        int position = 0;
        foreach (int size in new[] { 16_000, 1, 4_096, 70 * 16_000 })
        {
            int end = Math.Min(position + size, 70 * 16_000);
            planner.Push(Enumerable.Range(position, end - position).Select(Sample).ToArray());
            position = end;
        }

        Assert.Equal(fromFile, planner.Finish());
        Assert.True(fromFile.Count > 1);
    }

    [Fact]
    public void ThePlannerKeepsAShortFileWholeAndCutsNothingFromNothing()
    {
        var shortFile = new FilePiecePlanner();
        shortFile.Push(new float[29 * 16_000]);
        // Thirty seconds or less goes to the engine whole.
        Assert.Equal([R(0, 29 * 16_000)], shortFile.Finish());

        Assert.Empty(new FilePiecePlanner().Finish());

        var silent = new FilePiecePlanner();
        silent.Push(new float[90 * 16_000]);
        // A long silence has no pieces.
        Assert.Empty(silent.Finish());
    }

    [Fact]
    public void ShortFileStaysWholeAndLongSilenceProducesNoChunks()
    {
        FileAudioChunks first = Recording(29, _ => 0);
        Assert.Equal(29 * 16_000, first.Next()?.Samples.Length);
        Assert.Null(first.Next());
        FileAudioChunks quiet = Recording(90, _ => 0);
        Assert.Null(quiet.Next());
        Assert.Equal(90, quiet.Duration);
    }

    [Fact]
    public void JoinMovesWordsByTheirPieceAndKeepsRepeatsAndTheFinalNumber()
    {
        var joiner = new TimedTranscriptJoiner();
        static ASRResult.Word Word(string text, double start) =>
            new(text, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(start + 0.2));
        var duration = TimeSpan.FromSeconds(34);
        joiner.Append(
            new ASRResult(
                "Да да обсудим",
                [Word("Да", 1), Word("да", 2), Word("обсудим", 19.7)],
                TimeSpan.FromSeconds(20),
                TimeSpan.Zero),
            new FileAudioChunk([], 0, 20 * 16_000),
            duration);
        joiner.Append(
            new ASRResult(
                "обсудим число 2026.",
                [Word("обсудим", 0), Word("число", 1), Word("2026.", 13.9)],
                TimeSpan.FromSeconds(14),
                TimeSpan.Zero),
            new FileAudioChunk([], 20 * 16_000, 34 * 16_000),
            duration);
        Assert.Equal(["Да", "да", "обсудим", "обсудим", "число", "2026."], joiner.Words.Select(word => word.Text));
        Assert.Equal(
            new[] { 1, 2, 19.7, 20, 21, 33.9 }.Select(TimeSpan.FromSeconds),
            joiner.Words.Select(word => word.Start));
        Assert.Equal(TimeSpan.FromSeconds(34), joiner.Words[^1].End);
        Assert.Equal("Да да обсудим обсудим число 2026.", joiner.Text);
    }

    /// <summary>
    /// Port-only: a piece whose engine answered with text but no word timings still contributes
    /// its text. Upstream throws here; the sherpa-onnx adapter drops timestamps that are not sane
    /// seconds, so text without words is a legitimate answer.
    /// </summary>
    [Fact]
    public void APieceWithoutWordTimingsKeepsItsText()
    {
        var joiner = new TimedTranscriptJoiner();
        joiner.Append(
            new ASRResult(" first ", TimeSpan.FromSeconds(20), TimeSpan.Zero),
            new FileAudioChunk([], 0, 20 * 16_000),
            TimeSpan.FromSeconds(40));
        joiner.Append(
            new ASRResult("", TimeSpan.FromSeconds(20), TimeSpan.Zero),
            new FileAudioChunk([], 20 * 16_000, 40 * 16_000),
            TimeSpan.FromSeconds(40));
        Assert.Equal("first", joiner.Text);
        Assert.Empty(joiner.Words);
    }

    /// <summary>
    /// Noise whose loudness changes every 20 ms, as syllables do; a steady tone would be taken
    /// for the room's floor.
    /// </summary>
    internal static float Syllables(int index, float level)
    {
        unchecked
        {
            uint state = (uint)(index / 320) * 2_654_435_761 + 1;
            state = state * 1_664_525 + 1_013_904_223;
            float loudness = 0.4f + 1.2f * (float)(state >> 8) / (float)(1 << 24);
            uint noise = (uint)index * 747_796_405 + 2_891_336_453;
            noise ^= noise >> 13;
            noise *= 1_274_126_177;
            return level * loudness * ((float)(noise >> 8) / (float)(1 << 23) - 1);
        }
    }

    /// <summary>Write the recording and open it the way a file is transcribed: streamed, twice.</summary>
    private FileAudioChunks Recording(double seconds, Func<int, float> sample)
    {
        float[] samples = Enumerable.Range(0, (int)(seconds * 16_000)).Select(sample).ToArray();
        string path = WavFixture.WriteSamples(directory, samples, "chunks");
        return FileAudioChunks.Read(path);
    }
}
