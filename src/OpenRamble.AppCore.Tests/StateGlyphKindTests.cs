using OpenRamble.AppCore;

namespace OpenRamble.AppCore.Tests;

public sealed class StateGlyphKindTests
{
    [Theory]
    [InlineData(StateGlyphKind.Listening, StatusColorRole.Recording)]
    [InlineData(StateGlyphKind.HandsFree, StatusColorRole.Recording)]
    [InlineData(StateGlyphKind.Working, StatusColorRole.Processing)]
    [InlineData(StateGlyphKind.Info, StatusColorRole.Processing)]
    [InlineData(StateGlyphKind.Waiting, StatusColorRole.Attention)]
    [InlineData(StateGlyphKind.Attention, StatusColorRole.Attention)]
    [InlineData(StateGlyphKind.Pasted, StatusColorRole.Success)]
    public void ColouredStatesTakeTheirRole(StateGlyphKind kind, StatusColorRole role)
    {
        Assert.Equal(role, kind.ColorRole);
    }

    /// <summary>Silence is never red; kept is not waiting, so it is not orange.</summary>
    [Theory]
    [InlineData(StateGlyphKind.Silent)]
    [InlineData(StateGlyphKind.Copied)]
    [InlineData(StateGlyphKind.Kept)]
    [InlineData(StateGlyphKind.Nothing)]
    public void InkStatesHaveNoStatusColour(StateGlyphKind kind)
    {
        Assert.Null(kind.ColorRole);
    }
}
