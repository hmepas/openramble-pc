using DictationCore;
using OpenRamble.AppCore.Audio;

namespace OpenRamble.AppCore.Tests;

/// <summary>Stopping on silence, and the several ways that could go wrong.</summary>
public sealed class SilencePolicyTests
{
    private const float Loud = 0.5f;
    private const float Quiet = 0.001f;

    private static MonotonicInstant At(double seconds, MonotonicInstant start) =>
        start.Advanced(TimeSpan.FromMilliseconds((int)(seconds * 1000)));

    /// <summary>Speech, then a long enough pause, ends the take.</summary>
    [Fact]
    public void TestScenario001()
    {
        var policy = new SilencePolicy();
        MonotonicInstant start = MonotonicInstant.Now;
        Assert.False(policy.Observe(Loud, start));
        Assert.False(policy.Observe(Quiet, At(0.5, start)));
        Assert.False(policy.Observe(Quiet, At(1.5, start)));
        Assert.True(policy.Observe(Quiet, At(2.6, start)));
    }

    /// <summary>
    /// Silence before a word is said ends nothing.
    ///
    /// Starting hands-free in a quiet room would otherwise stop the recording before the person
    /// began, and the feature would look broken.
    /// </summary>
    [Fact]
    public void TestScenario002()
    {
        var policy = new SilencePolicy();
        MonotonicInstant start = MonotonicInstant.Now;
        for (int second = 0; second <= 10; second++)
        {
            Assert.False(policy.Observe(Quiet, At(second, start)), "no speech yet, so there is nothing to end");
        }
    }

    /// <summary>
    /// A pause between sentences is not an ending.
    ///
    /// The quiet clock restarts on the next word, so someone who thinks for a second and carries
    /// on keeps their recording.
    /// </summary>
    [Fact]
    public void TestScenario003()
    {
        var policy = new SilencePolicy();
        MonotonicInstant start = MonotonicInstant.Now;
        _ = policy.Observe(Loud, start);
        _ = policy.Observe(Quiet, At(1.0, start));
        Assert.False(policy.Observe(Loud, At(1.8, start)));
        // The quiet clock restarts at the first quiet frame after the word - here 3.0 - so two
        // seconds run out at 5.0, not measured from the word.
        Assert.False(policy.Observe(Quiet, At(3.0, start)));
        Assert.False(policy.Observe(Quiet, At(4.0, start)));
        Assert.True(policy.Observe(Quiet, At(5.1, start)));
    }

    /// <summary>
    /// A new take starts from nothing, or the previous one's silence would end it before a word
    /// was said.
    /// </summary>
    [Fact]
    public void TestScenario004()
    {
        var policy = new SilencePolicy();
        MonotonicInstant start = MonotonicInstant.Now;
        _ = policy.Observe(Loud, start);
        _ = policy.Observe(Quiet, At(1.0, start));
        policy.Reset();
        Assert.False(policy.HasHeardSpeech);
        Assert.Null(policy.QuietSince);
        Assert.False(policy.Observe(Quiet, At(9.0, start)));
    }

    /// <summary>
    /// The threshold separates a room from a voice rather than silence from sound: a quiet room
    /// is never exactly zero.
    /// </summary>
    [Fact]
    public void TestScenario005()
    {
        var policy = new SilencePolicy();
        MonotonicInstant start = MonotonicInstant.Now;
        _ = policy.Observe(Loud, start);
        // A plausible noise floor still counts as quiet.
        Assert.False(policy.Observe(0.015f, At(0.5, start)));
        Assert.True(policy.Observe(0.015f, At(3.0, start)));
    }
}
