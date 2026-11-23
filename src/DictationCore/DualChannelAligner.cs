namespace DictationCore;

/// <summary>
/// Two independently timed streams, made into one stereo timeline.
///
/// The output is strictly monotonic and append-only: frame N of the microphone
/// and frame N of the system side are the same instant, and nothing written is
/// ever rewritten. That is what lets the file be a WAV that grows on disk and
/// lets a transcript refer to a frame range and mean it.
///
/// Rules, each of which a test pins:
/// - A block that lands before the write cursor is truncated there and the
///   dropped frames are counted. Late audio does not move the past.
/// - A channel with nothing pending for a stretch is filled with silence on
///   that channel only, and the gap is counted — the other channel is not
///   held up. <b>One dead channel never blocks the file.</b>
/// - Emission lags the newest frame seen by <see cref="JitterFrames"/>, so a slightly
///   late block from the other channel still lands in place.
/// - A channel that is not active (a voice note has no system side) is
///   written as silence without being counted as a gap: it is not missing,
///   it was never asked for.
///
/// Pure. The clock that turns host time into frames is <see cref="ChannelClock"/>.
/// All positions count 16 kHz frames.
/// </summary>
public sealed class DualChannelAligner
{
    public sealed record Emission(long StartFrame, float[] Microphone, float[] System)
    {
        public int FrameCount => Microphone.Length;
    }

    /// <summary>
    /// 320 ms at 16 kHz: 41 kB of buffer, and more than the scheduling
    /// jitter of either source on a busy machine.
    /// </summary>
    public const long DefaultJitterFrames = 5_120;

    public long JitterFrames { get; }
    public IReadOnlySet<MeetingChannel> ActiveChannels { get; }

    private readonly record struct Block(long Start, float[] Samples)
    {
        public long End => Start + Samples.Length;
    }

    private readonly Dictionary<MeetingChannel, List<Block>> _pending = [];
    private readonly Dictionary<MeetingChannel, long> _gapFrames = [];
    private readonly Dictionary<MeetingChannel, long> _droppedLateFrames = [];
    private long _newestFrameSeen;

    /// <summary>The write cursor: the next frame to emit.</summary>
    public long EmittedFrames { get; private set; }

    /// <summary>Frames filled with silence because an active channel had nothing.</summary>
    public IReadOnlyDictionary<MeetingChannel, long> GapFrames => _gapFrames;

    /// <summary>Frames thrown away because they arrived behind the cursor.</summary>
    public IReadOnlyDictionary<MeetingChannel, long> DroppedLateFrames => _droppedLateFrames;

    public DualChannelAligner(IReadOnlySet<MeetingChannel>? activeChannels = null, long jitterFrames = DefaultJitterFrames)
    {
        ActiveChannels = activeChannels ?? new HashSet<MeetingChannel>(Enum.GetValues<MeetingChannel>());
        JitterFrames = jitterFrames;
    }

    /// <summary>
    /// Start a new recording-time segment at an existing file frame. Pauses
    /// do not create a hole in the WAV, so the next aligner must continue at
    /// the writer's current frame rather than starting from zero again.
    /// </summary>
    public void Reset(long toFrame)
    {
        _pending.Clear();
        EmittedFrames = Math.Max(0, toFrame);
        _newestFrameSeen = Math.Max(0, toFrame);
        _gapFrames.Clear();
        _droppedLateFrames.Clear();
    }

    public void Ingest(MeetingChannel channel, long startFrame, float[] samples)
    {
        var block = new Block(startFrame, samples);
        if (block.Start < EmittedFrames)
        {
            int late = (int)Math.Min(block.Samples.Length, EmittedFrames - block.Start);
            _droppedLateFrames[channel] = _droppedLateFrames.GetValueOrDefault(channel) + late;
            block = new Block(EmittedFrames, block.Samples[late..]);
        }
        if (block.Samples.Length == 0)
        {
            return;
        }

        if (!_pending.TryGetValue(channel, out List<Block>? blocks))
        {
            blocks = [];
            _pending[channel] = blocks;
        }
        // Sources deliver in order, so this is an append; the search keeps it
        // correct if one ever does not.
        int index = blocks.FindIndex(b => b.Start > block.Start);
        blocks.Insert(index < 0 ? blocks.Count : index, block);
        _newestFrameSeen = Math.Max(_newestFrameSeen, block.End);
    }

    /// <summary>Everything that is old enough to be certain about.</summary>
    public Emission? Drain() => Emit(_newestFrameSeen - JitterFrames);

    /// <summary>Everything, jitter or not — the recording is ending.</summary>
    public Emission? Flush() => Emit(_newestFrameSeen);

    private Emission? Emit(long target)
    {
        if (target <= EmittedFrames)
        {
            return null;
        }
        int count = (int)(target - EmittedFrames);
        float[] microphone = Take(MeetingChannel.Microphone, EmittedFrames, count);
        float[] system = Take(MeetingChannel.System, EmittedFrames, count);
        var emission = new Emission(EmittedFrames, microphone, system);
        EmittedFrames = target;
        return emission;
    }

    private float[] Take(MeetingChannel channel, long @base, int count)
    {
        var output = new float[count];
        if (!ActiveChannels.Contains(channel))
        {
            return output;
        }

        long covered = 0;
        var remaining = new List<Block>();
        foreach (Block block in _pending.GetValueOrDefault(channel) ?? [])
        {
            if (block.End <= @base)
            {
                continue;
            }
            if (block.Start >= @base + count)
            {
                remaining.Add(block);
                continue;
            }
            long from = Math.Max(block.Start, @base);
            long to = Math.Min(block.End, @base + count);
            int length = (int)(to - from);
            int sourceOffset = (int)(from - block.Start);
            int destinationOffset = (int)(from - @base);
            Array.Copy(block.Samples, sourceOffset, output, destinationOffset, length);
            covered += length;
            if (block.End > @base + count)
            {
                int keepFrom = (int)(@base + count - block.Start);
                remaining.Add(new Block(@base + count, block.Samples[keepFrom..]));
            }
        }
        _pending[channel] = remaining;
        _gapFrames[channel] = _gapFrames.GetValueOrDefault(channel) + Math.Max(0, count - covered);
        return output;
    }
}
