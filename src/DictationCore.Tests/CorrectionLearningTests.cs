using System.Diagnostics;
using System.Text;

namespace DictationCore.Tests;

/// <summary>
/// Rules for learning a dictionary from the edits of the last dictation.
///
/// A person edits the text in our window - the diff suggests replacements. The rules are
/// intentionally conservative: learning garbage is worse than learning nothing,
/// because a bad replacement breaks future dictations silently.
/// </summary>
public sealed class CorrectionLearningTests
{
    [Fact]
    public void TestScenario001()
    {
        // Main scenario: the model heard the term in Cyrillic, the human
        // corrected it to Latin. This is exactly what should go into the dictionary.
        IReadOnlyList<DictionaryReplacement> proposals = CorrectionLearning.Propose(
            original: "Открой поуст герз и проверь индексы.",
            edited: "Открой Postgres и проверь индексы.");

        Assert.Equal(["поуст герз"], proposals.Select(proposal => proposal.Spoken));
        Assert.Equal(["Postgres"], proposals.Select(proposal => proposal.Written));
    }

    [Fact]
    public void TestScenario002()
    {
        // "Быстро" → "быстрее" is an edit of speech, not a term. Learning this
        // means replacing a person's words in subsequent dictations.
        IReadOnlyList<DictionaryReplacement> proposals = CorrectionLearning.Propose(
            original: "Сделай это быстро и аккуратно.",
            edited: "Сделай это быстрее и аккуратно.");

        Assert.Empty(proposals);
    }

    [Fact]
    public void TestScenario003()
    {
        // "github" → "GitHub" - a brand spelling. This is a dictionary correction.
        IReadOnlyList<DictionaryReplacement> proposals = CorrectionLearning.Propose(
            original: "Загрузи в github ветку.",
            edited: "Загрузи в GitHub ветку.");

        Assert.Equal(["github"], proposals.Select(proposal => proposal.Spoken));
        Assert.Equal(["GitHub"], proposals.Select(proposal => proposal.Written));
    }

    [Fact]
    public void TestScenario004()
    {
        DictionaryReplacement[] existing =
        [
            new(spoken: "поуст герз", written: "Postgres"),
        ];

        IReadOnlyList<DictionaryReplacement> proposals = CorrectionLearning.Propose(
            original: "Открой поуст герз сейчас.",
            edited: "Открой Postgres сейчас.",
            existing: existing);

        Assert.Empty(proposals);
    }

    [Fact]
    public void TestScenario005()
    {
        IReadOnlyList<DictionaryReplacement> proposals = CorrectionLearning.Propose(
            original: "Скинь пул реквест в гит хаб.",
            edited: "Скинь pull request в GitHub.");

        Assert.Equal(2, proposals.Count);
        Assert.Equal("пул реквест", proposals[0].Spoken);
        Assert.Equal("pull request", proposals[0].Written);
        Assert.Equal("гит хаб", proposals[1].Spoken);
        Assert.Equal("GitHub", proposals[1].Written);
    }

    [Fact]
    public void TestScenario006()
    {
        IReadOnlyList<DictionaryReplacement> proposals = CorrectionLearning.Propose(
            original: "Ну вот сделай это сейчас.",
            edited: "Сделай это сейчас.");

        Assert.Empty(proposals);
    }

    [Fact]
    public void TestScenario007()
    {
        Assert.Empty(CorrectionLearning.Propose(original: "", edited: ""));
        Assert.Empty(CorrectionLearning.Propose(
            original: "Один и тот же текст.",
            edited: "Один и тот же текст."));
    }

    [Fact]
    public void TestScenario008()
    {
        // The same prohibition as for "быстро" → "быстрее", but in a text where
        // the Latin alphabet is on both sides. Previously, the sign of the term was considered
        // "there is Latin on the right" - in an English text it did not mean anything, and
        // any word edits fell through here.
        (string Original, string Edited)[] cases =
        [
            ("please send me the file", "please send me a file"),
            ("the quick brown fox jumps", "the slow brown fox jumps"),
            ("I went to the store today", "I ran to the store today"),
        ];

        foreach ((string original, string edited) in cases)
        {
            Assert.True(
                CorrectionLearning.Propose(original: original, edited: edited).Count == 0,
                $"Правка речи «{original}» → «{edited}» не должна становиться заменой");
        }
    }

    [Fact]
    public void TestScenario009()
    {
        // The cost of the error the filter exists for: one edit of "the" →
        // "a" rewrote every subsequent dictation.
        IReadOnlyList<DictionaryReplacement> learned = CorrectionLearning.Propose(
            original: "please send me the file",
            edited: "please send me a file");
        string text = new TextPipeline(replacements: learned)
            .Process("The report is on the desk").Text;

        Assert.Equal("The report is on the desk", text);
    }

    [Fact]
    public void TestScenario010()
    {
        // Rearranging capitalization when moving words inside a paragraph gave the pair
        // "The" → "the" along with its opposite "the" → "The": two
        // rules that argue with each other in every future dictation.
        IReadOnlyList<DictionaryReplacement> proposals = CorrectionLearning.Propose(
            original: "The file is ready. the build passed.",
            edited: "the file is ready. The build passed.");

        Assert.Empty(proposals);
    }

    [Fact]
    public void TestScenario011()
    {
        // There is formally a change of writing, but there is no term, while a change
        // throughout the text costs more than any benefit.
        IReadOnlyList<DictionaryReplacement> proposals = CorrectionLearning.Propose(
            original: "привет мир",
            edited: "привет a");

        Assert.Empty(proposals);
    }

    [Fact]
    public void TestScenario012()
    {
        // The only sign of a term that works in a text that is entirely
        // in Latin: a capital not at the beginning of the word.
        (string Spoken, string Written)[] cases = [("api", "API"), ("iphone", "iPhone"), ("mac os", "macOS")];
        foreach ((string spoken, string written) in cases)
        {
            IReadOnlyList<DictionaryReplacement> proposals = CorrectionLearning.Propose(
                original: $"open {spoken} now",
                edited: $"open {written} now");
            Assert.Equal([written], proposals.Select(proposal => proposal.Written));
        }
    }

    [Fact]
    public void TestScenario013()
    {
        IReadOnlyList<DictionaryReplacement> proposals = CorrectionLearning.Propose(
            original: "открой сентри и закрой сентри",
            edited: "открой Sentry и закрой Sentry");

        // A duplicate in the dictionary a person would have to delete separately.
        Assert.Single(proposals);
        Assert.Equal(["сентри"], proposals.Select(proposal => proposal.Spoken));
    }

    [Fact]
    public void TestScenario014()
    {
        // Not an edit of terms, but a different text: the anchor words are in place, but
        // everything between them is replaced. Establishing so many silent rules at once is
        // irreversible - it is more honest to learn nothing.
        var original = new StringBuilder();
        var edited = new StringBuilder();
        for (int index = 0; index < 40; index += 1)
        {
            original.Append($"слово{index} sep ");
            edited.Append($"Term{index}X sep ");
        }

        Assert.Empty(CorrectionLearning.Propose(original: original.ToString(), edited: edited.ToString()));
    }

    [Fact]
    public void TestScenario015()
    {
        // The border should let a real edit through: five terms in a row is
        // still an edit, not a replacement of text.
        var original = new StringBuilder();
        var edited = new StringBuilder();
        for (int index = 0; index < CorrectionLearning.MaximumProposalsPerCorrection; index += 1)
        {
            original.Append($"термин{index} sep ");
            edited.Append($"Term{index}X sep ");
        }

        Assert.Equal(
            CorrectionLearning.MaximumProposalsPerCorrection,
            CorrectionLearning.Propose(original: original.ToString(), edited: edited.ToString()).Count);
    }

    [Fact]
    public void TestScenario016()
    {
        // "сентри" → "центре"-like pairs without Latin letters do not pass the filter:
        // without the "this is a term" signal, the chance of learning an ordinary edit is too high.
        IReadOnlyList<DictionaryReplacement> proposals = CorrectionLearning.Propose(
            original: "Мы посмотрели в сентри вчера.",
            edited: "Мы посмотрели в центре вчера.");

        Assert.Empty(proposals);
    }

    [Fact]
    public void TestScenario017()
    {
        // An edit of thousands of words is not a term correction, and the diff
        // behind it is quadratic in both time and memory - and it runs on the
        // MainActor right after the insertion, while the person is still typing.
        // Measurement before the ceiling: 2000 words - 0.92 s and 31 MB, 4000 -
        // 3.36 s and 125 MB, 6000 - 7.50 s and 282 MB. The limit on the number of
        // proposals saved nothing: it is applied to the finished diff.
        var original = new StringBuilder();
        var edited = new StringBuilder();
        for (int index = 0; index < 6000; index += 1)
        {
            original.Append($"слово{index} ");
            edited.Append(index == 0 ? "GitHub " : $"слово{index} ");
        }

        var started = Stopwatch.StartNew();
        IReadOnlyList<DictionaryReplacement> proposals =
            CorrectionLearning.Propose(original: original.ToString(), edited: edited.ToString());
        TimeSpan spent = started.Elapsed;

        // There is nothing to learn from a rewritten text.
        Assert.Empty(proposals);
        // The bound is deliberately loose: this is not a benchmark, but a check
        // that the work is refused before it starts, and not after it.
        Assert.True(
            spent < TimeSpan.FromSeconds(2),
            $"Разбор правки идёт на MainActor и не имеет права подвешивать интерфейс");
    }

    [Fact]
    public void TestScenario018()
    {
        // The ceiling must not cut off a real edit: a long dictation with one
        // corrected term is still a lesson for the dictionary. One word further -
        // and there is nothing left to learn.
        static IReadOnlyList<DictionaryReplacement> Correction(int words)
        {
            var original = new StringBuilder();
            var edited = new StringBuilder();
            for (int index = 0; index < words; index += 1)
            {
                original.Append($"слово{index} ");
                edited.Append(index == 0 ? "GitHub " : $"слово{index} ");
            }

            return CorrectionLearning.Propose(original: original.ToString(), edited: edited.ToString());
        }

        Assert.Equal(
            ["GitHub"],
            Correction(CorrectionLearning.MaximumWordsPerCorrection).Select(proposal => proposal.Written));
        Assert.Empty(Correction(CorrectionLearning.MaximumWordsPerCorrection + 1));
    }
}
