namespace DictationCore.Tests;

/// <summary>
/// Echo is a delayed copy; the person is not. Decisions are made over a window, held for a while,
/// and never lose the person's first words.
/// </summary>
public sealed class CrossTalkGateTests
{
    /// <summary>Deterministic "speech": a fixed pseudo-random sequence.</summary>
    private static float[] Noise(int count, ulong seed, float amplitude)
    {
        ulong state = seed;
        var samples = new float[count];
        for (int i = 0; i < count; i++)
        {
            state = unchecked(state * 6_364_136_223_846_793_005UL + 1_442_695_040_888_963_407UL);
            samples[i] = ((float)(state >> 40) / (1 << 24) * 2 - 1) * amplitude;
        }
        return samples;
    }

    private static float[] Delayed(float[] samples, int frames, float gain) =>
        [.. Enumerable.Range(0, samples.Length).Select(i => i < frames ? 0 : samples[i - frames] * gain)];

    private static float[] Mixed(float[] a, float[] b) => [.. a.Zip(b, (x, y) => x + y)];

    [Fact]
    public void TestTheSpeakersLeakingIntoTheMicrophoneIsAnEchoAtTheRightLag()
    {
        var gate = new CrossTalkGate();
        var system = Noise(5_120, seed: 1, amplitude: 0.2f);
        // Half the level, 30 ms later, over a little room noise.
        var microphone = Mixed(Delayed(system, 480, gain: 0.5f), Noise(5_120, seed: 2, amplitude: 0.01f));
        var verdict = gate.Classify(microphone, system);
        Assert.True(verdict.IsEcho);
        Assert.InRange(verdict.LagFrames ?? -1, 480 - 32, 480 + 32);
        Assert.True(verdict.Correlation > 0.9f);
    }

    [Fact]
    public void TestAnEchoOfTheEndOfThePreviousBlockIsStillCaught()
    {
        var gate = new CrossTalkGate();
        var first = Noise(5_120, seed: 3, amplitude: 0.2f);
        var second = Noise(5_120, seed: 4, amplitude: 0.2f);
        float[] whole = [.. first, .. second];
        // The microphone hears everything 40 ms late: the start of this block's echo is the end
        // of the previous block's audio.
        var echo = Delayed(whole, 640, gain: 0.5f);
        _ = gate.Classify(echo.AsSpan(0, 5_120), first);
        var verdict = gate.Classify(echo.AsSpan(5_120), second);
        Assert.True(verdict.IsEcho);
        Assert.InRange(verdict.LagFrames ?? -1, 640 - 32, 640 + 32);
    }

    /// <summary>
    /// Sources deliver a few milliseconds at a time. Every one of those blocks must come out as
    /// echo, or the segmenter makes a paragraph of it.
    /// </summary>
    [Fact]
    public void TestEveryTinyBlockOfAnEchoIsAnEcho()
    {
        var gate = new CrossTalkGate();
        var system = Noise(48_000, seed: 5, amplitude: 0.2f);
        var echo = Delayed(system, 480, gain: 0.5f);
        List<bool> verdicts = [];
        int start = 0;
        while (start < system.Length)
        {
            int end = Math.Min(start + 170, system.Length);
            verdicts.Add(gate.Classify(echo.AsSpan(start..end), system.AsSpan(start..end)).IsEcho);
            start = end;
        }
        Assert.Equal(283, verdicts.Count);
        Assert.True(verdicts.All(v => v), $"{verdicts.Count(v => !v)} blocks slipped through");
    }

    /// <summary>
    /// Before there is enough context to decide, an active system side is taken as echo and a
    /// quiet one is not.
    /// </summary>
    [Fact]
    public void TestBeforeEnoughContextAnActiveSystemSideIsTakenAsEcho()
    {
        var gate = new CrossTalkGate();
        var system = Noise(512, seed: 6, amplitude: 0.2f);
        Assert.True(gate.Classify(Noise(512, seed: 7, amplitude: 0.1f), system).IsEcho);
        gate.Reset();
        Assert.False(gate.Classify(Noise(512, seed: 7, amplitude: 0.1f), Noise(512, seed: 8, amplitude: 0.001f)).IsEcho);
    }

    /// <summary>
    /// The other side stops; the room keeps ringing for a moment and the microphone still clears
    /// the speech floor. The decision holds through it - the window remembers the source for
    /// 320 ms and the hold adds 300 - then lets go.
    /// </summary>
    [Fact]
    public void TestADecisionOutlastsItsEvidenceForAboutSixHundredMilliseconds()
    {
        var gate = new CrossTalkGate();
        var system = Noise(5_120, seed: 9, amplitude: 0.2f);
        Assert.True(gate.Classify(Delayed(system, 480, gain: 0.5f), system).IsEcho);
        List<bool> verdicts = [];
        for (int block = 0; block < 8; block++)
        {
            var quiet = Noise(1_600, seed: 100 + (ulong)block, amplitude: 0.001f);
            var tail = Noise(1_600, seed: 200 + (ulong)block, amplitude: 0.03f);
            verdicts.Add(gate.Classify(tail, quiet).IsEcho);
        }
        Assert.Equal([true, true, true, true, true, false, false, false], verdicts);
    }

    [Fact]
    public void TestThePersonSpeakingIsNotAnEchoWhateverTheLevels()
    {
        var gate = new CrossTalkGate();
        var system = Noise(5_120, seed: 10, amplitude: 0.2f);
        var person = Noise(5_120, seed: 11, amplitude: 0.05f);
        var verdict = gate.Classify(person, system);
        Assert.False(verdict.IsEcho, "quieter than the other side, but not a copy of it");
        Assert.True(verdict.Correlation < 0.15f);
    }

    [Fact]
    public void TestThePersonTalkingOverAFaintLeakIsStillThePerson()
    {
        var gate = new CrossTalkGate();
        var system = Noise(5_120, seed: 12, amplitude: 0.2f);
        var microphone = Mixed(Noise(5_120, seed: 13, amplitude: 0.15f), Delayed(system, 480, gain: 0.05f));
        Assert.False(gate.Classify(microphone, system).IsEcho);
    }

    /// <summary>
    /// The person starts talking right after the other side stops: the hold ends on its own once
    /// the microphone stops matching.
    /// </summary>
    [Fact]
    public void TestThePersonTakingOverEndsTheHold()
    {
        var gate = new CrossTalkGate();
        var system = Noise(5_120, seed: 14, amplitude: 0.2f);
        Assert.True(gate.Classify(Delayed(system, 480, gain: 0.5f), system).IsEcho);
        List<bool> verdicts = [];
        for (int block = 0; block < 8; block++)
        {
            var other = Noise(1_600, seed: 300 + (ulong)block, amplitude: 0.2f);
            var person = Noise(1_600, seed: 400 + (ulong)block, amplitude: 0.15f);
            verdicts.Add(gate.Classify(person, other).IsEcho);
        }
        Assert.Equal([false, false, false], verdicts.TakeLast(3));
    }

    [Fact]
    public void TestAQuietSystemSideCannotBeAnEchoSource()
    {
        var gate = new CrossTalkGate();
        var system = Noise(5_120, seed: 15, amplitude: 0.002f);
        var microphone = Delayed(system, 480, gain: 1);
        Assert.False(gate.Classify(microphone, system).IsEcho);
    }

    [Fact]
    public void TestMismatchedOrEmptyBlocksAreNotEcho()
    {
        var gate = new CrossTalkGate();
        Assert.False(gate.Classify([], []).IsEcho);
        Assert.False(gate.Classify([0.1f, 0.2f], [0.1f]).IsEcho);
    }

    [Fact]
    public void TestRms()
    {
        Assert.Equal(0f, CrossTalkGate.Rms([]));
        Assert.Equal(0.5f, CrossTalkGate.Rms([0.5f, -0.5f, 0.5f, -0.5f]), 0.0001f);
    }
}
