namespace DictationCore.Audio;

/// <summary>
/// Hands finished pieces of a running recording to the engine, so the person is not waiting at
/// the end for work that could have happened while they spoke.
///
/// Fed from the same place as the recording itself - inside the ordered frame drain - and it
/// must be equally cheap there. <see cref="Submit"/> computes one peak and hands an
/// already-owned array to the sink; no copy of the take, no file, nothing held across the call.
///
/// The cut rule itself lives in <see cref="SpeechSegmenter"/>, which is pure and tested without
/// a microphone. This type is only the plumbing around it.
/// </summary>
/// <remarks>
/// The Swift original owns a serial queue of its own. Here the caller is already the one ordered
/// consumer of the <see cref="FrameSink"/> - one drain task per recording, frames in order - so
/// the segmenter runs inline on that drain and the barrier the Swift <c>queue.sync</c> provided
/// is the drain having finished. Ingesting a frame is one pass over it for the peak; the only
/// thing that could cost anything is the sink, and the controller's sink returns at once.
/// </remarks>
internal sealed class RecordingSegmentAttachment
{
    private readonly Lock gate = new();
    private readonly SpeechSegmenter segmenter;
    private Action<float[]>? sink;

    /// <summary>Audio not yet shipped in a segment.</summary>
    private readonly List<float> pending = [];

    /// <summary>
    /// How much of the recording has left in completed segments.
    ///
    /// The controller needs exactly this number at the stop: everything past it is the tail
    /// nobody has recognized yet.
    /// </summary>
    private int consumed;
    private bool closed;

    public RecordingSegmentAttachment(SpeechSegmenter? segmenter = null)
    {
        this.segmenter = segmenter ?? new SpeechSegmenter();
    }

    /// <summary>
    /// Where completed segments go. Absent means "do not segment at all", which is the behaviour
    /// every existing caller gets for free.
    /// </summary>
    public void SetSink(Action<float[]>? sink)
    {
        lock (gate) { this.sink = sink; }
    }

    /// <summary>
    /// Called from the ordered drain, one frame at a time.
    ///
    /// The array is retained, not copied: it is already owned by the drain's turn and nothing
    /// mutates it afterwards.
    /// </summary>
    public void Submit(float[] samples)
    {
        Action<float[]>? segment;
        lock (gate)
        {
            if (sink is null || samples.Length == 0 || closed) { return; }

            float peak = 0;
            foreach (float value in samples)
            {
                peak = Math.Max(peak, Math.Abs(value));
            }

            pending.AddRange(samples);
            int? cut = segmenter.Observe(peak, samples.Length);
            // A cut can only be offered for audio already in `pending`, because the segmenter
            // counts exactly the frames handed to it.
            if (cut is not { } offset || offset <= 0 || offset > pending.Count) { return; }

            float[] piece = pending.GetRange(0, offset).ToArray();
            pending.RemoveRange(0, offset);
            consumed += offset;
            segment = sink;
            segment(piece);
        }
    }

    /// <summary>
    /// Everything submitted so far has been processed; how much left in segments.
    ///
    /// Closes the attachment: a frame that arrives after the take is over is ignored rather than
    /// appended behind the tail the controller already has. The stop calls this after the drain
    /// has finished, so the tail it hands to the engine begins exactly where the last segment
    /// ended, with no frame counted twice and none dropped between them.
    /// </summary>
    public int DrainConsumedSampleCount()
    {
        lock (gate)
        {
            closed = true;
            return consumed;
        }
    }
}
