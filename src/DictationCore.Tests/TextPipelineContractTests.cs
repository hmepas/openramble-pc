using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace DictationCore.Tests;

/// <summary>Pipeline contract: stage boundaries and the protected span gate.</summary>
public sealed class TextPipelineContractTests
{
    private readonly DictionaryReplacement[] dictionary =
    [
        new(spoken: "поуст герз", written: "Postgres", inflects: false),
        new(spoken: "сентри", written: "Sentry", inflects: false),
    ];

    // MARK: - Gate spans

    /// <summary>
    /// <b>Main gate of the wave.</b> Spans, calculated after the dictionary, reach
    /// the insertion byte for byte.
    ///
    /// The corpus includes collision fixtures: dictionary terms inside backticks and
    /// a phonetic neighbour inside a path.
    /// </summary>
    [Theory]
    [InlineData("правь TextPipeline.Output прямо сейчас")]
    [InlineData("смотри `сентри` и просто сентри")]
    [InlineData("лежит в /Users/mik/сентри/log точно")]
    [InlineData("запусти `swift  test` с --dry-run")]
    [InlineData("открой /etc/hosts, потом AppState.shared")]
    [InlineData("onTextInserted сработал, а поуст герз нет")]
    [InlineData("зайди на https://wai.computer/openramble/ now")]
    public void TestSpansSurviveEveryPostDictionaryStageByteForByte(string text)
    {
        var pipeline = new TextPipeline(replacements: dictionary);

        string exact = DictionaryReplacements.ApplyExact(dictionary, text);
        string afterDictionary = DictionaryReplacements.ApplyPhonetic(
            dictionary,
            exact,
            ProtectedSpanDetector.Detect(exact));
        IEnumerable<string> expected = ProtectedSpanDetector.Detect(afterDictionary)
            .Select(span => span.Text);

        IEnumerable<string> final = pipeline.Run(text).Provenance.Spans
            .Select(span => span.Text);
        Assert.Equal(expected, final);
    }

    /// <summary>The spans in the origin entry actually belong to the final text.</summary>
    [Fact]
    public void TestProvenanceSpansBelongToTheFinalText()
    {
        TextPipeline.RunResult run = new TextPipeline(replacements: dictionary)
            .Run("правь TextPipeline.Output и /etc/hosts");
        Assert.Equal(
            ProtectedSpanDetector.Detect(run.Provenance.FinalText),
            run.Provenance.Spans);
    }

    // MARK: - Stage boundaries

    /// <summary>
    /// The command is separated before the dictionary - otherwise the dictionary will affect its words.
    /// </summary>
    [Fact]
    public void TestCommandIsStrippedBeforeTheDictionary()
    {
        DictionaryReplacement[] terms = [new(spoken: "строка", written: "line", inflects: false)];
        TextPipeline.Output output = new TextPipeline(replacements: terms)
            .Process("пиши сюда с новой строки");
        Assert.False(
            output.Text.Contains("line", StringComparison.Ordinal),
            "слово команды словарю не достаётся");
    }

    /// <summary>
    /// <c>afterDictionary</c> is taken before the cosmetics: the spaces have not yet been
    /// collapsed, the first letter is still lowercase.
    /// </summary>
    [Fact]
    public void TestAfterDictionaryIsTakenBeforeTypographyAndPolish()
    {
        PipelineProvenance provenance = new TextPipeline().Run("привет   мир").Provenance;
        Assert.Equal("привет   мир", provenance.AfterDictionary);
        Assert.Equal("Привет мир", provenance.FinalText);
    }

    /// <summary>
    /// Typography folding goes before the polish: otherwise the non-breaking space would
    /// survive until the insertion, and the polisher does not collapse it.
    /// </summary>
    [Fact]
    public void TestTypographyFoldRunsBeforeThePolisher()
    {
        Assert.Equal("Два слова", new TextPipeline().Process("два\u00A0\u00A0слова").Text);
    }

    /// <summary>
    /// <see cref="TrailingCommand.NewLine"/> must materialize after the polisher.
    ///
    /// The polisher starts with a trim of whitespace and newlines - the break added earlier
    /// would simply be cut off and the command would be lost.
    /// </summary>
    [Fact]
    public void TestNewLineMustRunAfterPolishOtherwiseItIsTrimmed()
    {
        TextPipeline.Output output = new TextPipeline().Process("привет с новой строки");
        Assert.True(
            output.Text.EndsWith('\n'),
            "перенос обязан дожить до вставки");
        Assert.True(output.Command is null, "команда уже выполнена текстом");
        Assert.True(
            TranscriptPolisher.Polish(output.Text) == "Привет",
            "а полишер бы её срезал");
    }

    // MARK: - Compatibility

    /// <summary>
    /// <c>Process</c> is <c>Run().Output</c>, exactly one path and no second branch.
    /// </summary>
    [Theory]
    [InlineData("открой поуст герз")]
    [InlineData("правь Foo.Bar")]
    [InlineData("")]
    [InlineData("с новой строки")]
    public void TestProcessIsExactlyRunOutput(string text)
    {
        var pipeline = new TextPipeline(replacements: dictionary);
        Assert.Equal(pipeline.Process(text), pipeline.Run(text).Output);
    }

    [Fact]
    public void TestEmptyInputStaysEmpty()
    {
        TextPipeline.RunResult run = new TextPipeline().Run("");
        Assert.Equal("", run.Output.Text);
        Assert.Empty(run.Provenance.Spans);
    }
}

/// <summary>Record the origin of the last dictation.</summary>
public sealed class PipelineProvenanceTests
{
    [Fact]
    public void TestTheDictionaryCheckpointIsRecordedBeforeTheCosmetics()
    {
        DictionaryReplacement[] terms =
        [
            new(spoken: "поуст герз", written: "Postgres", inflects: false),
        ];
        PipelineProvenance provenance = new TextPipeline(replacements: terms)
            .Run("открой поуст герз").Provenance;
        Assert.Equal("открой Postgres", provenance.AfterDictionary);
        Assert.Equal("Открой Postgres", provenance.FinalText);
    }

    [Theory]
    [InlineData("привет мир")]
    [InlineData("с новой строки")]
    [InlineData("правь Foo.Bar")]
    public void TestFinalTextIsExactlyWhatWillBeInserted(string text)
    {
        TextPipeline.RunResult run = new TextPipeline().Run(text);
        Assert.Equal(run.Provenance.FinalText, run.Output.Text);
    }

    /// <summary>
    /// Origin is built for any combination of toggles.
    ///
    /// This is the requirement "regardless of the settings": branches where there is no entry
    /// do not exist in the product.
    /// </summary>
    [Fact]
    public void TestProvenanceIsProducedRegardlessOfEveryToggle()
    {
        foreach (bool allowReturn in new[] { true, false })
        {
            foreach (bool phonetic in new[] { true, false })
            {
                var pipeline = new TextPipeline(
                    replacements: StarterDictionary.Developer,
                    allowPressReturnCommand: allowReturn,
                    phoneticMatching: phonetic);
                PipelineProvenance provenance = pipeline.Run("надо сделать деплой").Provenance;
                Assert.NotEqual("", provenance.FinalText);
            }
        }
    }

    /// <summary>
    /// "Never on disk" rests on a lack of conformance, and not on discipline.
    ///
    /// If someone adds serialization to "just save the history", this test will fail
    /// before the dictated text is in the file.
    /// </summary>
    /// <remarks>
    /// The Swift original asks whether the type conforms to <c>Encodable</c>. .NET has no such
    /// conformance - <c>System.Text.Json</c> serializes any public shape by reflection - so the
    /// port checks the deliberate act instead: nobody has marked the type as serializable, given
    /// it a converter, or written a serialization contract for it.
    /// </remarks>
    [Fact]
    public void TestProvenanceIsNotEncodable()
    {
        // происхождение не сериализуется — это и есть гарантия, что оно не попадёт на диск
        Type provenance = typeof(PipelineProvenance);
        Assert.Empty(provenance.GetCustomAttributes(typeof(SerializableAttribute), false));
        Assert.Empty(provenance.GetCustomAttributes(typeof(JsonConverterAttribute), false));
        Assert.Empty(provenance.GetCustomAttributes(typeof(DataContractAttribute), false));
        Assert.DoesNotContain(typeof(ISerializable), provenance.GetInterfaces());
    }

    /// <summary>The description does not bring out a single word from the dictation.</summary>
    [Fact]
    public void TestProvenanceDescriptionLeaksNoDictatedText()
    {
        const string secret = "совершенно секретная фраза";
        PipelineProvenance provenance = new TextPipeline().Run(secret).Provenance;
        string described = provenance.ToString();

        Assert.DoesNotContain("секретная", described, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, described, StringComparison.Ordinal);
        // Character counts are safe to expose.
        Assert.Contains("chars", described, StringComparison.Ordinal);
    }
}
