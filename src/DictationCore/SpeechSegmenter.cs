namespace DictationCore;

/// <summary>
/// Where a take may be cut so the engine can start work before the person stops
/// talking.
///
/// Pure, like the application's <c>SilencePolicy</c>: frame levels and sample counts go in, cut
/// offsets come out. Nothing here reads a device, a clock or a setting, so the whole rule is
/// testable without a microphone.
///
/// <b>The one rule that makes this safe: a cut is only ever placed inside silence.</b> Never
/// through speech, whatever it costs in latency. A previous attempt in this project chunked on
/// length alone, cut phrases in half, and lost three times more words than the engine did on its
/// own; that failure is excluded here by construction rather than by tuning. When someone talks
/// without pausing, no cut is offered and the segment simply grows - the take then behaves
/// exactly as it does today, which is the worst case rather than a new one.
///
/// Why cut at all, beyond latency: the engine decides one language for a whole decode and imposes
/// it on everything inside, so a long take is also a large blast radius for that decision. And
/// its cost is superlinear - measured on this model, 60 s costs 1.53 s but 140 s costs 5.64 s,
/// against a linear prediction of 2.02 s. Smaller decodes are cheaper per second, not just
/// earlier.
/// </summary>
/// <remarks>
/// The Swift original is a mutable <c>struct</c> observed in place; a small class keeps that
/// meaning without the copy semantics a C# struct would add. Every count here is in samples of
/// 16 kHz mono audio.
/// </remarks>
public sealed class SpeechSegmenter
{
    /// <summary>The knobs, with the values this project measured rather than guessed.</summary>
    public sealed record Parameters
    {
        /// <summary>
        /// The same constant the application's <c>SilencePolicy</c> uses for hands-free, repeated
        /// rather than shared because that type lives in the application layer and this package
        /// must not depend on it. The reasoning is the same: a room's noise floor sits low and
        /// steady while speech has sharp transients even when quiet. A fixed threshold fails safe -
        /// in a room too noisy for it, no pause is ever detected, no cut is offered, and the take
        /// behaves as it does today.
        /// </summary>
        public const float DefaultSpeechPeak = 0.02f;

        public Parameters(
            TimeSpan? minimumPause = null,
            TimeSpan? minimumSegment = null,
            TimeSpan? minimumSubmission = null,
            TimeSpan? relaxAfter = null,
            TimeSpan? relaxedPause = null,
            float speechPeak = DefaultSpeechPeak)
        {
            MinimumPause = minimumPause ?? TimeSpan.FromMilliseconds(700);
            MinimumSegment = minimumSegment ?? TimeSpan.FromSeconds(15);
            MinimumSubmission = minimumSubmission ?? TimeSpan.FromSeconds(2);
            RelaxAfter = relaxAfter ?? TimeSpan.FromSeconds(30);
            RelaxedPause = relaxedPause ?? TimeSpan.FromMilliseconds(350);
            SpeechPeak = speechPeak;
        }

        /// <summary>
        /// Silence at least this long may carry a cut.
        ///
        /// Below roughly 300 ms a pause stops being a pause: stop closures inside ordinary words
        /// run to 150 ms, and cutting there puts a seam inside a word. Measured on five real
        /// takes, 350 ms cut a hesitation inside a spoken number and split "2024" across two
        /// decodes, while 700 ms did not; fidelity against a whole-file decode went 95.7% → 97.0%
        /// across that range. The default is the fidelity end.
        /// </summary>
        public TimeSpan MinimumPause { get; init; }

        /// <summary>
        /// How much audio must be pending before a pause is worth spending.
        ///
        /// Fifteen seconds, and a cut is not free even when it lands in silence.
        ///
        /// Every cut is a new audio boundary, and this engine chooses the language of a decode
        /// partly from where its audio stops - so each one is another draw from a distribution
        /// that is sometimes wrong. Measured across varied boundaries in real recordings, about
        /// one decode in ninety comes back in the wrong script. A cut also costs meaning at the
        /// seam: the two sides are recognized without each other's context, which on measured
        /// takes moved about 2% of words and once split a spoken year across the join.
        ///
        /// So a cut has to buy something. Below this length the whole take decodes in a fraction
        /// of a second and streaming it saves nothing anybody feels; past a minute the whole
        /// decode grows superlinearly and streaming saves seconds. Fifteen rather than thirty
        /// because thirty was measured on a fast Mac: on hardware three times slower a
        /// thirty-second tail is over a second of waiting, which is a certainty, against a couple
        /// of percentage points of extra risk that only applies to takes over half a minute. On
        /// ordinary dictation the two are identical - a take of sixteen seconds is one segment
        /// either way.
        /// </summary>
        public TimeSpan MinimumSegment { get; init; }

        /// <summary>
        /// Never hand the engine less than this.
        ///
        /// Measured, and the reason this parameter exists: below about two seconds the decoder is
        /// not merely worse, it is <i>non-monotonic</i>. The same start offset returned "Yeah." at
        /// 0.8 s, nothing at 1.0 s, "Whoa." at 1.2 s and nothing at 1.6 s. Adding audio removed
        /// output. A segment shorter than this is not shipped on its own; it stays with the audio
        /// that follows it.
        /// </summary>
        public TimeSpan MinimumSubmission { get; init; }

        /// <summary>
        /// Once the pending audio passes this, accept shorter pauses.
        ///
        /// A person mid-argument may not offer a 700 ms pause for a minute, and past this length
        /// the whole-take decode is expensive enough that waiting for a better pause costs more
        /// than taking a worse one. Still a real pause, never a cut through speech.
        /// </summary>
        public TimeSpan RelaxAfter { get; init; }

        /// <summary>The shortest pause accepted once <see cref="RelaxAfter"/> has passed.</summary>
        public TimeSpan RelaxedPause { get; init; }

        /// <summary>A frame at or above this peak counts as speech.</summary>
        public float SpeechPeak { get; init; }
    }

    private readonly int sampleRate;

    /// <summary>Samples seen since the last cut. The pending segment's length.</summary>
    private int pendingSamples;

    /// <summary>
    /// Length of the silence currently running, in samples, or <c>null</c> while the person is
    /// speaking.
    /// </summary>
    private int? quietSamples;

    /// <summary>
    /// Whether any speech has been heard in the pending segment.
    ///
    /// A segment of pure silence is not worth a decode, and a take that begins with someone
    /// finding their words must not be cut before the first one.
    /// </summary>
    private bool heardSpeech;

    public SpeechSegmenter(Parameters? parameters = null, int sampleRate = 16_000)
    {
        this.Configuration = parameters ?? new Parameters();
        this.sampleRate = sampleRate;
    }

    /// <summary>The knobs this segmenter runs with.</summary>
    public Parameters Configuration { get; }

    private int Samples(TimeSpan duration) => (int)(duration.TotalSeconds * sampleRate);

    /// <summary>
    /// The pause length required right now, given how much is pending.
    ///
    /// Exposed for the tests that pin the relaxation, and because a rule worth having is a rule
    /// worth reading.
    /// </summary>
    internal int RequiredPauseSamples =>
        pendingSamples >= Samples(Configuration.RelaxAfter)
            ? Samples(Configuration.RelaxedPause)
            : Samples(Configuration.MinimumPause);

    /// <summary>
    /// Feed one frame of committed audio.
    ///
    /// <paramref name="peak"/> is the frame's peak magnitude and <paramref name="count"/> its
    /// sample count. The return value is the offset <b>within the pending segment</b> at which to
    /// cut, or <c>null</c> for "keep going". The offset is measured from the start of the pending
    /// segment, so the caller never has to track absolute positions.
    ///
    /// The cut lands in the middle of the silence that earned it, which leaves quiet on both sides
    /// of the seam: the segment that closes ends in silence and the one that opens begins in it.
    /// Recordings padded with silence at either edge decode exactly as they do without it, so
    /// this costs nothing and spares both decodes a hard edge against speech.
    /// </summary>
    public int? Observe(float peak, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        pendingSamples += count;

        if (peak >= Configuration.SpeechPeak)
        {
            heardSpeech = true;
            quietSamples = null;
            return null;
        }

        // Silence before the first word is not a pause between words. Without this, a take that
        // starts with a breath would be cut before it began.
        if (!heardSpeech) { return null; }

        int quiet = (quietSamples ?? 0) + count;
        quietSamples = quiet;

        if (quiet < RequiredPauseSamples) { return null; }
        if (pendingSamples < Samples(Configuration.MinimumSegment)) { return null; }

        // The midpoint of the silence observed so far. `pendingSamples` already includes this
        // frame, so the silence occupies the last `quiet` samples.
        int cut = pendingSamples - quiet / 2;

        // Never ship less than the engine can be trusted with. A segment this short waits for the
        // audio after it instead.
        if (cut < Samples(Configuration.MinimumSubmission)) { return null; }

        pendingSamples -= cut;
        quietSamples = quiet - quiet / 2;
        heardSpeech = false;
        return cut;
    }

    /// <summary>Forget the pending segment. For a new take.</summary>
    public void Reset()
    {
        pendingSamples = 0;
        quietSamples = null;
        heardSpeech = false;
    }

    /// <summary>
    /// Whether the pending audio is worth handing to the engine on its own.
    ///
    /// The tail at key-release goes through this: a tail shorter than
    /// <see cref="Parameters.MinimumSubmission"/> must be merged with what came before rather than
    /// decoded alone, because on its own it may decode to nothing at all.
    /// </summary>
    public bool PendingIsSubmittable => pendingSamples >= Samples(Configuration.MinimumSubmission);

    /// <summary>How much audio is waiting, for the caller's own accounting.</summary>
    public int PendingSampleCount => pendingSamples;
}
