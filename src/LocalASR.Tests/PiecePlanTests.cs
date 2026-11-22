namespace LocalASR.Tests;

/// <summary>
/// The tests of upstream's <c>apps/cli/src/plan.rs</c>, on the same synthetic envelopes and with
/// the same expected pieces, so this port, the Mac and the server cut one way.
/// </summary>
public class PiecePlanTests
{
    private const int S = PiecePlan.FramesPerSecond;
    private const float SpeechLevel = 0.1f;

    /// <summary>Speech-like loudness: never quiet, never flat. The server's generator.</summary>
    private static float[] Speech(int seconds, uint seed)
    {
        unchecked
        {
            uint state = seed * 2_654_435_761 + 1;
            var result = new float[seconds * S];
            for (int index = 0; index < result.Length; index++)
            {
                state = state * 1_664_525 + 1_013_904_223;
                result[index] = SpeechLevel * (0.6f + 0.8f * (float)(state >> 8) / (float)(1 << 24));
            }

            return result;
        }
    }

    /// <summary>Low steady noise, as a quiet room records.</summary>
    private static float[] Room(int seconds) =>
        Enumerable.Range(0, seconds * S).Select(index => 0.002f * (1 + 0.02f * ((float)(index % 7) - 3))).ToArray();

    private static void Put(float[] envelope, float seconds, float[] words)
    {
        int at = (int)(seconds * S);
        words.CopyTo(envelope, at);
    }

    private static PieceRange R(int start, int end) => new(start, end);

    private static IReadOnlyList<PieceRange> Gated(float[] envelope) =>
        PiecePlan.Plan(envelope, R(0, envelope.Length), 20, gate: true);

    [Fact]
    public void SilenceAndDigitalSilenceGiveNoPieces()
    {
        Assert.Empty(Gated(Room(60)));
        Assert.Empty(Gated(new float[60 * S]));
        Assert.Empty(PiecePlan.Plan([], R(0, 0), 20, gate: true));
    }

    [Fact]
    public void AWordInAMinuteOfRoomNoiseGetsASecondOnEachSide()
    {
        float[] envelope = Room(60);
        Put(envelope, 30, Speech(1, 1));
        Assert.Equal([R(29 * S, 32 * S)], Gated(envelope));
    }

    [Fact]
    public void WordsAMomentApartMergeAndWordsFarApartDoNot()
    {
        float[] close = Room(60);
        Put(close, 10, Speech(1, 1));
        Put(close, 12.5f, Speech(1, 2));
        Assert.Equal([R(9 * S, 29 * S / 2)], Gated(close));
        float[] apart = Room(60);
        Put(apart, 10, Speech(1, 1));
        Put(apart, 14, Speech(1, 2));
        Assert.Equal([R(9 * S, 12 * S), R(13 * S, 16 * S)], Gated(apart));
    }

    [Fact]
    public void AClickIsNotSpeech()
    {
        float[] envelope = Room(60);
        Put(envelope, 30, Enumerable.Repeat(SpeechLevel, 4).ToArray());
        Assert.Empty(Gated(envelope));
        Put(envelope, 30, Enumerable.Repeat(SpeechLevel, 5).ToArray());
        Assert.Single(Gated(envelope));
    }

    [Fact]
    public void ContinuousSpeechIsCutAtItsPausesIntoContiguousPieces()
    {
        float[] envelope = Speech(95, 7);
        int length = envelope.Length;
        var planted = new List<int>();
        // n = ceil(95 / 16) = 6 pieces; plant a V-shaped dip near each even cut.
        int[] shifts = [75, -95, 20, -40, 100];
        for (int k = 1; k < 6; k++)
        {
            int at = k * length / 6 + shifts[k - 1];
            for (int offset = -5; offset <= 5; offset++) { envelope[at + offset] = 0.05f * Math.Abs(offset) / 6; }
            planted.Add(at);
        }

        IReadOnlyList<PieceRange> pieces = PiecePlan.Plan(envelope, R(0, length), 20, gate: false);
        Assert.Equal(6, pieces.Count);
        Assert.Equal(0, pieces[0].Start);
        Assert.Equal(length, pieces[^1].End);
        Assert.All(pieces, piece => Assert.True(piece.Length <= 20 * S));
        Assert.DoesNotContain(pieces.Zip(pieces.Skip(1)), pair => pair.First.End != pair.Second.Start);
        Assert.Equal(planted, pieces.Skip(1).Select(piece => piece.Start));
    }

    /// <summary>
    /// <c>the_mac_cuts_the_same_frames</c> in plan.rs asserts these same pieces. Change both, or
    /// neither.
    /// </summary>
    [Fact]
    public void TheServerCutsTheSameFrames()
    {
        float[] envelope = Room(300);
        Put(envelope, 12, Speech(75, 11));
        Put(envelope, 100, Speech(3, 12));
        Put(envelope, 140, Speech(130, 13));
        Assert.Equal(
            [
                R(550, 1163), R(1163, 1753), R(1753, 2209), R(2233, 2891), R(2891, 3581), R(3581, 4400),
                R(4950, 5200), R(6950, 7716), R(7716, 8508), R(8508, 9266), R(9266, 9983), R(9983, 10819),
                R(10821, 11543), R(11543, 12148), R(12148, 12846), R(12846, 13550),
            ],
            Gated(envelope));
    }

    [Fact]
    public void LongRegionsSplitIntoPiecesNearSixteenSeconds()
    {
        foreach ((int seconds, int count) in new[] { (12, 1), (20, 1), (30, 2), (60, 4), (240, 15) })
        {
            float[] envelope = Speech(seconds, (uint)seconds);
            IReadOnlyList<PieceRange> pieces = PiecePlan.Plan(envelope, R(0, envelope.Length), 20, gate: false);
            Assert.Equal(count, pieces.Count);
            Assert.All(pieces, piece => Assert.InRange(piece.Length, 10 * S, 20 * S));
        }
    }

    [Fact]
    public void TheGateNeverReachesOutsideTheSpan()
    {
        float[] envelope = Room(60);
        Put(envelope, 20, Speech(10, 4));
        Assert.Equal(
            [R(20 * S + 10, 25 * S)],
            PiecePlan.Plan(envelope, R(20 * S + 10, 25 * S), 20, gate: true));
    }

    [Fact]
    public void PiecesOfAFileAreInSamplesAndEndAtItsLastSample()
    {
        float[] envelope = Room(100);
        Put(envelope, 70, Speech(5, 6));
        Assert.Equal([R(69 * 16_000, 76 * 16_000)], PiecePlan.Pieces(envelope, 100 * 16_000 - 77, 20));
        float[] shortEnvelope = Room(3);
        Put(shortEnvelope, 0.5f, Speech(2, 1));
        Assert.Equal([R(0, 3 * 16_000 - 77)], PiecePlan.Pieces(shortEnvelope, 3 * 16_000 - 77, 20));
    }
}
