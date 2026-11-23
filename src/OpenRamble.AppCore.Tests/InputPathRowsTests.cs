using OpenRamble.AppCore;

namespace OpenRamble.AppCore.Tests;

public sealed class InputPathRowsTests
{
    [Fact]
    public void KeyRowNamesTheFirstProblemThenWaitsForAPress()
    {
        Assert.Equal((StateGlyphKind.Attention, "no device"), InputPathRows.Key("Right Ctrl", "no device", "warn", heard: true));
        Assert.Equal((StateGlyphKind.Attention, "warn"), InputPathRows.Key("Right Ctrl", null, "warn", heard: true));
        Assert.Equal((StateGlyphKind.Pasted, "OpenRamble hears Right Ctrl."), InputPathRows.Key("Right Ctrl", null, null, heard: true));
        Assert.Equal(StateGlyphKind.Silent, InputPathRows.Key("Right Ctrl", null, null, heard: false).Glyph);
    }

    [Fact]
    public void TypingRowIsDoneOnlyWithTheTools()
    {
        Assert.Equal(StateGlyphKind.Pasted, InputPathRows.Typing(true).Glyph);
        Assert.Equal(StateGlyphKind.Attention, InputPathRows.Typing(false).Glyph);
        Assert.Contains("wtype", InputPathRows.Typing(false).Detail, StringComparison.Ordinal);
    }
}
