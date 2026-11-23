namespace DictationCore;

/// <summary>
/// Keeps the other side's words out of the person's own paragraphs.
///
/// Without headphones, what the computer plays reaches the microphone - measured on a laptop at
/// high volume: at roughly half the tapped level, about 30 ms later. That is loud enough to clear
/// the segmenter's speech floor, so a remote sentence would be decoded twice and once attributed
/// to You.
///
/// Loudness alone cannot tell the two apart: the ratio hovered around two and dipped below it
/// within the same sentence. Shape can. Microphone audio that is a delayed copy of the system
/// audio correlates with it - 0.4 to 0.6 at the right lag against nothing at the others, in the
/// same measurement - while the person's own voice does not.
///
/// Two things about the segmenter shape the rest. It never sees samples, only a peak per block,
/// and blocks are whatever the sources deliver - often a few milliseconds, far too short for a
/// stable estimate. And a single block that slips through becomes a segment whose audio is read
/// back from the file, so the whole sentence returns. Decisions are therefore made over a rolling
/// window, and a positive decision outlasts its evidence long enough to cover the gaps between
/// words and the room's tail. Nothing is removed from the file.
///
/// The person talking <i>over</i> the other side on speakers is the one case this cannot
/// resolve: their own voice plus the echo may still correlate, and those words go unattributed.
/// Headphones fix that; the UI says so.
/// </summary>
/// <remarks>
/// The Swift original is a mutable <c>struct</c> classified in place; a small class keeps that
/// meaning. Its <c>Equatable</c> conformance is not ported: nothing compares gates. Every count is
/// in frames (samples) of 16 kHz mono audio.
/// </remarks>
public sealed class CrossTalkGate
{
    public sealed record Parameters
    {
        public Parameters(
            float activationRms = 0.005f,
            float echoCorrelation = 0.25f,
            int maximumLagFrames = 1_600,
            int lagStepFrames = 32,
            int windowFrames = 5_120,
            int decisionFrames = 1_600,
            int holdFrames = 4_800)
        {
            ActivationRms = activationRms;
            EchoCorrelation = echoCorrelation;
            MaximumLagFrames = maximumLagFrames;
            LagStepFrames = lagStepFrames;
            WindowFrames = windowFrames;
            DecisionFrames = decisionFrames;
            HoldFrames = holdFrames;
        }

        /// <summary>Below this the system side is ambient noise, not a source of echo.</summary>
        public float ActivationRms { get; init; }

        /// <summary>Normalised correlation at or above which the window is an echo.</summary>
        public float EchoCorrelation { get; init; }

        /// <summary>
        /// The longest delay searched, in frames. 100 ms at 16 kHz covers the acoustic path plus
        /// both audio pipelines with room to spare.
        /// </summary>
        public int MaximumLagFrames { get; init; }

        /// <summary>Lag resolution. 2 ms: finer buys nothing, coarser can miss the peak.</summary>
        public int LagStepFrames { get; init; }

        /// <summary>
        /// How much recent audio a decision looks at. 320 ms is long enough for a stable estimate
        /// and short enough to notice the person starting to talk.
        /// </summary>
        public int WindowFrames { get; init; }

        /// <summary>
        /// How often a fresh decision is made. The correlation is the expensive part, and echo does
        /// not come and go faster than this.
        /// </summary>
        public int DecisionFrames { get; init; }

        /// <summary>
        /// How long a decision outlasts its evidence, on top of the window's own memory: about
        /// 600 ms after the other side stops, which covers the gaps between words and the room's
        /// tail. The person's first words inside it are not lost - a segment carries up to a
        /// second of what precedes its first speech.
        /// </summary>
        public int HoldFrames { get; init; }
    }

    /// <param name="IsEcho">Whether the block is the speakers rather than the person.</param>
    /// <param name="LagFrames">The delay at which the last decision found its match, when it did.</param>
    /// <param name="Correlation">The normalised correlation of the last decision.</param>
    public readonly record struct Verdict(bool IsEcho, int? LagFrames, float Correlation);

    private float[] microphoneWindow = [];

    /// <summary>
    /// Longer than the microphone window by the lag room, so an echo of audio that played just
    /// before the window can still be matched.
    /// </summary>
    private float[] systemWindow = [];

    private int? framesSinceDecision;
    private (int? Lag, float Correlation) lastMatch = (null, 0);
    private int holdRemaining;

    public CrossTalkGate(Parameters? parameters = null)
    {
        Configuration = parameters ?? new Parameters();
    }

    /// <summary>The knobs this gate runs with.</summary>
    public Parameters Configuration { get; }

    /// <summary>
    /// Whether this microphone block is the speakers rather than the person. Blocks must arrive
    /// in file order; the recent past is remembered.
    /// </summary>
    public Verdict Classify(ReadOnlySpan<float> microphone, ReadOnlySpan<float> system)
    {
        if (microphone.IsEmpty || microphone.Length != system.Length)
        {
            return new Verdict(false, null, 0);
        }
        microphoneWindow = AppendSuffix(microphoneWindow, microphone, Configuration.WindowFrames);
        systemWindow = AppendSuffix(
            systemWindow, system, Configuration.WindowFrames + Configuration.MaximumLagFrames);
        holdRemaining = Math.Max(0, holdRemaining - microphone.Length);

        float systemRms = Rms(systemWindow.AsSpan(Math.Max(0, systemWindow.Length - microphoneWindow.Length)));
        bool systemIsActive = systemRms >= Configuration.ActivationRms;

        // Until a decision's worth of context exists, an active system side is taken as echo. The
        // cost is at most that long of leading silence on a segment; the alternative is a stray
        // paragraph.
        if (microphoneWindow.Length < Configuration.DecisionFrames)
        {
            return new Verdict(systemIsActive, null, 0);
        }

        if (framesSinceDecision is { } since && since + microphone.Length < Configuration.DecisionFrames)
        {
            framesSinceDecision = since + microphone.Length;
        }
        else
        {
            framesSinceDecision = 0;
            lastMatch = systemIsActive ? BestMatch() : (null, 0);
            if (lastMatch.Lag is not null && lastMatch.Correlation >= Configuration.EchoCorrelation)
            {
                holdRemaining = Configuration.HoldFrames;
            }
        }
        return new Verdict(holdRemaining > 0, lastMatch.Lag, lastMatch.Correlation);
    }

    /// <summary>Forget the recent past - after a pause, or a route change.</summary>
    public void Reset()
    {
        microphoneWindow = [];
        systemWindow = [];
        framesSinceDecision = null;
        lastMatch = (null, 0);
        holdRemaining = 0;
    }

    /// <summary>
    /// The lag at which the microphone window best matches the system side, and how well. Samples
    /// that would need reference from before the remembered past are left out, not the lag.
    /// </summary>
    private (int? Lag, float Correlation) BestMatch()
    {
        int count = microphoneWindow.Length;
        int offset = systemWindow.Length - count;
        (int? Lag, float Correlation) best = (null, -1);
        for (int lag = 0; lag <= Configuration.MaximumLagFrames; lag += Configuration.LagStepFrames)
        {
            int first = Math.Max(0, lag - offset);
            if (first >= count) { continue; }
            float dot = 0;
            float referenceEnergy = 0;
            float microphoneEnergy = 0;
            for (int i = first; i < count; i++)
            {
                float r = systemWindow[offset + i - lag];
                float m = microphoneWindow[i];
                dot += m * r;
                referenceEnergy += r * r;
                microphoneEnergy += m * m;
            }
            float denominator = MathF.Sqrt(referenceEnergy * microphoneEnergy);
            float correlation = denominator > 0 ? dot / denominator : 0;
            if (correlation > best.Correlation) { best = (lag, correlation); }
        }
        return best;
    }

    public static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) { return 0; }
        float sum = 0;
        foreach (float sample in samples) { sum += sample * sample; }
        return MathF.Sqrt(sum / samples.Length);
    }

    /// <summary>The last <paramref name="limit"/> samples of <paramref name="window"/> followed by <paramref name="block"/>.</summary>
    private static float[] AppendSuffix(float[] window, ReadOnlySpan<float> block, int limit)
    {
        int total = window.Length + block.Length;
        int skip = Math.Max(0, total - limit);
        var result = new float[total - skip];
        int fromWindow = Math.Max(0, window.Length - skip);
        window.AsSpan(window.Length - fromWindow).CopyTo(result);
        block[Math.Max(0, skip - window.Length)..].CopyTo(result.AsSpan(fromWindow));
        return result;
    }
}
