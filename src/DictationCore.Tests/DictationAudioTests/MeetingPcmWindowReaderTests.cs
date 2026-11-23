using DictationCore.Audio;

namespace DictationCore.Tests.DictationAudioTests;

public sealed class MeetingPcmWindowReaderTests : IDisposable
{
    private readonly string directory;

    public MeetingPcmWindowReaderTests()
    {
        directory = Path.Combine(Path.GetTempPath(), $"pcm-window-{Guid.NewGuid()}");
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

    [Fact]
    public void TestReadsOneChannelAtAnOffsetSampleExact()
    {
        var writer = new MeetingWriter(directory);
        writer.Open();
        // Left counts up, right counts down, so a wrong channel or a wrong offset is visible
        // in the values.
        writer.Append(
            [.. Enumerable.Range(0, 4_000).Select(index => index / 8_000f)],
            [.. Enumerable.Range(0, 4_000).Select(index => -index / 8_000f)]);
        writer.Finish();

        var reader = new MeetingPcmWindowReader(writer.AudioPath);
        float[] left = reader.Read(MeetingChannel.Microphone, startFrame: 1_000, frameCount: 10);
        float[] right = reader.Read(MeetingChannel.System, startFrame: 1_000, frameCount: 10);
        Assert.Equal(1_000.0 / 8_000, left[0], 0.0001);
        Assert.Equal(1_009.0 / 8_000, left[9], 0.0001);
        Assert.Equal(-1_000.0 / 8_000, right[0], 0.0001);
        Assert.Equal(-1_009.0 / 8_000, right[9], 0.0001);
    }

    [Fact]
    public void TestReadsWhileTheHeaderStillSaysNothingWasRecorded()
    {
        var writer = new MeetingWriter(directory);
        writer.Open();
        writer.Append([.. Enumerable.Repeat(0.5f, 3_200)], new float[3_200]);
        // Not finished: the header's sizes are still zero.
        var reader = new MeetingPcmWindowReader(writer.AudioPath);
        float[] samples = reader.Read(new MeetingSegmentRef(MeetingChannel.Microphone, StartFrame: 1_600, FrameCount: 1_600));
        Assert.Equal(1_600, samples.Length);
        Assert.Equal(0.5, samples[0], 0.001);
        writer.Finish();
    }

    [Fact]
    public void TestAWindowPastTheEndIsAShortReadNotSilence()
    {
        var writer = new MeetingWriter(directory);
        writer.Open();
        writer.Append([.. Enumerable.Repeat(0.5f, 100)], new float[100]);
        writer.Finish();
        var reader = new MeetingPcmWindowReader(writer.AudioPath);
        Assert.Throws<MeetingPcmWindowReader.Failure.ShortRead>(
            () => reader.Read(MeetingChannel.Microphone, startFrame: 50, frameCount: 100));
    }

    [Fact]
    public void TestAMissingFileIsAnError()
    {
        var reader = new MeetingPcmWindowReader(Path.Combine(directory, "nope.wav"));
        Assert.Throws<MeetingPcmWindowReader.Failure.CannotOpen>(
            () => reader.Read(MeetingChannel.Microphone, startFrame: 0, frameCount: 10));
    }
}
