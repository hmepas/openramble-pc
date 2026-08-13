using System.Globalization;

namespace LocalASR;

/// <summary>
/// Return punctuation marks lost by the dictionary rescorer.
///
/// Measurement in <c>docs/benchmarks.md</c>: on long recordings the rescorer left 347
/// characters out of 450 - it replaces a word along with the punctuation stuck to it. A
/// defect of the library, but our call, so a wrapper around the result works here rather
/// than an edit to the engine.
///
/// The seam is intentionally at the level of strings, not tokens: both sides are a plain
/// string, so the function is pure and is checked without a model at all.
///
/// Marks are removed <b>only from the edges</b> of a field, the middle is not touched. That
/// protects <c>3.14</c>, <c>etc</c>, <c>https://</c>, <c>don't</c> and <c>typescript</c>.
///
/// Three invariants the tests rest on:
/// 1. not a single mark produced by the rescorer is deleted;
/// 2. new marks do not appear - only ones from the union of both sides;
/// 3. <b>words never change</b> - otherwise returning punctuation would undo the dictionary
///    edit the rescorer exists for.
/// </summary>
internal static class PunctuationReattachment
{
    /// <summary>The only public entry point.</summary>
    internal static string Restore(string original, string rescored)
    {
        if (original == rescored)
        {
            return rescored;
        }

        if (original.Length == 0 || rescored.Length == 0)
        {
            return rescored;
        }

        List<Field> originalFields = Fields(original);
        List<Field> rescoredFields = Fields(rescored);
        if (originalFields.Count == 0 || rescoredFields.Count == 0)
        {
            return rescored;
        }

        List<Step> steps = Align(originalFields, rescoredFields);

        // The fuse. If the texts diverge too much, understanding which mark belongs where is
        // no longer possible, and inventing punctuation is worse than losing it. This is
        // exactly today's behavior - and it is what is tested.
        //
        // The denominator is the short side, not the rescorer's side. A word split
        // ("Postgres" -> "Post gres") legitimately increases the number of fields on the
        // right, and dividing by them would suppress exactly the case all this was written
        // for. Only exact matches are counted: if the texts are entirely different, the
        // alignment will still give a run of substitutions, and those are no signal.
        int matched = steps.Count(step => step.Kind == StepKind.Match);
        int anchor = Math.Min(originalFields.Count, rescoredFields.Count);
        if (anchor <= 0 || (double)matched / anchor < 0.5)
        {
            return rescored;
        }

        var rendered = new List<Field>();
        // Where a mark taken from the original was placed. Needed because a word split
        // arrives as a tail of insertions: the mark must reach the last piece, otherwise
        // "postgres." becomes "Post. gres" instead of "Post gres."
        int? borrowedTrailAt = null;

        foreach (Step step in steps)
        {
            switch (step.Kind)
            {
                case StepKind.Match:
                case StepKind.Substitution:
                {
                    Field source = originalFields[step.Left];
                    Field field = rescoredFields[step.Right];
                    // The rescorer's own mark is always more important: it lost nothing there.
                    if (field.Lead.Length == 0)
                    {
                        field.Lead = source.Lead;
                    }

                    bool borrowed = false;
                    if (field.Trail.Length == 0 && source.Trail.Length != 0)
                    {
                        field.Trail = source.Trail;
                        borrowed = true;
                    }

                    rendered.Add(field);
                    borrowedTrailAt = borrowed ? rendered.Count - 1 : null;
                    break;
                }

                case StepKind.Insertion:
                {
                    // The rescorer split a word into several - carry it over as it is, but
                    // take the borrowed mark along to the end of the gap.
                    Field field = rescoredFields[step.Right];
                    if (borrowedTrailAt is { } source
                        && source == rendered.Count - 1
                        && field.Trail.Length == 0)
                    {
                        field.Trail = rendered[source].Trail;
                        Field donor = rendered[source];
                        donor.Trail = string.Empty;
                        rendered[source] = donor;
                        rendered.Add(field);
                        borrowedTrailAt = rendered.Count - 1;
                    }
                    else
                    {
                        rendered.Add(field);
                        borrowedTrailAt = null;
                    }

                    break;
                }

                case StepKind.Deletion:
                {
                    // The rescorer glued several words into one. The mark is given only by
                    // the last deleted field, and only if the receiver has nothing to lose:
                    // restoring "pull, request" from "pull request" would mean inventing
                    // punctuation inside the term.
                    if (rendered.Count == 0 || originalFields[step.Left].Trail.Length == 0)
                    {
                        continue;
                    }

                    if (rendered[^1].Trail.Length == 0)
                    {
                        Field last = rendered[^1];
                        last.Trail = originalFields[step.Left].Trail;
                        rendered[^1] = last;
                    }

                    break;
                }
            }
        }

        return string.Concat(rendered.Select(field => field.Whole));
    }

    // MARK: - Fields

    /// <summary>
    /// A field is one non-whitespace piece with the spaces in front of it.
    ///
    /// Spaces are kept so the text can be put back together without reformatting: line
    /// breaks and alignment stay exactly as the rescorer gave them.
    /// </summary>
    /// <remarks>
    /// The Swift original is a mutable <c>struct</c>; the port keeps value semantics, so
    /// every mutation goes through a copy back into the list.
    /// </remarks>
    internal struct Field : IEquatable<Field>
    {
        public Field(string spacingBefore, string lead, string core, string trail)
        {
            SpacingBefore = spacingBefore;
            Lead = lead;
            Core = core;
            Trail = trail;
        }

        public string SpacingBefore { get; set; }

        public string Lead { get; set; }

        public string Core { get; set; }

        public string Trail { get; set; }

        public readonly string Whole => SpacingBefore + Lead + Core + Trail;

        public readonly bool Equals(Field other) =>
            SpacingBefore == other.SpacingBefore
            && Lead == other.Lead
            && Core == other.Core
            && Trail == other.Trail;

        public override readonly bool Equals(object? other) => other is Field field && Equals(field);

        public override readonly int GetHashCode() =>
            HashCode.Combine(SpacingBefore, Lead, Core, Trail);
    }

    /// <summary>
    /// Marks that are stripped from the edges.
    ///
    /// There is no hyphen here on purpose: <c>-x</c> is a switch, not a word with a mark, and
    /// the hyphen cannot be torn off it.
    /// </summary>
    /// <remarks>
    /// The set holds grapheme clusters, not <c>char</c>s: the Swift original iterates
    /// <c>Character</c>s, and splitting a cluster would be the classic port bug.
    /// </remarks>
    private static readonly HashSet<string> Marks =
    [
        ".", ",", "!", "?", ":", ";", "…",
        "«", "»", "\"", "'", "(", ")", "—", "–",
    ];

    /// <summary>Split the text into fields. Indexes count grapheme clusters, never UTF-16 units.</summary>
    internal static List<Field> Fields(string text)
    {
        List<string> characters = Characters(text);
        var result = new List<Field>();
        int index = 0;

        while (index < characters.Count)
        {
            string spacing = string.Empty;
            while (index < characters.Count && IsWhitespace(characters[index]))
            {
                spacing += characters[index];
                index++;
            }

            if (index >= characters.Count)
            {
                // Trailing spaces are attached to the last field so that reassembly returns
                // the text character for character.
                if (spacing.Length != 0 && result.Count != 0)
                {
                    Field last = result[^1];
                    last.Trail += spacing;
                    result[^1] = last;
                }

                break;
            }

            var token = new System.Text.StringBuilder();
            while (index < characters.Count && !IsWhitespace(characters[index]))
            {
                token.Append(characters[index]);
                index++;
            }

            result.Add(Split(token.ToString(), spacing));
        }

        return result;
    }

    private static Field Split(string token, string spacingBefore)
    {
        List<string> characters = Characters(token);
        var lead = new System.Text.StringBuilder();
        var trail = new System.Text.StringBuilder();

        int first = 0;
        int last = characters.Count - 1;
        while (first <= last && Marks.Contains(characters[first]))
        {
            lead.Append(characters[first]);
            first++;
        }

        while (last >= first && Marks.Contains(characters[last]))
        {
            trail.Insert(0, characters[last]);
            last--;
        }

        // A field made entirely of marks is a "word" in its own right (a dash acting as a
        // link). Moving it into lead and trail would lose it during alignment.
        if (first > last)
        {
            return new Field(spacingBefore, string.Empty, token, string.Empty);
        }

        return new Field(
            spacingBefore,
            lead.ToString(),
            string.Concat(characters.GetRange(first, last - first + 1)),
            trail.ToString());
    }

    /// <summary>Swift's <c>Array(text)</c>: grapheme clusters, not UTF-16 units.</summary>
    private static List<string> Characters(string text)
    {
        var characters = new List<string>(text.Length);
        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            characters.Add((string)enumerator.Current);
        }

        return characters;
    }

    /// <summary>
    /// Swift's <c>Character.isWhitespace</c>: a cluster is whitespace when the character it
    /// starts with is.
    /// </summary>
    private static bool IsWhitespace(string character) =>
        character.Length > 0 && char.IsWhiteSpace(character, 0);

    // MARK: - Alignment

    internal enum StepKind
    {
        Match,
        Substitution,
        Deletion,
        Insertion,
    }

    /// <summary>
    /// One alignment step. <see cref="Left"/> indexes the original's fields,
    /// <see cref="Right"/> the rescorer's; the unused side is -1.
    /// </summary>
    /// <remarks>
    /// The Swift original is an <c>enum</c> with associated values. A record hierarchy would
    /// allocate one object per field here, and this runs after the key is released on texts
    /// of thousands of words - so the cases live in one value type instead.
    /// </remarks>
    internal readonly record struct Step(StepKind Kind, int Left, int Right)
    {
        public static Step Match(int left, int right) => new(StepKind.Match, left, right);

        public static Step Substitution(int left, int right) => new(StepKind.Substitution, left, right);

        public static Step Deletion(int left) => new(StepKind.Deletion, left, -1);

        public static Step Insertion(int right) => new(StepKind.Insertion, -1, right);
    }

    /// <summary>
    /// Levenshtein over fields, restricted to a diagonal band.
    ///
    /// The full matrix is what this used to be, and it is unaffordable here: this runs
    /// synchronously inside <c>transcribe</c>, after the person has already released the key
    /// and is waiting. Measured on the old code - 4000 words 7.1 s, 8000 words 28.4 s and a
    /// 488 MB matrix on top of the ~500 MB model. An hour-long note, which the app explicitly
    /// allows, froze it for half a minute with nothing on screen.
    ///
    /// The band is exact, not an approximation. A path can only leave a band of half-width
    /// <c>k</c> by accumulating more than <c>k</c> edits, so a result whose total cost is
    /// <c>&lt;= k</c> is provably the same answer the full matrix gives. When the cost reaches
    /// the edge we double the band and try again, ending at full width in the worst case - so
    /// the answer never changes, only the work needed to reach it.
    ///
    /// This suits the actual input: the two texts differ by the handful of words the rescorer
    /// substituted, so the first band almost always wins.
    /// </summary>
    internal static List<Step> Align(List<Field> left, List<Field> right)
    {
        int rows = left.Count;
        int columns = right.Count;
        if (rows == 0)
        {
            return Enumerable.Range(0, columns).Select(Step.Insertion).ToList();
        }

        if (columns == 0)
        {
            return Enumerable.Range(0, rows).Select(Step.Deletion).ToList();
        }

        // Lowercasing inside the loop cost two string allocations per cell - n² of them, and
        // the single biggest constant in the old profile.
        string[] leftKeys = left.Select(field => field.Core.ToLowerInvariant()).ToArray();
        string[] rightKeys = right.Select(field => field.Core.ToLowerInvariant()).ToArray();

        int band = Math.Max(16, Math.Abs(rows - columns) + 8);
        int widest = Math.Max(rows, columns);
        long cellsSpent = 0;
        while (true)
        {
            long cells = CellCount(rows, columns, band);
            if (cells > AlignmentCellBudget - cellsSpent)
            {
                break;
            }

            cellsSpent += cells;
            if (AlignWithinBand(leftKeys, rightKeys, band) is { } steps)
            {
                return steps;
            }

            if (band >= widest)
            {
                break;
            }

            band = Math.Min(band * 2, widest);
        }

        // Too far apart to align affordably. `Restore` reads an empty result as "leave the
        // rescorer's text alone", which is the same thing the 0.5 fuse already does when the
        // two sides disagree too much: losing some punctuation beats freezing after the key
        // was released.
        return [];
    }

    /// <summary>
    /// How much total alignment work may happen before it stops being worth it.
    ///
    /// The band widens whenever the edit cost reaches its edge, and cost counts
    /// substitutions - so a long text the rescorer changed in many places pushes the band up
    /// proportionally, and the cost goes back to quadratic with a smaller constant. Measured:
    /// 4000 words with a substitution every 50 took a band of 128, and 8000 words took 256,
    /// so doubling the input still quadrupled the time.
    ///
    /// The product cannot afford that after the key is released, so the budget covers cells
    /// across every widening attempt, not only the final matrix. Four million cells is about
    /// 16 MB of writes and a fraction of a second; beyond it the answer is not worth waiting
    /// for.
    /// </summary>
    private const long AlignmentCellBudget = 4_000_000;

    private static long CellCount(int rows, int columns, int band)
    {
        int drift = Math.Abs(columns - rows);
        long width = Math.Min((long)columns + 1, (long)drift + ((long)band * 2) + 1);
        return (rows + 1L) * width;
    }

    /// <summary>
    /// One banded pass. Returns <c>null</c> when the cost reached the band edge and the
    /// answer may therefore be inexact.
    /// </summary>
    private static List<Step>? AlignWithinBand(string[] leftKeys, string[] rightKeys, int band)
    {
        int rows = leftKeys.Length;
        int columns = rightKeys.Length;
        int drift = columns - rows;
        int lowestDiagonal = Math.Min(0, drift) - band;
        int highestDiagonal = Math.Max(0, drift) + band;
        int rowWidth = highestDiagonal - lowestDiagonal + 1;
        // Anything at or above this is "outside the band"; adding to it must not overflow,
        // so it stays far below int.MaxValue.
        int unreachable = rows + columns + 1;

        int[] cost = new int[(rows + 1) * rowWidth];
        Array.Fill(cost, unreachable);

        // Column index for a cell, or -1 when the cell lies outside the band.
        int Slot(int row, int column)
        {
            int offset = column - (row + lowestDiagonal);
            return offset >= 0 && offset < rowWidth ? (row * rowWidth) + offset : -1;
        }

        int Value(int row, int column)
        {
            if (row < 0 || column < 0)
            {
                return unreachable;
            }

            int index = Slot(row, column);
            return index < 0 ? unreachable : cost[index];
        }

        int start = Slot(0, 0);
        if (start >= 0)
        {
            cost[start] = 0;
        }

        for (int row = 0; row <= rows; row++)
        {
            int first = Math.Max(0, row + lowestDiagonal);
            int last = Math.Min(columns, row + highestDiagonal);
            if (first > last)
            {
                continue;
            }

            for (int column = first; column <= last; column++)
            {
                if (row == 0 && column == 0)
                {
                    continue;
                }

                int index = Slot(row, column);
                if (index < 0)
                {
                    continue;
                }

                int best = unreachable;
                if (row > 0 && column > 0)
                {
                    bool same = leftKeys[row - 1] == rightKeys[column - 1];
                    int diagonal = Value(row - 1, column - 1);
                    if (diagonal < unreachable)
                    {
                        best = Math.Min(best, diagonal + (same ? 0 : 1));
                    }
                }

                if (row > 0)
                {
                    int above = Value(row - 1, column);
                    if (above < unreachable)
                    {
                        best = Math.Min(best, above + 1);
                    }
                }

                if (column > 0)
                {
                    int leftCost = Value(row, column - 1);
                    if (leftCost < unreachable)
                    {
                        best = Math.Min(best, leftCost + 1);
                    }
                }

                cost[index] = best;
            }
        }

        int total = Value(rows, columns);
        // A cost at or beyond the half-width means the optimal path could have stepped
        // outside what we computed. Widen rather than answer wrongly.
        if (total >= unreachable || total > band)
        {
            return null;
        }

        var steps = new List<Step>();
        int currentRow = rows;
        int currentColumn = columns;
        while (currentRow > 0 || currentColumn > 0)
        {
            if (currentRow > 0 && currentColumn > 0)
            {
                bool same = leftKeys[currentRow - 1] == rightKeys[currentColumn - 1];
                if (Value(currentRow, currentColumn)
                    == Value(currentRow - 1, currentColumn - 1) + (same ? 0 : 1))
                {
                    steps.Add(same
                        ? Step.Match(currentRow - 1, currentColumn - 1)
                        : Step.Substitution(currentRow - 1, currentColumn - 1));
                    currentRow--;
                    currentColumn--;
                    continue;
                }
            }

            if (currentRow > 0 && Value(currentRow, currentColumn) == Value(currentRow - 1, currentColumn) + 1)
            {
                steps.Add(Step.Deletion(currentRow - 1));
                currentRow--;
                continue;
            }

            if (currentColumn > 0)
            {
                steps.Add(Step.Insertion(currentColumn - 1));
                currentColumn--;
                continue;
            }

            // Defensive: the traceback must always be able to move. Bailing out here beats
            // looping forever on an unexpected matrix.
            return null;
        }

        steps.Reverse();
        return steps;
    }
}
