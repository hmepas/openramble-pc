using DictationCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>The space at the end, and the places it must not appear.</summary>
public sealed class TrailingSpaceTests
{
    private static TrailingSpacePipeline Pipeline(bool appendsSpace) =>
        new(new TextPipeline(), appendsSpace);

    [Fact]
    public void TestScenario001()
    {
        Assert.Equal("Hello there ", Pipeline(true).Run("hello there").Output.Text);
        Assert.Equal("Hello there", Pipeline(false).Run("hello there").Output.Text);
    }

    /// <summary>
    /// Nothing was recognized, so there is nothing to put a space after. A lone space inserted
    /// into someone's document is worse than silence.
    /// </summary>
    [Fact]
    public void TestScenario002()
    {
        Assert.Equal("", Pipeline(true).Run("   ").Output.Text);
    }

    /// <summary>
    /// "New line" already ended the text where the person wanted it, and a space after the
    /// break is an indent nobody asked for.
    /// </summary>
    [Fact]
    public void TestScenario003()
    {
        TextPipeline.RunResult run = Pipeline(true).Run("first thought new line");
        Assert.True(run.Output.Text.EndsWith('\n'), $"got {run.Output.Text}");
    }

    /// <summary>
    /// Provenance describes the text that was inserted, or the edit watcher would be comparing
    /// against something that never reached the screen.
    /// </summary>
    [Fact]
    public void TestScenario004()
    {
        TextPipeline.RunResult run = Pipeline(true).Run("hello there");
        Assert.Equal(run.Output.Text, run.Provenance.FinalText);
    }
}
