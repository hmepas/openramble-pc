namespace DictationCore;

/// <summary>
/// Where a long recording may be cut for the engine - <see cref="SpeechSegmenter"/> with a
/// ceiling.
///
/// The segmenter's one rule stands: a cut lands inside silence, never through speech. For a
/// dictation that is the whole story; a person who does not pause simply produces one long take.
/// A meeting cannot afford that ending. Decode cost on this model is quadratic in segment length -
/// measured, 60 s costs 1.53 s and 140 s costs 5.64 s - and the segment is what every dictation
/// would have to wait behind. So past <see cref="Parameters.HardCap"/> a cut is forced anyway, at
/// the quietest frame in the last second, which is the least bad place inside continuous speech:
/// between words far more often than inside one.
///
/// Positions are absolute frames in the file, so what comes out is a
/// <see cref="MeetingSegmentRef"/> that reads straight from the WAV. Pure.
/// </summary>
/// <remarks>
/// The Swift original is a mutable <c>struct</c> observed in place; a small class keeps that
/// meaning, as with <see cref="SpeechSegmenter"/>. Every count is in frames (samples) of mono
/// audio at the given sample rate.
/// </remarks>
public sealed class MeetingSegmentPolicy
{
    public sealed record Parameters
    {
        public Parameters(
            SpeechSegmenter.Parameters? speech = null,
            TimeSpan? hardCap = null,
            TimeSpan? forcedCutWindow = null,
            bool preserveQuietAudio = false)
        {
            Speech = speech ?? new SpeechSegmenter.Parameters();
            HardCap = hardCap ?? TimeSpan.FromSeconds(20);
            ForcedCutWindow = forcedCutWindow ?? TimeSpan.FromSeconds(1);
            PreserveQuietAudio = preserveQuietAudio;
        }

        public SpeechSegmenter.Parameters Speech { get; init; }

        /// <summary>The longest segment ever handed to the engine.</summary>
        public TimeSpan HardCap { get; init; }

        /// <summary>How far back to look for the quietest frame when forcing a cut.</summary>
        public TimeSpan ForcedCutWindow { get; init; }

        /// <summary>
        /// Imported files can be much quieter than a live microphone. Use the same pause/cut
        /// policy without dropping sub-threshold audio.
        /// </summary>
        public bool PreserveQuietAudio { get; init; }

        /// <summary>
        /// Shorter segments than dictation's, deliberately. Text appears sooner, and the worst
        /// wait a dictation can inherit from a decode already running - 20 s at RTF 0.11 - stays
        /// near two seconds.
        /// </summary>
        public static Parameters Meeting { get; } = new(
            speech: new SpeechSegmenter.Parameters(minimumSegment: TimeSpan.FromSeconds(10)),
            hardCap: TimeSpan.FromSeconds(20),
            forcedCutWindow: TimeSpan.FromSeconds(1));

        /// <summary>
        /// A live recording's default for each channel: <see cref="Meeting"/>, with a lower speech
        /// floor for the other side. Tapped playback can be far quieter than a microphone, and a
        /// voice that never peaks at the floor is never decoded: held-out English read speech fed
        /// in as the other side lost over half its audio at 0.02. The microphone keeps 0.02,
        /// because there a lower floor let sparse leakage from the speakers past the echo gate and
        /// into You.
        /// </summary>
        public static Parameters Capture(MeetingChannel channel) =>
            channel == MeetingChannel.System
                ? Meeting with { Speech = Meeting.Speech with { SpeechPeak = 0.005f } }
                : Meeting;
    }

    private readonly record struct Frame(float Peak, int Count);

    /// <summary>
    /// How much silence a segment may begin with. Enough to spare the decode a hard edge against
    /// the first word; not an hour of the other side saying nothing, which would otherwise be the
    /// first thing decoded when they finally spoke.
    /// </summary>
    internal static readonly TimeSpan LeadingSilence = TimeSpan.FromSeconds(1);

    private readonly int sampleRate;
    private readonly SpeechSegmenter segmenter;
    private int segmentStart;
    private int end;
    private bool heardSpeech;
    private readonly List<Frame> recent = [];
    private int recentFrames;

    public MeetingSegmentPolicy(
        MeetingChannel channel,
        Parameters? parameters = null,
        int sampleRate = 16_000,
        int startFrame = 0)
    {
        Channel = channel;
        Configuration = parameters ?? Parameters.Meeting;
        this.sampleRate = sampleRate;
        segmenter = new SpeechSegmenter(Configuration.Speech, sampleRate);
        segmentStart = startFrame;
        end = startFrame;
    }

    public MeetingChannel Channel { get; }

    /// <summary>The knobs this policy runs with.</summary>
    public Parameters Configuration { get; }

    private int Frames(TimeSpan duration) => (int)(duration.TotalSeconds * sampleRate);

    /// <summary>Audio waiting since the last cut.</summary>
    public int PendingFrames => end - segmentStart;

    /// <summary>
    /// Feed one frame of written audio. Returns the segment it completed, if this frame completed
    /// one.
    /// </summary>
    public MeetingSegmentRef? Observe(float peak, int count)
    {
        end += count;
        if (peak >= Configuration.Speech.SpeechPeak) { heardSpeech = true; }
        Remember(new Frame(peak, count));

        // Silence before the first word is not part of any segment. Let the start slide forward
        // so a quiet channel never accumulates.
        if (!heardSpeech && !Configuration.PreserveQuietAudio && PendingFrames > Frames(LeadingSilence))
        {
            segmentStart = end - Frames(LeadingSilence);
            segmenter.Reset();
            // The segmenter's cut offset counts from segmentStart, so it must count the quiet
            // second kept above. Started from zero, its next pause cut landed a second early,
            // inside the last words, and this slide then dropped them with the silence after.
            _ = segmenter.Observe(0, Frames(LeadingSilence));
            return null;
        }

        if (segmenter.Observe(peak, count) is { } cut)
        {
            var segment = new MeetingSegmentRef(Channel, segmentStart, cut);
            segmentStart += cut;
            // What remains is the second half of the pause that earned the cut: silence, and not
            // yet a segment.
            heardSpeech = false;
            recent.Clear();
            recentFrames = 0;
            return segment;
        }

        if (!(heardSpeech || Configuration.PreserveQuietAudio)
            || PendingFrames < Frames(Configuration.HardCap)
            || recent.Count == 0)
        {
            return null;
        }
        return ForceCut();
    }

    /// <summary>
    /// The tail, at the end of a recording or before a pause - if anyone said anything in it.
    /// Silence alone is not worth a decode.
    /// </summary>
    public MeetingSegmentRef? Flush()
    {
        MeetingSegmentRef? tail = (heardSpeech || Configuration.PreserveQuietAudio) && end > segmentStart
            ? new MeetingSegmentRef(Channel, segmentStart, end - segmentStart)
            : null;
        segmentStart = end;
        segmenter.Reset();
        heardSpeech = false;
        recent.Clear();
        recentFrames = 0;
        return tail;
    }

    private void Remember(Frame frame)
    {
        recent.Add(frame);
        recentFrames += frame.Count;
        int window = Frames(Configuration.ForcedCutWindow);
        while (recent.Count > 1 && recentFrames - recent[0].Count >= window)
        {
            recentFrames -= recent[0].Count;
            recent.RemoveAt(0);
        }
    }

    /// <summary>
    /// Cut at the end of the quietest recent frame, then feed the frames after it back to a fresh
    /// segmenter so the remainder is accounted for exactly.
    /// </summary>
    private MeetingSegmentRef ForceCut()
    {
        // Ties go to the newest frame: with nothing to choose between, cut as close to the cap as
        // possible and keep the segment long.
        int quietest = 0;
        for (int index = 0; index < recent.Count; index++)
        {
            if (recent[index].Peak <= recent[quietest].Peak) { quietest = index; }
        }
        List<Frame> after = recent.GetRange(quietest + 1, recent.Count - quietest - 1);
        int remainder = after.Sum(frame => frame.Count);
        int cutFrame = end - remainder;
        var segment = new MeetingSegmentRef(Channel, segmentStart, cutFrame - segmentStart);

        segmentStart = cutFrame;
        segmenter.Reset();
        heardSpeech = false;
        recent.Clear();
        recentFrames = 0;
        foreach (Frame frame in after)
        {
            _ = segmenter.Observe(frame.Peak, frame.Count);
            if (frame.Peak >= Configuration.Speech.SpeechPeak) { heardSpeech = true; }
            Remember(frame);
        }
        return segment;
    }
}
