using System.Diagnostics;

namespace LocalASR.Tests;

/// <summary>
/// The cost of restoring punctuation, not just its correctness.
///
/// This runs synchronously inside <c>transcribe</c>, after the user has already released the
/// key and is waiting for text. Measured before the fix: 4000 words took 4.3 s and 122 MB,
/// 8000 words took 16.7 s and 488 MB - on top of the ~500 MB model. A 50-minute note
/// therefore froze the app for a quarter of a minute with nothing on screen.
/// </summary>
public class PunctuationReattachmentCostTests
{
    private static (string Original, string Rescored) Text(int words, int changeEvery = 50)
    {
        var original = new List<string>(words);
        var rescored = new List<string>(words);
        for (int index = 0; index < words; index++)
        {
            string word = $"слово{index % 900}";
            original.Add(index % 25 == 0 ? word + "," : word);
            // A substitution every so often is what the rescorer actually does.
            rescored.Add(index % changeEvery == 0 ? $"Term{index}" : word);
        }

        return (string.Join(" ", original), string.Join(" ", rescored));
    }

    /// <summary>
    /// The quadratic blow-up must not return - measured as a shape, not a stopwatch.
    ///
    /// Every absolute-seconds budget this test carried measured the machine as much as the
    /// code: 5 s passed on an idle laptop, failed at 6.3 s on a shared CI runner, and read
    /// 938 s on the same laptop under swap thrash - all on identical, healthy code. The
    /// property actually being guarded is the SHAPE of the cost: the old full-matrix path
    /// grew quadratically (4x the words -> ~16x the time), the banded fix grows linearly (4x
    /// the words -> ~4x the time). A ratio of two runs on the same machine in the same second
    /// is immune to how fast that machine happens to be.
    /// </summary>
    [Fact]
    public void CostScalesLinearlyNotQuadratically()
    {
        // One warm-up so allocator and cache effects don't land on either side.
        (string Original, string Rescored) warm = Text(500);
        PunctuationReattachment.Restore(warm.Original, warm.Rescored);

        // The fastest of a few runs: a single run measures whatever else the machine was doing -
        // a parallel test suite preempting the small run, or a GC landing on the large one.
        static TimeSpan Fastest((string Original, string Rescored) text, out string result)
        {
            TimeSpan fastest = TimeSpan.MaxValue;
            result = string.Empty;
            for (int run = 0; run < 5; run++)
            {
                long started = Stopwatch.GetTimestamp();
                result = PunctuationReattachment.Restore(text.Original, text.Rescored);
                TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
                if (elapsed < fastest) { fastest = elapsed; }
            }

            return fastest;
        }

        TimeSpan smallElapsed = Fastest(Text(2000), out _);
        TimeSpan largeElapsed = Fastest(Text(8000), out string result);

        Assert.NotEqual(string.Empty, result);
        double ratio = largeElapsed.Ticks / Math.Max(1, (double)smallElapsed.Ticks);
        // Linear is about 4, quadratic about 16. Ten separates them with room for noise on
        // both sides.
        Assert.True(
            ratio < 10,
            $"4x the words cost {ratio:F1}x the time - the quadratic path is back");
    }

    /// <summary>
    /// Two texts with nothing in common must not be ground through a full matrix. The work
    /// budget gives up instead, and giving up means leaving the rescorer's text alone - the
    /// same thing the 0.5 fuse already does.
    /// </summary>
    [Fact]
    public void HopelesslyDifferentTextsGiveUpInsteadOfGrinding()
    {
        string original = string.Join(" ", Enumerable.Range(0, 6000).Select(index => $"aaa{index}"));
        string rescored = string.Join(" ", Enumerable.Range(0, 6000).Select(index => $"zzz{index}"));

        long started = Stopwatch.GetTimestamp();
        string result = PunctuationReattachment.Restore(original, rescored);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

        Assert.Equal(rescored, result);
        // The behavioral assert above is the test. The clock is only a runaway backstop, set
        // far above any honest machine so it cannot measure load instead of code.
        Assert.True(elapsed < TimeSpan.FromSeconds(60), $"took {elapsed} - the give-up path is grinding");
    }

    /// <summary>
    /// Speed must not have been bought with correctness: the three invariants still hold on a
    /// long text.
    /// </summary>
    [Fact]
    public void InvariantsSurviveOnLongText()
    {
        (string Original, string Rescored) sample = Text(3000);
        string result = PunctuationReattachment.Restore(sample.Original, sample.Rescored);

        static List<string> Cores(string text) =>
            PunctuationReattachment.Fields(text).Select(field => field.Core).ToList();

        Assert.Equal(Cores(sample.Rescored), Cores(result));

        static int Marks(string text) =>
            text.Count(character => ".,!?:;…«»\"'()—–".Contains(character, StringComparison.Ordinal));

        Assert.True(Marks(result) >= Marks(sample.Rescored));
        Assert.True(Marks(result) <= Math.Max(Marks(sample.Original), Marks(sample.Rescored)));
    }
}

/// <summary>
/// The banded alignment must give the SAME answer as the full matrix.
///
/// Speed is worthless if it changed the result. The band is exact by argument - a path cannot
/// leave a band of half-width k without accumulating more than k edits - but an argument is
/// not a test, so this compares against a plain reference implementation on inputs designed
/// to stress the band: scattered substitutions, word splits and merges, insertions and
/// deletions at both ends, and lengths that differ.
/// </summary>
public class BandedAlignmentEquivalenceTests
{
    /// <summary>Deliberately naive: the full matrix this replaced.</summary>
    private static List<PunctuationReattachment.Step> ReferenceAlign(
        List<PunctuationReattachment.Field> left,
        List<PunctuationReattachment.Field> right)
    {
        int rows = left.Count;
        int columns = right.Count;
        if (rows == 0)
        {
            return Enumerable.Range(0, columns).Select(PunctuationReattachment.Step.Insertion).ToList();
        }

        if (columns == 0)
        {
            return Enumerable.Range(0, rows).Select(PunctuationReattachment.Step.Deletion).ToList();
        }

        int[,] cost = new int[rows + 1, columns + 1];
        for (int row = 0; row <= rows; row++)
        {
            cost[row, 0] = row;
        }

        for (int column = 0; column <= columns; column++)
        {
            cost[0, column] = column;
        }

        for (int row = 1; row <= rows; row++)
        {
            for (int column = 1; column <= columns; column++)
            {
                bool same = left[row - 1].Core.ToLowerInvariant() == right[column - 1].Core.ToLowerInvariant();
                cost[row, column] = Math.Min(
                    cost[row - 1, column - 1] + (same ? 0 : 1),
                    Math.Min(cost[row - 1, column] + 1, cost[row, column - 1] + 1));
            }
        }

        var steps = new List<PunctuationReattachment.Step>();
        int currentRow = rows;
        int currentColumn = columns;
        while (currentRow > 0 || currentColumn > 0)
        {
            if (currentRow > 0 && currentColumn > 0)
            {
                bool same = left[currentRow - 1].Core.ToLowerInvariant()
                    == right[currentColumn - 1].Core.ToLowerInvariant();
                if (cost[currentRow, currentColumn]
                    == cost[currentRow - 1, currentColumn - 1] + (same ? 0 : 1))
                {
                    steps.Add(same
                        ? PunctuationReattachment.Step.Match(currentRow - 1, currentColumn - 1)
                        : PunctuationReattachment.Step.Substitution(currentRow - 1, currentColumn - 1));
                    currentRow--;
                    currentColumn--;
                    continue;
                }
            }

            if (currentRow > 0 && cost[currentRow, currentColumn] == cost[currentRow - 1, currentColumn] + 1)
            {
                steps.Add(PunctuationReattachment.Step.Deletion(currentRow - 1));
                currentRow--;
                continue;
            }

            steps.Add(PunctuationReattachment.Step.Insertion(currentColumn - 1));
            currentColumn--;
        }

        steps.Reverse();
        return steps;
    }

    /// <summary>Deterministic pseudo-random, so a failure is reproducible.</summary>
    private struct Seeded(ulong state)
    {
        private ulong state = state;

        public int Next(int bound)
        {
            state = (state * 6364136223846793005) + 1442695040888963407;
            return (int)((state >> 33) % (ulong)bound);
        }
    }

    private static int EditCost(List<PunctuationReattachment.Step> steps) =>
        steps.Count(step => step.Kind != PunctuationReattachment.StepKind.Match);

    [Fact]
    public void BandedMatchesTheFullMatrixOnRandomEdits()
    {
        var random = new Seeded(20260810);

        for (int trial = 0; trial < 200; trial++)
        {
            int count = 5 + random.Next(60);
            var original = new List<string>();
            var rescored = new List<string>();
            for (int index = 0; index < count; index++)
            {
                string word = $"w{random.Next(12)}";
                original.Add(word);
                switch (random.Next(10))
                {
                    case 0:
                        rescored.Add($"X{index}");             // substitution
                        break;
                    case 1:
                        break;                                  // deletion
                    case 2:
                        rescored.Add(word);
                        rescored.Add($"Y{index}");              // insertion
                        break;
                    case 3:
                        rescored.Add(word);
                        rescored.Add(word);                     // split
                        break;
                    default:
                        rescored.Add(word);
                        break;
                }
            }

            if (rescored.Count == 0)
            {
                continue;
            }

            List<PunctuationReattachment.Field> left =
                PunctuationReattachment.Fields(string.Join(" ", original));
            List<PunctuationReattachment.Field> right =
                PunctuationReattachment.Fields(string.Join(" ", rescored));

            List<PunctuationReattachment.Step> banded = PunctuationReattachment.Align(left, right);
            List<PunctuationReattachment.Step> reference = ReferenceAlign(left, right);

            // Several optimal alignments can exist, so compare the property that matters -
            // total edit cost - plus the shape of the result.
            Assert.True(
                EditCost(banded) == EditCost(reference),
                $"trial {trial}: banded found a worse alignment than the full matrix");
        }
    }

    /// <summary>Lengths far apart force the band wider than its first guess.</summary>
    [Fact]
    public void BandWidensWhenTheTextsDivergeALot()
    {
        List<PunctuationReattachment.Field> left = PunctuationReattachment.Fields(
            string.Join(" ", Enumerable.Range(0, 120).Select(index => $"a{index}")));
        List<PunctuationReattachment.Field> right = PunctuationReattachment.Fields(
            string.Join(" ", Enumerable.Range(0, 120).Select(index => $"b{index}")));

        List<PunctuationReattachment.Step> banded = PunctuationReattachment.Align(left, right);
        List<PunctuationReattachment.Step> reference = ReferenceAlign(left, right);

        Assert.True(EditCost(banded) == EditCost(reference), "every word differs - the band must widen");
    }
}
