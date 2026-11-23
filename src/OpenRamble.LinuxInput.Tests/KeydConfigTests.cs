using OpenRamble.AppCore;

namespace OpenRamble.LinuxInput.Tests;

/// <summary>
/// Reading keyd's configuration for a remap that swallows the paste-last-take chord.
/// </summary>
public sealed class KeydConfigTests
{
    private const int KeyV = 47;
    private const string Path = "/etc/keyd/default.conf";

    /// <summary>A real config: Super+C/V/X become the Insert-key clipboard chords.</summary>
    private const string SuperClipboard = """
        [ids]

        *

        [main]

        [meta]
        # when SUPER button pressed
        c = C-insert
        v = S-insert
        x = S-delete

        [meta+alt]
        c = M-A-c
        v = M-A-v
        x = M-A-x

        [meta+shift]
        minus = G-minus
        """;

    private static string? Warning(string text, HotkeyModifiers modifiers) =>
        KeydConfig.Warning([new KeydConfigFile(Path, text)], new KeyCombination(KeyV, modifiers), LinuxShortcutKeymap.Instance);

    [Fact]
    public void SuperVRemappedToShiftInsertIsFlagged()
    {
        Assert.Equal(
            "keyd turns Super+V into Shift+Insert, so this shortcut never arrives. Pick another, or change /etc/keyd/default.conf.",
            Warning(SuperClipboard, HotkeyModifiers.Control | HotkeyModifiers.Windows));
    }

    [Fact]
    public void CtrlAltVIsLeftAlone()
    {
        Assert.Null(Warning(SuperClipboard, HotkeyModifiers.Control | HotkeyModifiers.Alt));
    }

    [Fact]
    public void ACompositeLayerReEmittingTheSameChordIsNoProblem()
    {
        // [meta+alt] wins over [meta] and gives back Super+Alt+V unchanged.
        Assert.Null(Warning(SuperClipboard, HotkeyModifiers.Windows | HotkeyModifiers.Alt));
    }

    [Fact]
    public void ACompositeLayerThatRemapsIsFlagged()
    {
        const string config = """
            [meta+alt]
            v = C-v
            """;

        Assert.Equal(
            "keyd turns Alt+Super+V into Ctrl+V, so this shortcut never arrives. Pick another, or change /etc/keyd/default.conf.",
            Warning(config, HotkeyModifiers.Windows | HotkeyModifiers.Alt));
        Assert.Null(Warning(config, HotkeyModifiers.Windows | HotkeyModifiers.Control));
    }

    [Theory]
    [InlineData("[meta\nv = S-insert")]
    [InlineData("[meta]\nv S-insert")]
    [InlineData("v = S-insert\n[meta]")]
    [InlineData("\0\u0001garbage")]
    public void AMalformedFileSaysNothing(string text)
    {
        Assert.Null(Warning(text, HotkeyModifiers.Control | HotkeyModifiers.Windows));
    }

    [Fact]
    public void AMissingDirectoryReadsAsNoFiles()
    {
        Assert.Empty(KeydConfig.Read("/nonexistent/keyd"));
    }
}
