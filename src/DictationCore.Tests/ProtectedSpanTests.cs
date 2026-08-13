using System.Globalization;

namespace DictationCore.Tests;

/// <summary>
/// Protected spans: pieces of text that no one touches after the dictionary.
///
/// The rules are intentionally conservative. A false span suppresses the dictionary and the polisher
/// silently - a person sees an uncorrected term and does not understand why. A missed
/// span looks worse, but can be repaired with a dictionary. Therefore, wherever the choice is not obvious,
/// "not a span" is selected.
/// </summary>
public sealed class ProtectedSpanDetectorTests
{
    private static ProtectedSpanKind[] Kinds(string text) =>
        ProtectedSpanDetector.Detect(text).Select(span => span.Kind).ToArray();

    private static string[] Texts(string text) =>
        ProtectedSpanDetector.Detect(text).Select(span => span.Text).ToArray();

    // MARK: - Backticks

    [Fact]
    public void TestBacktickedFragmentIsOneSpanIncludingTheDelimiters()
    {
        Assert.Equal(["`swift test`"], Texts("запусти `swift test` сейчас"));
        Assert.Equal([ProtectedSpanKind.Backticks], Kinds("запусти `swift test` сейчас"));
    }

    /// <summary>An unclosed backtick is dictation, not code.</summary>
    [Fact]
    public void TestUnpairedBacktickIsNotASpan()
    {
        Assert.Empty(Texts("тут просто `бэктик и всё"));
    }

    /// <summary>Nothing is searched inside backticks: the code is already there, and it is completely protected.</summary>
    [Fact]
    public void TestBacktickSpanSwallowsEverythingInside()
    {
        var spans = ProtectedSpanDetector.Detect("`TextPipeline.Output --flag /etc/hosts`");
        Assert.Single(spans);
        Assert.Equal(ProtectedSpanKind.Backticks, spans[0].Kind);
    }

    /// <summary>
    /// A backtick behind a quotation mark or a bracket is still a backtick.
    ///
    /// The scan jumped straight to the end of the token, so a pair that did not
    /// open the token was never looked at at all: the polisher collapsed the
    /// spaces inside <c>"`a  b`"</c>, and phonetic matching fired inside the backticks.
    /// </summary>
    [Fact]
    public void TestBacktickSpanIsFoundBehindAQuoteOrBracket()
    {
        Assert.Equal(["`a  b`"], Texts("напиши \"`a  b`\" вот так"));
        Assert.Equal(["`диплой`"], Texts("(`диплой`)"));
        Assert.Equal([ProtectedSpanKind.Backticks], Kinds("(`диплой`)"));
    }

    // MARK: - Paths

    [Fact]
    public void TestAbsoluteAndHomePathsAreSpans()
    {
        Assert.Equal(["/etc/hosts"], Texts("открой /etc/hosts"));
        Assert.Equal(["~/Documents/Code"], Texts("лежит в ~/Documents/Code"));
        Assert.Equal([ProtectedSpanKind.Path], Kinds("открой /etc/hosts"));
    }

    /// <summary>
    /// One slash between regular words is an "or", not a path.
    ///
    /// "and/or" and "24/7" are found in live speech much more often than a path of two
    /// bare segments, so either a second slash or a dot/hyphen is required
    /// inside a segment.
    /// </summary>
    [Fact]
    public void TestRelativePathNeedsMoreThanASingleSlash()
    {
        Assert.Equal(["src/main.swift"], Texts("правь src/main.swift"));
        Assert.Equal(["a/b/c"], Texts("путь a/b/c тоже"));
        Assert.Empty(Texts("это и/или то"));
        Assert.Empty(Texts("работает 24/7"));
        Assert.Empty(Texts("формат and/or"));
    }

    /// <summary>The period at the end of the phrase belongs to the phrase, not to the path.</summary>
    [Fact]
    public void TestTrailingSentencePunctuationStaysOutsideThePath()
    {
        Assert.Equal(["/etc/hosts"], Texts("открой /etc/hosts."));
        Assert.Equal(["src/main.swift"], Texts("смотри src/main.swift, там"));
    }

    [Fact]
    public void TestUrlIsOneSpanIncludingTheScheme()
    {
        Assert.Equal(
            ["https://wai.computer/openramble/"],
            Texts("зайди на https://wai.computer/openramble/"));
    }

    // MARK: - Flags

    [Fact]
    public void TestLongAndShortFlagsAreSpans()
    {
        Assert.Equal(["--dry-run"], Texts("добавь --dry-run"));
        Assert.Equal(["-x"], Texts("ключ -x поставь"));
        Assert.Equal([ProtectedSpanKind.Flag], Kinds("добавь --dry-run"));
    }

    [Fact]
    public void TestLongFlagCarriesItsValue()
    {
        Assert.Equal(["--out=build/app"], Texts("передай --out=build/app"));
    }

    /// <summary>
    /// A hyphen inside a word does not make a flag: there is a letter before it.
    ///
    /// The Russian dash-hyphen in "word - word" is especially important: there is a space next to it,
    /// and the rule does not apply to it.
    /// </summary>
    [Fact]
    public void TestHyphenInsideAWordIsNotAFlag()
    {
        Assert.Empty(Texts("это well-known вещь"));
        Assert.Empty(Texts("пишем тайп-скрипт"));
        Assert.Empty(Texts("минус -5 градусов"));
        Assert.Empty(Texts("Москва — столица"));
        Assert.Empty(Texts("просто -- и всё"));
    }

    // MARK: - Identifiers

    /// <summary>CamelCase is a transition from lowercase to uppercase. An abbreviation is not one.</summary>
    [Fact]
    public void TestCamelCaseIsASpanButAllCapsIsNot()
    {
        Assert.Equal(["TextPipeline"], Texts("правь TextPipeline"));
        Assert.Equal(["onTextInserted"], Texts("колбэк onTextInserted"));
        Assert.Equal(["URLSession"], Texts("класс URLSession"));
        Assert.Empty(Texts("отдай API"));
        Assert.Empty(Texts("формат JSON и HTTP"));
    }

    /// <summary>
    /// A dotted identifier needs a capital letter in at least one segment -
    /// otherwise domains and versions would become spans and the dictionary would not touch them.
    /// </summary>
    [Fact]
    public void TestDottedIdentifierNeedsAnUppercaseSegment()
    {
        Assert.Equal(["TextPipeline.Output"], Texts("тип TextPipeline.Output"));
        Assert.Equal(["AppState.shared"], Texts("зови AppState.shared"));
        Assert.Empty(Texts("сайт wai.computer"));
        Assert.Empty(Texts("версия 2.0.1"));
        Assert.Empty(Texts("и т.д. дальше"));
    }

    /// <summary>
    /// A known and accepted cost of the rule: a glued English phrase looks
    /// like an identifier and remains glued. The Cyrillic alphabet is not affected - the rule
    /// is only about ASCII, so the polisher will still break "Done.Next".
    /// </summary>
    [Fact]
    public void TestGluedEnglishSentenceIsProtectedAndThatIsTheKnownCost()
    {
        Assert.Equal(["Done.Next"], Texts("Done.Next"));
        Assert.Empty(Texts("Готово.Дальше"));
    }

    // MARK: - Brackets and quotation marks

    /// <summary>
    /// Brackets and quotes around the token belong to the phrase, not to the token.
    ///
    /// Only the tail was cut off before, and the token was obliged to begin right
    /// after a space - so one leading character made the whole word invisible to
    /// the detector, and every later stage rewrote it: <c>«TextPipeline.Output»</c>
    /// came out as <c>«TextPipeline. Output»</c>. This is not an exotic case: « » is
    /// the standard Russian quotation mark, and the model emits it constantly.
    /// </summary>
    [Fact]
    public void TestLeadingQuoteOrBracketDoesNotHideTheToken()
    {
        Assert.Equal(["TextPipeline.Output"], Texts("тип «TextPipeline.Output» дальше"));
        Assert.Equal(["TextPipeline.Output"], Texts("тип (TextPipeline.Output)"));
        Assert.Equal(["AppState.shared"], Texts("зови [AppState.shared]"));
        Assert.Equal(["/путь/сентри/файл"], Texts("открой (/путь/сентри/файл)"));
        Assert.Equal([ProtectedSpanKind.Path], Kinds("открой (/путь/сентри/файл)"));
    }

    /// <summary>
    /// Cutting off the brackets has no right to turn ordinary speech into a span.
    ///
    /// A false span silently suppresses the dictionary and the polisher, and that
    /// is worse than a missed one: the person sees an uncorrected term and does
    /// not understand why.
    /// </summary>
    [Fact]
    public void TestBracketsAroundOrdinarySpeechStillGiveNoSpans()
    {
        Assert.Empty(Texts("это (и/или) то"));
        Assert.Empty(Texts("«Привет, как дела?»"));
        Assert.Empty(Texts("работает (24/7)"));
        Assert.Empty(Texts("«тайп-скрипт»"));
        Assert.Empty(Texts("смешно)))"));
        // Versions and domains in brackets remain the same as without them: they
        // are protected by the polisher's guard, and the dictionary has the right
        // to reach them.
        Assert.Empty(Texts("версия (2.0.1)"));
        Assert.Empty(Texts("сайт «wai.computer»"));
        Assert.Empty(Texts("отдай «API»"));
    }

    // MARK: - General properties

    /// <summary>
    /// Detection is idempotent: the spans of the same text are the same.
    ///
    /// The entire contract rests on this - the stages recalculate the spans after every
    /// text edit, and if the detection were "floating", the byte-to-byte gate would be false.
    /// </summary>
    [Fact]
    public void TestDetectionIsIdempotent()
    {
        const string text = "правь `swift test` в src/main.swift через --dry-run для TextPipeline.Output";
        var once = ProtectedSpanDetector.Detect(text);
        var twice = ProtectedSpanDetector.Detect(text);
        Assert.Equal(once, twice);
        Assert.Equal(4, once.Count);
    }

    /// <summary>Span ranges actually point to their text.</summary>
    [Fact]
    public void TestRangesPointAtTheirOwnText()
    {
        const string text = "открой /etc/hosts и правь TextPipeline";
        string[] characters = SplitIntoCharacters(text);
        foreach (var span in ProtectedSpanDetector.Detect(text))
        {
            Assert.Equal(span.Text, string.Concat(characters[span.Range]));
        }
    }

    [Fact]
    public void TestSpansNeverOverlapAndComeInOrder()
    {
        const string text = "`код` потом /usr/local/bin затем --flag и AppState.shared";
        var spans = ProtectedSpanDetector.Detect(text);
        Assert.Equal(4, spans.Count);
        foreach (var (earlier, later) in spans.Zip(spans.Skip(1)))
        {
            Assert.True(earlier.Range.End.Value <= later.Range.Start.Value);
        }
    }

    [Fact]
    public void TestOrdinaryRussianSpeechHasNoSpans()
    {
        Assert.Empty(Texts("Привет, как дела? Сегодня хорошая погода."));
        Assert.Empty(Texts("Надо купить молока и хлеба."));
    }

    [Fact]
    public void TestEmptyText()
    {
        Assert.Empty(ProtectedSpanDetector.Detect(""));
    }

    /// <summary>
    /// The test's own copy of the Swift <c>Array(text)</c>: grapheme clusters,
    /// the unit every span range counts.
    /// </summary>
    private static string[] SplitIntoCharacters(string text)
    {
        var characters = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            characters.Add((string)enumerator.Current);
        }

        return characters.ToArray();
    }
}
