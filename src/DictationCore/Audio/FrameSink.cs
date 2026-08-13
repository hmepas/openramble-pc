using System.Threading.Channels;

namespace DictationCore.Audio;

/// <summary>
/// The queue of frames between the audio stream and recording to disk.
///
/// The sound engine calls its callback on its own real-time thread: you can neither
/// wait there nor write to a file. The frame needs to be handed off somewhere within
/// nanoseconds - and at the same time lose neither the order nor the last frames.
///
/// Both requirements came from real defects. Each frame used to go to the disk as a
/// separate task - the order of their execution is not specified by anything, and the
/// recording could mix with itself. And the stop closed the file without waiting for
/// the queue - the last word disappeared, exactly the one on which the person lets go
/// of the key.
///
/// The type is separated from the microphone intentionally: this way it can be checked
/// without sound hardware, which is not present in the test environment.
/// </summary>
/// <remarks>
/// The Swift original is an <c>actor</c> holding an <c>AsyncStream</c> continuation; here
/// the queue is an unbounded <see cref="Channel"/> and the small amount of state around it
/// is guarded by a lock. The channel itself is thread-safe, which is what the audio
/// thread needs from <see cref="Start"/>'s return value.
/// </remarks>
internal sealed class FrameSink
{
    private readonly Lock gate = new();
    private Channel<float[]>? frames;
    private Task? drain;
    private CancellationTokenSource? cancellation;

    /// <summary>
    /// Start receiving frames.
    ///
    /// Returns a function for the audio stream: it just puts the frame in the queue and
    /// returns immediately. One task in a row works through the queue - this is the
    /// guarantee of order.
    /// </summary>
    public Action<float[]> Start(Func<float[], Task> consume)
    {
        var channel = Channel.CreateUnbounded<float[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });
        // Deliberately not disposed: the drain keeps holding this token, and cancelling
        // does not wait for it to notice.
        var cancellationSource = new CancellationTokenSource();

        lock (gate)
        {
            frames = channel;
            cancellation = cancellationSource;
            drain = Task.Run(() => Drain(channel.Reader, consume, cancellationSource.Token));
        }

        return samples => channel.Writer.TryWrite(samples);
    }

    /// <summary>Close the queue and wait until the last frame is on disk.</summary>
    public async Task Finish()
    {
        Task? pending;
        lock (gate)
        {
            frames?.Writer.TryComplete();
            frames = null;
            pending = drain;
            drain = null;
            cancellation = null;
        }

        if (pending is not null)
        {
            await pending.ConfigureAwait(false);
        }
    }

    /// <summary>Leave the queue without waiting for the recording - the dictation was cancelled.</summary>
    public void Cancel()
    {
        CancellationTokenSource? cancellationSource;
        lock (gate)
        {
            frames?.Writer.TryComplete();
            frames = null;
            cancellationSource = cancellation;
            cancellation = null;
            drain = null;
        }

        cancellationSource?.Cancel();
    }

    private static async Task Drain(
        ChannelReader<float[]> reader,
        Func<float[], Task> consume,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (float[] samples in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await consume(samples).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the whole point of Cancel(): what is left in the queue
            // belongs to a dictation nobody asked to keep.
        }
    }
}
