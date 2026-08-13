using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DictationCore;

namespace AsrBench;

/// <summary>One corpus entry: the file and what it actually says.</summary>
internal sealed record EvalItem
{
    [JsonConstructor]
    public EvalItem(string file, string reference, IReadOnlyList<string>? tags = null)
    {
        File = file;
        Reference = reference;
        Tags = tags ?? [];
    }

    [JsonPropertyName("file")]
    public string File { get; }

    [JsonPropertyName("reference")]
    public string Reference { get; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; }
}

/// <summary>The total for one recording.</summary>
internal sealed record EvalOutcome(
    EvalItem Item,
    ScoreReport Report,
    ASRResult Result,
    TimeSpan WallClock,
    long PeakMemory);

internal static class Evaluation
{
    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    internal static IReadOnlyList<EvalItem> LoadManifest(string path)
    {
        // Swift reads it through `LocalFile`, which refuses an http address: `Data(contentsOf:)`
        // would accept one and silently go online. Here the path only ever reaches
        // System.IO, which has no idea what a URL is - the same guarantee, for free.
        byte[] data = System.IO.File.ReadAllBytes(path);
        return JsonSerializer.Deserialize<List<EvalItem>>(data, ManifestOptions) ?? [];
    }

    internal static string Percent(double value) =>
        (value * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";

    internal static string Describe(EvalOutcome outcome, bool showDifferences)
    {
        ScoreReport report = outcome.Report;
        var lines = new List<string>
        {
            $"\n=== {Path.GetFileName(outcome.Item.File)} [{string.Join(",", outcome.Item.Tags)}] ===",
            $"raw response: {outcome.Result.Text}",
            $"reference: {report.Reference.Text}",
            $"received: {report.Hypothesis.Text}",
            string.Format(
                CultureInfo.InvariantCulture,
                "WER {0} ({1} sub {2} del {3} ins of {4}) CER {5} WER+marks {6} marks {7}/{8}",
                Percent(report.Words.Rate),
                report.Words.Substitutions,
                report.Words.Deletions,
                report.Words.Insertions,
                report.Words.ReferenceLength,
                Percent(report.CharacterErrorRate),
                Percent(report.WordsWithPunctuation.Rate),
                report.Punctuation.Correct,
                report.Punctuation.ReferenceLength),
        };

        double realtime = outcome.WallClock > TimeSpan.Zero
            ? outcome.Result.AudioDuration / outcome.WallClock
            : 0;
        lines.Add(string.Format(
            CultureInfo.InvariantCulture,
            "audio {0:F1} s, recognition {1:F2} s - {2:F0}x real time, words with timings {3}",
            outcome.Result.AudioDuration.TotalSeconds,
            outcome.WallClock.TotalSeconds,
            realtime,
            outcome.Result.Words.Count));

        if (showDifferences && report.Differences.Count > 0)
        {
            string shown = string.Join(
                "; ",
                report.Differences.Take(25).Select(difference => difference.Description));
            string tail = report.Differences.Count > 25 ? $"...total {report.Differences.Count}" : "";
            lines.Add($"divergences: {shown}{tail}");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// The group summary: comparing scenarios against each other is the only conclusion that
    /// a small corpus allows one to draw at all.
    /// </summary>
    internal static string Summary(IReadOnlyList<EvalOutcome> outcomes)
    {
        var byTag = new Dictionary<string, List<EvalOutcome>>(StringComparer.Ordinal);
        foreach (EvalOutcome outcome in outcomes)
        {
            foreach (string tag in outcome.Item.Tags)
            {
                if (!byTag.TryGetValue(tag, out List<EvalOutcome>? group))
                {
                    group = [];
                    byTag[tag] = group;
                }

                group.Add(outcome);
            }
        }

        static string Pad(string text, int width) =>
            text.Length >= width ? text : text + new string(' ', width - text.Length);

        var lines = new List<string>
        {
            "\n=== Group summary ===",
            Pad("group", 16) + " entries  words  WER     CER     WER+marks marks",
        };

        foreach (string tag in byTag.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            List<EvalOutcome> group = byTag[tag];
            ScoreReport.Counts words = Total(group, outcome => outcome.Report.Words);
            ScoreReport.Counts tokens = Total(group, outcome => outcome.Report.WordsWithPunctuation);
            ScoreReport.Counts marks = Total(group, outcome => outcome.Report.Punctuation);

            // CER is weighted by the length of the reference, otherwise a short recording
            // would weigh as much as a half-hour one.
            double errors = 0;
            double characters = 0;
            foreach (EvalOutcome outcome in group)
            {
                double length = outcome.Report.Reference.Characters.Count;
                errors += outcome.Report.CharacterErrorRate * length;
                characters += length;
            }

            double cer = characters > 0 ? errors / characters : 0;

            lines.Add(
                Pad(tag, 16)
                + string.Format(CultureInfo.InvariantCulture, " {0,7} {1,6}  ", group.Count, words.ReferenceLength)
                + Pad(Percent(words.Rate), 8)
                + Pad(Percent(cer), 8)
                + Pad(Percent(tokens.Rate), 10)
                + $"{marks.Correct}/{marks.ReferenceLength}");
        }

        return string.Join("\n", lines);
    }

    private static ScoreReport.Counts Total(
        IEnumerable<EvalOutcome> group,
        Func<EvalOutcome, ScoreReport.Counts> select)
    {
        var total = default(ScoreReport.Counts);
        foreach (EvalOutcome outcome in group)
        {
            ScoreReport.Counts counts = select(outcome);
            total.Substitutions += counts.Substitutions;
            total.Deletions += counts.Deletions;
            total.Insertions += counts.Insertions;
            total.Correct += counts.Correct;
        }

        return total;
    }
}
