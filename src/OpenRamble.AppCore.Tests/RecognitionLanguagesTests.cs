using LocalASR;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The language picker's catalog. No macOS counterpart test exists; these pin the two properties
/// the picker depends on - the codes come from the engine, and every row has a readable name.
/// </summary>
public class RecognitionLanguagesTests
{
    [Fact]
    public void EveryEngineCodeGetsExactlyOneNamedOption()
    {
        Assert.Equal(SherpaOnnxAdapter.SupportedLanguageHints.Count, RecognitionLanguages.Options.Count);
        Assert.Equal(
            SherpaOnnxAdapter.SupportedLanguageHints.OrderBy(code => code, StringComparer.Ordinal),
            RecognitionLanguages.Options.Select(option => option.Code).OrderBy(code => code, StringComparer.Ordinal));
        Assert.All(RecognitionLanguages.Options, option => Assert.NotEmpty(option.Name));
        // Sorted by name: the picker is read top to bottom, not in engine order.
        Assert.Equal(
            RecognitionLanguages.Options.Select(option => option.Name).Order(StringComparer.Ordinal),
            RecognitionLanguages.Options.Select(option => option.Name));
    }

    /// <summary>Automatic detection is the default, and it needs a name a person can read.</summary>
    [Fact]
    public void NoCodeMeansAutomatic()
    {
        Assert.Equal("Automatic", RecognitionLanguages.Name(null));
        Assert.Equal("xx-not-a-language", RecognitionLanguages.Name("xx-not-a-language"));
        Assert.Contains("German", RecognitionLanguages.Name("de"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A saved code the engine no longer accepts must be rejected on load: keeping it would fail
    /// every dictation and leave the picker showing a blank row.
    /// </summary>
    [Fact]
    public void OnlyEngineCodesAreSupported()
    {
        Assert.True(RecognitionLanguages.IsSupported("en"));
        Assert.False(RecognitionLanguages.IsSupported("xx-not-a-language"));
        Assert.False(RecognitionLanguages.IsSupported(null));
    }
}
