namespace OpenRamble.LinuxInput.Tests;

/// <summary>
/// The bytes the kernel writes, read back.
/// </summary>
/// <remarks>
/// The one place in the hotkey path where a defect is silent: a wrong offset produces a plausible
/// keycode for a key nobody pressed, and the only symptom is a hotkey that answers to something
/// else. So the layout is asserted literally, field by field, against bytes the test wrote itself.
/// </remarks>
public sealed class EvdevEventStreamTests
{
    [Fact]
    public void AnEventIsTheKernelStructWithoutItsTimestamp()
    {
        // 24 bytes on a 64-bit machine: two 8-byte timeval fields, then u16 type, u16 code,
        // s32 value. Nothing here is allowed to drift from linux/input.h.
        Assert.Equal(nint.Size == 8 ? 24 : 16, EvdevEventStream.EventSize);
    }

    [Fact]
    public void TheFieldsComeOutWhereTheyWentIn()
    {
        byte[] raw = EvdevBytes.Key(Keys.RightCtrl, 1);

        EvdevEvent read = EvdevEventStream.Parse(raw);

        Assert.Equal(EvdevEvent.TypeKey, read.Type);
        Assert.Equal(Keys.RightCtrl, read.Code);
        Assert.True(read.IsPress);
        Assert.False(read.IsRelease);
        Assert.False(read.IsRepeat);
    }

    [Fact]
    public void TheThreeKeyStatesAreTold()
    {
        Assert.True(EvdevEventStream.Parse(EvdevBytes.Key(Keys.C, 0)).IsRelease);
        Assert.True(EvdevEventStream.Parse(EvdevBytes.Key(Keys.C, 1)).IsPress);
        Assert.True(EvdevEventStream.Parse(EvdevBytes.Key(Keys.C, 2)).IsRepeat);
    }

    [Fact]
    public void EveryEventInAStreamIsDeliveredInOrder()
    {
        var stream = new MemoryStream(EvdevBytes.Concat(
            EvdevBytes.Key(Keys.RightCtrl, 1),
            EvdevBytes.Event(EvdevBytes.SynchronizationType, 0, 0),
            EvdevBytes.Key(Keys.RightCtrl, 0)));
        var seen = new List<(ushort Type, ushort Code, int Value)>();

        EvdevEventStream.Pump(stream, e => seen.Add((e.Type, e.Code, e.Value)));

        // The synchronisation event is passed on rather than filtered: deciding what is interesting
        // belongs to the monitor, and a reader that dropped events would be a reader with an
        // opinion about which keys matter.
        Assert.Equal(
            [
                (EvdevEvent.TypeKey, (ushort)Keys.RightCtrl, 1),
                (EvdevBytes.SynchronizationType, (ushort)0, 0),
                (EvdevEvent.TypeKey, (ushort)Keys.RightCtrl, 0),
            ],
            seen);
    }

    [Fact]
    public void AnEventSplitAcrossTwoReadsIsStillOneEvent()
    {
        // A character device never does this; a stream can, and the carry is four lines against a
        // failure that would shift every later event by a field.
        byte[] two = EvdevBytes.Concat(
            EvdevBytes.Key(Keys.CapsLock, 1),
            EvdevBytes.Key(Keys.CapsLock, 0));
        var stream = new DribblingStream(two, chunk: 7);
        var seen = new List<EvdevEvent>();

        EvdevEventStream.Pump(stream, seen.Add);

        Assert.Equal(2, seen.Count);
        Assert.Equal(Keys.CapsLock, seen[0].Code);
        Assert.True(seen[0].IsPress);
        Assert.True(seen[1].IsRelease);
    }

    [Fact]
    public void ATrailingFragmentIsNotAnEvent()
    {
        // The device was unplugged mid-write, or the stream ended where it liked. Inventing an
        // event out of half a structure would be inventing a keystroke.
        byte[] partial = EvdevBytes.Key(Keys.RightCtrl, 1)[..(EvdevEventStream.EventSize - 3)];
        var seen = new List<EvdevEvent>();

        EvdevEventStream.Pump(new MemoryStream(partial), seen.Add);

        Assert.Empty(seen);
    }

    [Fact]
    public void ReadingStopsWhenTheCallerHasStopped()
    {
        // What lets an abandoned reader leave when its device finally produces something, instead
        // of holding the descriptor for the life of the process.
        var stream = new MemoryStream(EvdevBytes.Key(Keys.RightCtrl, 1));
        var seen = new List<EvdevEvent>();

        EvdevEventStream.Pump(stream, seen.Add, keepReading: () => false);

        Assert.Empty(seen);
    }

    /// <summary>A stream that hands over a few bytes at a time, the way a pipe does.</summary>
    private sealed class DribblingStream : Stream
    {
        private readonly byte[] bytes;
        private readonly int chunk;
        private int position;

        public DribblingStream(byte[] bytes, int chunk)
        {
            this.bytes = bytes;
            this.chunk = chunk;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => bytes.Length;

        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int taken = Math.Min(Math.Min(chunk, count), bytes.Length - position);
            Array.Copy(bytes, position, buffer, offset, taken);
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
