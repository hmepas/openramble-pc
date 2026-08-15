using System.Buffers.Binary;

namespace OpenRamble.LinuxAudio.Tests;

/// <summary>
/// The framing, checked the only way it can be: by feeding a stream that behaves like a pipe.
/// </summary>
/// <remarks>
/// This is the one place in the Linux audio path where a defect is silent. A lost or doubled byte
/// does not throw and does not log - it shifts every later sample by a quarter of one and the
/// dictation comes back as noise, which looks like a bad microphone.
/// </remarks>
public sealed class RawSampleStreamTests
{
    private const int Chunk = 1_600;

    [Fact]
    public void AnEmptyStreamProducesNoFrames()
    {
        var frames = new List<float[]>();
        RawSampleStream.Pump(new MemoryStream(), Chunk, frames.Add);

        Assert.Empty(frames);
    }

    [Fact]
    public void SamplesComeOutInTheOrderTheyWentIn()
    {
        float[] written = Ramp(4_000);
        var read = new List<float>();

        RawSampleStream.Pump(new MemoryStream(Bytes(written)), Chunk, frame => read.AddRange(frame));

        Assert.Equal(written, read);
    }

    [Fact]
    public void ReadsThatStopInTheMiddleOfASampleAreRejoined()
    {
        float[] written = Ramp(1_000);
        var read = new List<float>();

        // Three bytes at a time: every read but the fourth ends inside a sample, which is what a
        // pipe does and what a naive "convert what this read returned" loop gets wrong.
        RawSampleStream.Pump(
            new StubbornStream(Bytes(written), maximumRead: 3),
            Chunk,
            frame => read.AddRange(frame));

        Assert.Equal(written, read);
    }

    [Fact]
    public void NoFrameIsLargerThanTheChunkAndNoneIsEmpty()
    {
        // Ten chunks in one buffer: the source can always answer a read in full, so the only
        // thing keeping the frames small is the loop.
        var frames = new List<float[]>();
        RawSampleStream.Pump(new MemoryStream(Bytes(Ramp(Chunk * 10))), Chunk, frames.Add);

        Assert.NotEmpty(frames);
        Assert.All(frames, frame => Assert.InRange(frame.Length, 1, Chunk));
        Assert.Equal(Chunk * 10, frames.Sum(frame => frame.Length));
    }

    [Fact]
    public void ATrailingPartialSampleIsNotInvented()
    {
        // The recorder was killed mid-sample: three bytes of the fourth arrived and the rest
        // never will. Reading them as a float would put one wrong sample at the end of the
        // dictation; there is nothing honest to do with them but drop them.
        byte[] bytes = [.. Bytes([1f, 2f, 3f]), 0x00, 0x00, 0x80];
        var read = new List<float>();

        RawSampleStream.Pump(new MemoryStream(bytes), Chunk, frame => read.AddRange(frame));

        Assert.Equal([1f, 2f, 3f], read);
    }

    [Fact]
    public void FramesAreTheCalleesToKeep()
    {
        // The sink queues them and writes them later; a reused buffer would hand every waiting
        // frame the contents of the last read.
        var frames = new List<float[]>();
        RawSampleStream.Pump(new StubbornStream(Bytes(Ramp(8)), maximumRead: 4), Chunk, frames.Add);

        Assert.True(frames.Count > 1);
        Assert.Equal(Ramp(8), frames.SelectMany(frame => frame));
    }

    private static float[] Ramp(int count) =>
        [.. Enumerable.Range(0, count).Select(index => index / (float)count - 0.5f)];

    private static byte[] Bytes(IEnumerable<float> samples)
    {
        float[] values = [.. samples];
        byte[] bytes = new byte[values.Length * 4];
        for (int index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * 4, 4), values[index]);
        }

        return bytes;
    }

    /// <summary>A stream that never answers a read in full - the awkward half of a pipe.</summary>
    private sealed class StubbornStream : Stream
    {
        private readonly byte[] content;
        private readonly int maximumRead;
        private int position;

        public StubbornStream(byte[] content, int maximumRead)
        {
            this.content = content;
            this.maximumRead = maximumRead;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => content.Length;

        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int taken = Math.Min(Math.Min(count, maximumRead), content.Length - position);
            Array.Copy(content, position, buffer, offset, taken);
            position += taken;
            return taken;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
