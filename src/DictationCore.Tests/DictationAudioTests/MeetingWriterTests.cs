using System.Buffers.Binary;
using DictationCore.Audio;

namespace DictationCore.Tests.DictationAudioTests;

/// <summary>One stereo file, both sides in step, playable after a crash.</summary>
public sealed class MeetingWriterTests : IDisposable
{
    private readonly string directory;

    public MeetingWriterTests()
    {
        directory = Path.Combine(Path.GetTempPath(), $"meeting-writer-{Guid.NewGuid()}");
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

    private static float[] Constant(float value, int count) => [.. Enumerable.Repeat(value, count)];

    /// <summary>
    /// The Swift test reads the file back with AVAudioFile; here the header is validated by
    /// <see cref="WavFile"/> and the interleaved samples are split by hand.
    /// </summary>
    private static (float[] Left, float[] Right, int SampleRate) ReadChannels(string path)
    {
        WavFile wav = WavFile.Read(path);
        Assert.Equal(2, wav.Channels);
        byte[] bytes = File.ReadAllBytes(path);
        int frames = (int)wav.Length;
        float[] left = new float[frames];
        float[] right = new float[frames];
        for (int frame = 0; frame < frames; frame++)
        {
            left[frame] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44 + (frame * 4), 2)) / 32_768f;
            right[frame] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(46 + (frame * 4), 2)) / 32_768f;
        }

        return (left, right, wav.SampleRate);
    }

    [Fact]
    public void TestTheMicrophoneIsLeftAndTheSystemIsRightAndTheyStayInStep()
    {
        var writer = new MeetingWriter(directory);
        writer.Open();
        writer.Append(Constant(0.5f, 1_600), Constant(-0.5f, 1_600));
        writer.Append(Constant(0.25f, 800), Constant(0, 800));
        writer.Finish();

        var audio = ReadChannels(writer.AudioPath);
        Assert.Equal(16_000, audio.SampleRate);
        Assert.Equal(2_400, audio.Left.Length);
        Assert.Equal(0.5, audio.Left[0], 0.001);
        Assert.Equal(-0.5, audio.Right[0], 0.001);
        Assert.Equal(0.25, audio.Left[2_000], 0.001);
        Assert.Equal(0, audio.Right[2_000], 0.001);
        Assert.Equal(2_400, writer.FrameCount);
        Assert.Equal(0.15, writer.Duration.TotalSeconds, 0.0001);
    }

    [Fact]
    public void TestPeaksAreWrittenOneBucketPerHundredMilliseconds()
    {
        var writer = new MeetingWriter(directory);
        writer.Open();
        // 2.5 buckets: two full and a partial one that Finish() must flush.
        writer.Append(Constant(0.1f, 1_600), Constant(0.9f, 1_600));
        writer.Append(Constant(0.3f, 1_600), Constant(0.0f, 1_600));
        writer.Append(Constant(0.7f, 800), Constant(0.2f, 800));
        writer.Finish();

        PeakFile.Contents peaks = PeakFile.Read(writer.PeaksPath);
        Assert.Equal(3, peaks.Channels[0].Length);
        Assert.Equal(26.0 / 255, peaks.Channels[0][0], 0.001);
        Assert.Equal(230.0 / 255, peaks.Channels[1][0], 0.001);
        // The partial bucket was flushed.
        Assert.Equal(179.0 / 255, peaks.Channels[0][2], 0.001);
    }

    [Fact]
    public void TestMismatchedChannelLengthsAreRefusedBeforeAnythingIsWritten()
    {
        var writer = new MeetingWriter(directory);
        writer.Open();
        Assert.Throws<MeetingWriter.Failure.MismatchedChannels>(() => writer.Append(Constant(0, 100), Constant(0, 99)));
        Assert.Equal(0, writer.FrameCount);
        // A caller mistake is not a disk failure.
        Assert.False(writer.HasWriteFailure);
        writer.Finish();
    }

    [Fact]
    public void TestAFileAbandonedMidRecordingIsUnplayableUntilItsHeaderIsRepaired()
    {
        var writer = new MeetingWriter(directory);
        writer.Open();
        writer.Append(Constant(0.5f, 3_200), Constant(-0.5f, 3_200));
        writer.Abandon();

        // The header still says nothing was recorded, so a reader believes the take is empty.
        byte[] unrepaired = File.ReadAllBytes(writer.AudioPath);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(unrepaired.AsSpan(40, 4)));

        long frames = MeetingWriter.RepairHeader(writer.AudioPath);
        Assert.Equal(3_200, frames);
        Assert.Equal(3_200, MeetingWriter.FrameCountAt(writer.AudioPath));
        var audio = ReadChannels(writer.AudioPath);
        Assert.Equal(3_200, audio.Left.Length);
        Assert.Equal(-0.5, audio.Right[3_199], 0.001);
    }

    [Fact]
    public void TestRepairRefusesAFileThatIsNotAWAV()
    {
        string path = Path.Combine(directory, "audio.wav");
        File.WriteAllBytes(path, [.. Enumerable.Repeat((byte)0x41, 100)]);
        Assert.ThrowsAny<MeetingWriter.Failure>(() => MeetingWriter.RepairHeader(path));
    }

    [Fact]
    public void TestAppendingToAnUnopenedWriterFails()
    {
        var writer = new MeetingWriter(directory);
        Assert.ThrowsAny<MeetingWriter.Failure>(() => writer.Append(Constant(0, 10), Constant(0, 10)));
    }

    [Fact]
    public void TestAFailedPeaksOpenDoesNotLeaveTheWavOpen()
    {
        Directory.CreateDirectory(Path.Combine(directory, MeetingWriter.PeaksFileName));
        var writer = new MeetingWriter(directory);

        Assert.Throws<MeetingWriter.Failure.WriteFailed>(writer.Open);

        Assert.False(File.Exists(writer.AudioPath));
    }

    [Fact]
    public void TestAFailedPeaksCloseStillSealsAndClosesTheWav()
    {
        var writer = new MeetingWriter(directory);
        writer.Open();
        writer.Append(Constant(0.5f, 800), Constant(-0.5f, 800));
        // The peak file loses its descriptor, so closing it fails.
        var peaks = (PeakFile)typeof(MeetingWriter)
            .GetField("peaks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(writer)!;
        peaks.Abandon();

        Assert.Throws<MeetingWriter.Failure.WriteFailed>(writer.Finish);

        byte[] bytes = File.ReadAllBytes(writer.AudioPath);
        Assert.Equal(800u * 4, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40, 4)));
        using var exclusive = new FileStream(writer.AudioPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
}
