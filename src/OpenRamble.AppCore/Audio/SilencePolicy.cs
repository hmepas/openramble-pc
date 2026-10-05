using DictationCore;

namespace OpenRamble.AppCore.Audio;

/// <summary>
/// When a hands-free dictation has heard enough silence to stop itself.
///
/// Pure, so the rule can be tested without a microphone: peaks and a clock go in, a decision
/// comes out. Nothing here reads a device or a setting.
///
/// Only for hands-free. While the key is held, the person is holding it - they are saying, with
/// their hand, that they have not finished, and a pause for thought is not an ending. Stopping
/// under a held key would take the sentence away mid-thought, which is the failure mode that
/// makes voice input feel hostile.
/// </summary>
/// <remarks>
/// A mutable struct, like the Swift original: the state is two fields and it is reset per take.
/// Hold it in a field and call <see cref="Observe"/> on that field, never on a copy.
/// </remarks>
public struct SilencePolicy : IEquatable<SilencePolicy>
{
    /// <summary>
    /// Below this a frame counts as silence.
    ///
    /// Peak rather than average: a room's noise floor sits low and steady, while speech has
    /// sharp transients even at conversational volume, so a peak separates the two where an
    /// average blurs them.
    /// </summary>
    public const float Threshold = 0.02f;

    /// <summary>
    /// How long the quiet must last.
    ///
    /// Long enough to survive the pause between sentences, which is where a shorter window cuts
    /// people off mid-paragraph, and short enough that a person who has finished does not sit
    /// waiting for the app to notice.
    /// </summary>
    public static readonly TimeSpan RequiredSilence = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Speech must have been heard before silence can end anything.
    ///
    /// Otherwise starting hands-free in a quiet room would stop the recording before a word was
    /// said, and the person would be left holding a feature that appears broken.
    /// </summary>
    public bool HasHeardSpeech { get; private set; }

    public MonotonicInstant? QuietSince { get; private set; }

    /// <summary>Feed one frame's peak. Returns <c>true</c> when the take should finish.</summary>
    public bool Observe(float peak, MonotonicInstant now)
    {
        if (peak >= Threshold)
        {
            HasHeardSpeech = true;
            QuietSince = null;
            return false;
        }

        if (!HasHeardSpeech) { return false; }

        if (QuietSince is not { } since)
        {
            QuietSince = now;
            return false;
        }

        return since.DurationTo(now) >= RequiredSilence;
    }

    /// <summary>A new take starts hearing nothing, from nobody.</summary>
    public void Reset()
    {
        HasHeardSpeech = false;
        QuietSince = null;
    }

    public bool Equals(SilencePolicy other) =>
        HasHeardSpeech == other.HasHeardSpeech && QuietSince == other.QuietSince;

    public override bool Equals(object? obj) => obj is SilencePolicy other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(HasHeardSpeech, QuietSince);

    public static bool operator ==(SilencePolicy left, SilencePolicy right) => left.Equals(right);

    public static bool operator !=(SilencePolicy left, SilencePolicy right) => !left.Equals(right);
}
