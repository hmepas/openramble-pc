using DictationCore;
using Xunit;

namespace DictationCore.Tests;

/// <summary>A title becomes a file name without becoming a path.</summary>
public class MeetingExportNamingTests
{
    [Fact]
    public void AnOrdinaryTitleIsKept() =>
        Assert.Equal("Standup with Anna", MeetingExportNaming.FileName("Standup with Anna", fallback: "3 September"));

    [Fact]
    public void SeparatorsCannotSurvive() =>
        Assert.Equal("Q3 Q4 plan  rollout", MeetingExportNaming.FileName("Q3/Q4 plan: rollout", fallback: "3 September"));

    [Fact]
    public void NewlinesBecomeSpaces() =>
        Assert.Equal("Call with Sam", MeetingExportNaming.FileName("Call\nwith Sam", fallback: "3 September"));

    /// <summary>A name starting with a dot is hidden - never that.</summary>
    [Fact]
    public void ALeadingDotIsRemoved() =>
        Assert.Equal("notes", MeetingExportNaming.FileName("..notes", fallback: "3 September"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("///")]
    [InlineData(".")]
    [InlineData("\n")]
    public void ATitleWithNothingLeftFallsBackToTheDate(string title) =>
        Assert.Equal("3 September", MeetingExportNaming.FileName(title, fallback: "3 September"));

    [Fact]
    public void ALongTitleIsCutOnACharacterBoundary()
    {
        string name = MeetingExportNaming.FileName(string.Concat(Enumerable.Repeat("🙂", 200)), fallback: "x");
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(name) <= 200);
        // cut between characters, never inside one
        Assert.Equal(50, new System.Globalization.StringInfo(name).LengthInTextElements);
    }
}
