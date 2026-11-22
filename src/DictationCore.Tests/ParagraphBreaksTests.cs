namespace DictationCore.Tests;

/// <summary>
/// A long pause after a sentence becomes a paragraph.
///
/// The timings below are what the shipping Parakeet model reported for
/// synthesized speech with a measured silence between sentences (macOS `say`,
/// two voices, 300 ms to 3 s). The model folds a pause into the previous word's
/// duration or into a gap before the next word, so both shapes appear here.
/// </summary>
public sealed class ParagraphBreaksTests
{
    internal static IReadOnlyList<ASRResult.Word> Words(params (string Text, double Start, double End)[] spans) =>
        spans
            .Select(static span => new ASRResult.Word(
                span.Text,
                TimeSpan.FromSeconds(span.Start),
                TimeSpan.FromSeconds(span.End)))
            .ToArray();

    [Fact]
    public void TestAPauseFoldedIntoTheLastWordOfASentenceBreaksTheParagraph()
    {
        // 2 s of silence after "green." - the model stretched the word.
        const string text = "The tests are green. Next, we ship.";
        IReadOnlyList<ASRResult.Word> timings = Words(
            ("The", 0.0, 0.2), ("tests", 0.2, 0.5), ("are", 0.5, 0.8), ("green.", 0.8, 2.96),
            ("Next,", 2.96, 3.4), ("we", 3.4, 3.6), ("ship.", 3.6, 4.0));
        Assert.Equal("The tests are green.\u2029Next, we ship.", ParagraphBreaks.Mark(text, timings));
    }

    [Fact]
    public void TestAPauseReportedAsAGapBreaksTheParagraph()
    {
        // 1.8 s of silence after "it." - the model left a gap instead.
        const string text = "Then we test it. Finally we ship.";
        IReadOnlyList<ASRResult.Word> timings = Words(
            ("Then", 0.0, 0.2), ("we", 0.2, 0.4), ("test", 0.4, 0.7), ("it.", 0.7, 1.34),
            ("Finally", 2.7, 3.2), ("we", 3.2, 3.4), ("ship.", 3.4, 3.9));
        Assert.Equal("Then we test it.\u2029Finally we ship.", ParagraphBreaks.Mark(text, timings));
    }

    [Fact]
    public void TestAnOrdinaryPauseBetweenSentencesDoesNot()
    {
        // 1.2 s of silence measured: "yesterday." 1.68 s long - still one paragraph.
        const string text = "We finished yesterday. Tomorrow we start.";
        IReadOnlyList<ASRResult.Word> timings = Words(
            ("We", 0.0, 0.2), ("finished", 0.2, 0.7), ("yesterday.", 0.7, 2.38),
            ("Tomorrow", 2.38, 2.9), ("we", 2.9, 3.1), ("start.", 3.1, 3.6));
        Assert.Equal(text, ParagraphBreaks.Mark(text, timings));
    }

    [Fact]
    public void TestAPauseAfterACommaIsNotAParagraph()
    {
        const string text = "We ship, and then we rest.";
        IReadOnlyList<ASRResult.Word> timings = Words(
            ("We", 0.0, 0.2), ("ship,", 0.2, 3.0), ("and", 3.0, 3.2), ("then", 3.2, 3.4),
            ("we", 3.4, 3.5), ("rest.", 3.5, 3.9));
        Assert.Equal(text, ParagraphBreaks.Mark(text, timings));
    }

    [Fact]
    public void TestNothingFollowsTheLastWord()
    {
        const string text = "We ship.";
        Assert.Equal(text, ParagraphBreaks.Mark(text, Words(("We", 0.0, 0.2), ("ship.", 0.2, 4.0))));
    }

    /// <summary>
    /// When the words cannot be matched to the text one for one, nothing is
    /// guessed: the text goes on unbroken.
    /// </summary>
    [Fact]
    public void TestWordsThatDoNotMatchTheTextLeaveItAlone()
    {
        const string text = "The tests are green. Next, we ship.";
        Assert.Equal(text, ParagraphBreaks.Mark(text, []));
        Assert.Equal(text, ParagraphBreaks.Mark(text, Words(("The", 0.0, 0.2), ("tests", 0.2, 5.0))));
    }

    // MARK: - Through the pipeline

    [Fact]
    public void TestTheMarkBecomesABlankLineInProseAndASpaceElsewhere()
    {
        const string marked = "the tests are green.\u2029Next, we ship";
        Assert.Equal(
            "The tests are green.\n\nNext, we ship",
            new TextPipeline(mode: PromptMode.Prose).Process(marked).Text);
        Assert.Equal(
            "The tests are green. Next, we ship",
            new TextPipeline(mode: PromptMode.Verbatim).Process(marked).Text);
        Assert.Equal("The tests are green. Next, we ship", new TextPipeline().Process(marked).Text);
    }

    [Fact]
    public void TestAHesitationRightAfterABreakDoesNotTakeTheBreakWithIt()
    {
        Assert.Equal(
            "First point.\n\nSecond point.",
            new TextPipeline(mode: PromptMode.Prose).Process("First point.\u2029Um, second point.").Text);
    }

    /// <summary>
    /// Words shaped for a prose app that end up in a verbatim one ("Insert
    /// Last Dictation" into a terminal) lose their paragraph breaks - each
    /// becomes the space the pipeline puts there - and keep the line break a
    /// spoken "New line." asked for.
    /// </summary>
    [Fact]
    public void TestFittingProseTextToAVerbatimAppFoldsItsParagraphs()
    {
        Assert.Equal("Para one. Para two.", ParagraphBreaks.Fit("Para one.\n\nPara two.", PromptMode.Verbatim));
        Assert.Equal("Para one. Para two.\n", ParagraphBreaks.Fit("Para one.\n\nPara two.\n", PromptMode.Verbatim));
        Assert.Equal("Para one.\n\nPara two.", ParagraphBreaks.Fit("Para one.\n\nPara two.", PromptMode.Prose));
    }

    /// <summary>
    /// A person who pauses before saying "New line." still gets a line break,
    /// not the words under a blank line.
    /// </summary>
    [Fact]
    public void TestNewLineSaidAfterALongPauseIsStillALineBreak()
    {
        Assert.Equal("Dear John.\n", new TextPipeline(mode: PromptMode.Prose).Process("Dear John.\u2029New line.").Text);
        Assert.Equal(
            "Привет.\n",
            new TextPipeline(mode: PromptMode.Prose).Process("Привет.\u2029Новая строка.").Text);
        Assert.Equal(
            "Dear John.\n",
            new TextPipeline(mode: PromptMode.Verbatim).Process("Dear John.\u2029New line.").Text);
    }
}

/// <summary>
/// The whole path: a take with a long pause reaches a prose app as two
/// paragraphs and a terminal as one line.
/// </summary>
[Collection(DictationControllerCollection.Name)]
public sealed class ParagraphBreaksDictationTests
{
    private static async Task<IReadOnlyList<string>> Dictate(PromptMode mode)
    {
        var inserter = new FakeInserter();
        var controller = new DictationController(
            capture: new FakeCapture(),
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "The tests are green. Next, we ship.",
                words: ParagraphBreaksTests.Words(
                    ("The", 0.0, 0.2), ("tests", 0.2, 0.5), ("are", 0.5, 0.8), ("green.", 0.8, 2.96),
                    ("Next,", 2.96, 3.4), ("we", 3.4, 3.6), ("ship.", 3.6, 4.0)),
                audioDuration: TimeSpan.FromSeconds(4),
                processingDuration: TimeSpan.FromSeconds(0.1))),
            inserter: inserter,
            overlay: new FakeOverlay(),
            sounds: new FakeSounds(),
            pipeline: _ => new TextPipeline(mode: mode));
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle(12);
        controller.Stop();
        await Settle(24);
        return inserter.InsertedTexts;
    }

    [Fact]
    public async Task TestAProseAppGetsTwoParagraphs()
    {
        Assert.Equal(["The tests are green.\n\nNext, we ship."], await Dictate(PromptMode.Prose));
    }

    [Fact]
    public async Task TestATerminalGetsOneLine()
    {
        Assert.Equal(["The tests are green. Next, we ship."], await Dictate(PromptMode.Verbatim));
    }

    private static async Task Settle(int iterations)
    {
        for (int i = 0; i < iterations; i += 1)
        {
            await Task.Yield();
            await Task.Delay(5);
        }
    }
}
