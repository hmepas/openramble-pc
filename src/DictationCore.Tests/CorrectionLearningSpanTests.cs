namespace DictationCore.Tests;

/// <summary>
/// Training on edits and protected spans.
///
/// Editing inside the code is editing the code, not the term. Turning it into
/// a silent rule of future dictations is impossible: the person was repairing a specific line,
/// rather than teaching the application the language.
/// </summary>
public sealed class CorrectionLearningSpanTests
{
    [Fact]
    public void TestProposalsAreNeverBuiltFromTextInsideASpan()
    {
        string inserted = "запусти `сентри` сейчас";
        IReadOnlyList<ProtectedSpan> spans = ProtectedSpanDetector.Detect(inserted);
        // The premise of the test.
        Assert.Equal([ProtectedSpanKind.Backticks], spans.Select(span => span.Kind));

        IReadOnlyList<DictionaryReplacement> proposals = CorrectionLearning.Propose(
            original: inserted,
            edited: "запусти `Sentry` сейчас",
            spans: spans);
        // An edit inside backticks does not make a term.
        Assert.Empty(proposals);
    }

    [Fact]
    public void TestPathEditsDoNotTeachTheDictionary()
    {
        string inserted = "лежит в /Users/mik/сентри/log";
        IReadOnlyList<DictionaryReplacement> proposals = CorrectionLearning.Propose(
            original: inserted,
            edited: "лежит в /Users/mik/sentry/log",
            spans: ProtectedSpanDetector.Detect(inserted));
        Assert.Empty(proposals);
    }

    /// <summary>
    /// The same edit outside a span teaches as before - otherwise the test above would pass
    /// with "training is completely broken".
    /// </summary>
    [Fact]
    public void TestTheSameEditOutsideASpanStillTeaches()
    {
        string inserted = "запусти сентри сейчас";
        IReadOnlyList<DictionaryReplacement> proposals = CorrectionLearning.Propose(
            original: inserted,
            edited: "запусти Sentry сейчас",
            spans: ProtectedSpanDetector.Detect(inserted));
        Assert.Single(proposals);
        Assert.Equal("сентри", proposals[0].Spoken);
        Assert.Equal("Sentry", proposals[0].Written);
    }

    /// <summary>
    /// Defaulting to an empty list keeps the previous behavior.
    ///
    /// Pairs are compared, not entire structures: <c>id</c> is a fresh identifier for each
    /// call, and there will never be equality of structures.
    /// </summary>
    [Fact]
    public void TestWithoutSpansProposalsAreExactlyWhatTheyWere()
    {
        string original = "открой поуст герз и сентри";
        string edited = "открой Postgres и Sentry";
        static string[][] Pairs(IReadOnlyList<DictionaryReplacement> proposals) =>
            proposals
                .Select(proposal => new[] { proposal.Spoken, proposal.Written, proposal.Inflects.ToString() })
                .ToArray();

        Assert.Equal(
            Pairs(CorrectionLearning.Propose(original: original, edited: edited)),
            Pairs(CorrectionLearning.Propose(original: original, edited: edited, spans: [])));
    }
}
