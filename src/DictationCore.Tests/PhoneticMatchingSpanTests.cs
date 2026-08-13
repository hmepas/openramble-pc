namespace DictationCore.Tests;

/// <summary>
/// Splitting the dictionary into exact and phonetic passes.
///
/// A service split: span detection comes between the passes. Therefore
/// the main thing here is to prove that the split itself did not change anything.
/// </summary>
public sealed class DictionaryPassSplitTests
{
    private readonly DictionaryReplacement[] dictionary =
    [
        new(spoken: "поуст герз", written: "Postgres", inflects: false),
        new(spoken: "код", written: "code", inflects: false),
        new(spoken: "пул реквест", written: "pull request", inflects: false),
    ];

    [Fact]
    public void TestExactPassAloneEqualsThePipelineWithPhoneticOff()
    {
        string[] corpus =
        [
            "открой поуст герз и проверь",
            "это код, а это кодировка",
            "сделай пул реквест сейчас",
            "обычная русская фраза без терминов",
            "",
        ];
        foreach (string text in corpus)
        {
            // The split has no right to change the exact pass.
            Assert.Equal(
                DictionaryReplacements.Apply(dictionary, text, phoneticMatching: false),
                DictionaryReplacements.ApplyExact(dictionary, text));
        }
    }

    [Fact]
    public void TestFacadeStillEqualsExactThenPhonetic()
    {
        string text = "открой поуст герз и сделай пул реквест";
        string manual = DictionaryReplacements.ApplyPhonetic(
            dictionary,
            DictionaryReplacements.ApplyExact(dictionary, text),
            []);
        Assert.Equal(manual, DictionaryReplacements.Apply(dictionary, text));
    }

    /// <summary>
    /// An empty set of spans must give exactly the same result.
    ///
    /// The matcher is greedy, and its behavior is emergent: a shift here would not be noticeable
    /// to the eye, but would ruin every second dictation. We race on the full
    /// dictionary, and not on three pairs.
    /// </summary>
    [Fact]
    public void TestEmptySpanSetChangesNothing()
    {
        IReadOnlyList<DictionaryReplacement> starter = StarterDictionary.Developer;
        string[] corpus =
        [
            "надо сделать деплой на сервер",
            "открой поуст герз и посмотри в центре экрана",
            "коммит уже готов, а комета нет",
            "джава скрипт и джаваскрипт — одно и то же",
            "у нас даун тайм с утра",
            "Привет, как дела? Сегодня хорошая погода.",
        ];
        foreach (string text in corpus)
        {
            // The path without spans must be indistinguishable from the previous one.
            Assert.Equal(
                PhoneticMatching.Apply(starter, text),
                PhoneticMatching.Apply(starter, text, []));
        }
    }
}

/// <summary>Phonetic selection and protected spans are collision fixtures.</summary>
public sealed class PhoneticMatchingSpanTests
{
    /// <summary>"сентри" is phonetically close to Sentry - this makes the fixture a collision.</summary>
    private readonly DictionaryReplacement[] dictionary =
    [
        new(spoken: "сентри", written: "Sentry", inflects: false),
    ];

    private string Phonetic(string text)
    {
        IReadOnlyList<ProtectedSpan> spans = ProtectedSpanDetector.Detect(text);
        return DictionaryReplacements.ApplyPhonetic(dictionary, text, spans);
    }

    /// <summary>Collision 1: dictionary term inside backticks.</summary>
    [Fact]
    public void TestPhoneticDoesNotFireInsideBackticks()
    {
        string result = Phonetic("смотри `сентри` и просто сентри");
        // Inside the backticks it must stay as is.
        Assert.Contains("`сентри`", result, StringComparison.Ordinal);
        // Outside - it must be replaced.
        Assert.Contains("просто Sentry", result, StringComparison.Ordinal);
    }

    /// <summary>Collision 2: phonetic neighbour inside a path.</summary>
    [Fact]
    public void TestPhoneticDoesNotFireInsideAPath()
    {
        string result = Phonetic("лежит в /Users/mik/сентри/log");
        Assert.Equal("лежит в /Users/mik/сентри/log", result);
    }

    /// <summary>
    /// Collision 3: the same two pieces, but in brackets.
    ///
    /// A quotation mark or a bracket before the piece used to disable the
    /// protection entirely, and the selection worked right inside the path and
    /// inside the backticks - exactly where a person dictated the writing verbatim.
    /// </summary>
    [Fact]
    public void TestPhoneticDoesNotFireInsideABracketedPathOrBackticks()
    {
        Assert.Equal(
            "лежит в (/Users/mik/сентри/log)",
            Phonetic("лежит в (/Users/mik/сентри/log)"));
        Assert.Equal(
            "смотри (`сентри`) тут",
            Phonetic("смотри (`сентри`) тут"));
    }

    /// <summary>A window of three words has no right to step over a span.</summary>
    [Fact]
    public void TestPhoneticWindowNeverStraddlesASpanBoundary()
    {
        DictionaryReplacement[] terms =
        [
            new(spoken: "даун тайм", written: "downtime", inflects: false),
        ];
        string text = "был даун `код` тайм вчера";
        IReadOnlyList<ProtectedSpan> spans = ProtectedSpanDetector.Detect(text);
        string result = DictionaryReplacements.ApplyPhonetic(terms, text, spans);
        // A term is not assembled through a protected piece.
        Assert.Equal(text, result);
    }

    /// <summary>
    /// Exact replacement is the explicit will of a person, and it is above the protection of a span.
    ///
    /// It works not by a priority rule, but by order: the exact pass is processed
    /// before the spans are even counted.
    /// </summary>
    [Fact]
    public void TestExactReplacementOutranksSpanProtection()
    {
        string text = "смотри `сентри` тут";
        string afterExact = DictionaryReplacements.ApplyExact(dictionary, text);
        Assert.Equal("смотри `Sentry` тут", afterExact);
    }

    /// <summary>Spans are recalculated after the exact replacement, and are not taken from the raw text.</summary>
    [Fact]
    public void TestSpansAreRecomputedAfterTheExactPass()
    {
        DictionaryReplacement[] terms =
        [
            new(spoken: "путь", written: "/etc/hosts", inflects: false),
        ];
        string afterExact = DictionaryReplacements.ApplyExact(terms, "открой путь скорее");
        Assert.Equal("открой /etc/hosts скорее", afterExact);
        // The replacement gave birth to a path - it is obliged to become a span.
        Assert.Equal(
            [ProtectedSpanKind.Path],
            ProtectedSpanDetector.Detect(afterExact).Select(span => span.Kind));
    }
}
