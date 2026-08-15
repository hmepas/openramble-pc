using System.Buffers.Binary;

namespace OpenRamble.LinuxAudio;

/// <summary>
/// Bytes from a pipe into frames of samples.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of what replaces WASAPI's callback: a pipe hands over whatever the kernel had
/// when the read returned, which is not a whole number of samples, not a fixed size, and not
/// aligned to anything. Two things must survive that, and both have cost words elsewhere in this
/// port: the sample sequence has to come out exactly as it went in - a lost or doubled byte
/// shifts every later sample by a quarter of one and turns speech into noise - and a frame must
/// never be larger than <c>CaptureDrainPolicy.ChunkSamples</c>, which is what everything
/// downstream sizes itself against.
/// </para>
/// <para>
/// Separate from the capture, and pure, so that both can be checked by feeding a stream: this is
/// the one piece of the Linux audio path where a defect is silent - it produces a recording that
/// plays as noise rather than an error anybody logs.
/// </para>
/// </remarks>
internal static class RawSampleStream
{
    /// <summary>Float32, which is what the recorder is asked for.</summary>
    private const int BytesPerSample = 4;

    /// <summary>
    /// Read until <paramref name="source"/> ends, handing over every whole sample it produced.
    /// </summary>
    /// <param name="source">The recorder's stdout: little-endian Float32 and nothing else.</param>
    /// <param name="chunkSamples">The largest frame that may be delivered.</param>
    /// <param name="deliver">
    /// Called on this thread, in order, never with an empty frame. Each frame is the callee's to
    /// keep - it is a fresh array, because the sink's queue outlives this loop's buffer.
    /// </param>
    /// <remarks>
    /// Returns on end of stream. Whatever the reader throws - a broken pipe, a disposed process -
    /// is the caller's to interpret: mid-dictation it is the recorder dying, and during a stop it
    /// is the stop.
    /// </remarks>
    public static void Pump(Stream source, int chunkSamples, Action<float[]> deliver)
    {
        byte[] buffer = new byte[chunkSamples * BytesPerSample];

        // Bytes of a sample that was split across two reads. One to three of them, and they are
        // the reason this loop cannot simply convert what each read returned.
        int carried = 0;

        while (true)
        {
            int read = source.Read(buffer, carried, buffer.Length - carried);
            if (read <= 0)
            {
                // End of stream. Anything still carried is a fraction of a sample the recorder
                // never finished writing; there is nothing honest to do with it.
                return;
            }

            int available = carried + read;
            int whole = available / BytesPerSample;
            if (whole > 0)
            {
                var frame = new float[whole];
                for (int index = 0; index < whole; index++)
                {
                    frame[index] = BinaryPrimitives.ReadSingleLittleEndian(
                        buffer.AsSpan(index * BytesPerSample, BytesPerSample));
                }

                deliver(frame);
            }

            carried = available - (whole * BytesPerSample);
            if (carried > 0)
            {
                buffer.AsSpan(whole * BytesPerSample, carried).CopyTo(buffer);
            }
        }
    }
}
