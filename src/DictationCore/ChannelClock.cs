namespace DictationCore;

/// <summary>
/// Where a block of audio belongs on the recording's timeline.
///
/// Two devices deliver audio with two clocks, and neither is the wall clock.
/// Counting samples is exact within one device and drifts against the other;
/// host time is shared by both and jitters by a few milliseconds per block
/// because converters buffer internally. So this uses both: the host time is
/// the <i>coarse</i> anchor, the running sample count is the <i>fine</i> position, and
/// the count wins as long as it stays within <see cref="ResyncThresholdFrames"/> of the
/// anchor. Past that the block is re-anchored to host time and the skew is
/// reported, so drift becomes an occasional bounded seam rather than an error
/// that grows for ninety minutes.
///
/// Pure: frames in, a placement out. The conversion from host time (Stopwatch
/// ticks on this port) to a frame index happens outside, where the recording's
/// start instant lives. All positions count 16 kHz frames.
/// </summary>
public sealed class ChannelClock
{
    public abstract record Placement
    {
        private Placement() { }

        public abstract long StartFrame { get; }

        /// <summary>Butted against the previous block.</summary>
        public sealed record Contiguous(long StartFrame) : Placement
        {
            public override long StartFrame { get; } = StartFrame;
        }

        /// <summary>
        /// Moved to where the host clock says it is; <c>SkewFrames</c> is how far
        /// the sample count had wandered (positive: the device ran slow).
        /// </summary>
        public sealed record Resynchronized(long StartFrame, long SkewFrames) : Placement
        {
            public override long StartFrame { get; } = StartFrame;
        }
    }

    /// <summary>
    /// 50 ms at 16 kHz. Below this, converter jitter looks like drift and
    /// every block would be a seam; above it, a resync is a gap a person
    /// could hear. Consumer audio clocks sit near 100 ppm, which reaches this
    /// threshold about every eight minutes.
    /// </summary>
    public const long DefaultResyncThresholdFrames = 800;

    public long ResyncThresholdFrames { get; }
    private readonly long _baseFrame;
    private long? _expectedNext;

    public ChannelClock(long resyncThresholdFrames = DefaultResyncThresholdFrames, long startFrame = 0)
    {
        ResyncThresholdFrames = resyncThresholdFrames;
        _baseFrame = Math.Max(0, startFrame);
    }

    /// <summary>
    /// Place a block. <paramref name="hostFrame"/> is where the host clock says it starts, or
    /// <c>null</c> when the source had no valid timestamp.
    /// </summary>
    public Placement Place(long? hostFrame, int sampleCount)
    {
        Placement placement;
        switch (hostFrame, _expectedNext)
        {
            case (null, null):
                placement = new Placement.Contiguous(_baseFrame);
                break;
            case (null, long expected):
                placement = new Placement.Contiguous(expected);
                break;
            case (long host, null):
                placement = new Placement.Contiguous(Math.Max(_baseFrame, host));
                break;
            case (long host, long expected):
                long skew = host - expected;
                placement = Math.Abs(skew) > ResyncThresholdFrames
                    ? new Placement.Resynchronized(Math.Max(0, host), skew)
                    : new Placement.Contiguous(expected);
                break;
        }
        _expectedNext = placement.StartFrame + sampleCount;
        return placement;
    }

    /// <summary>
    /// Forget the running position — after a pause, the next block starts a
    /// new run against a re-anchored clock.
    /// </summary>
    public void Reset() => _expectedNext = null;
}
