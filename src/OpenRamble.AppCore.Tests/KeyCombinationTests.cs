namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The shortcut that puts the last dictation back on the clipboard, and the rules that decide
/// what may be one. The scenarios that need a running app (the monitor starting and stopping,
/// the setting surviving a relaunch) live with the app-state suites.
/// </summary>
public sealed class KeyCombinationTests
{
    private const int VkC = 0x43;
    private const int VkF5 = 0x74;
    private const int VkF13 = 0x7C;
    private const int VkLeft = 0x25;
    private const int VkSpace = 0x20;
    private const int VkEscape = 0x1B;
    private const int VkBack = 0x08;
    private const int VkDelete = 0x2E;

    private static readonly IShortcutKeymap Windows = WindowsShortcutKeymap.Instance;
    private static readonly IShortcutKeymap Linux = LinuxShortcutKeymap.Instance;

    /// <summary>
    /// A plain key would fire in the middle of a word, and Shift with a letter is just a
    /// capital letter. Both would type into someone's document.
    /// </summary>
    [Fact]
    public void TestScenario002()
    {
        Assert.False(new KeyCombination(VkC, HotkeyModifiers.None).IsValid(Windows));
        Assert.False(new KeyCombination(VkC, HotkeyModifiers.Shift).IsValid(Windows));
        Assert.True(new KeyCombination(VkC, HotkeyModifiers.Windows).IsValid(Windows));
        Assert.True(new KeyCombination(VkC, HotkeyModifiers.Control).IsValid(Windows));
        Assert.True(new KeyCombination(VkC, HotkeyModifiers.Alt).IsValid(Windows));
    }

    /// <summary>The function row types nothing, which is exactly why people bind it.</summary>
    [Fact]
    public void TestScenario003()
    {
        Assert.True(new KeyCombination(VkF13, HotkeyModifiers.None).IsValid(Windows));
        Assert.True(new KeyCombination(VkF5, HotkeyModifiers.None).IsValid(Windows));
        // Evdev: F5 is 63, F13 is 183 - a different table, the same answer.
        Assert.True(new KeyCombination(63, HotkeyModifiers.None).IsValid(Linux));
        Assert.True(new KeyCombination(183, HotkeyModifiers.None).IsValid(Linux));
        Assert.False(new KeyCombination(46, HotkeyModifiers.None).IsValid(Linux)); // KEY_C
    }

    /// <summary>
    /// Ctrl+C must not answer to Ctrl+Shift+C: in most applications that is a different
    /// command, and a shortcut that fires on supersets fires on other people's shortcuts.
    /// </summary>
    [Fact]
    public void TestScenario004()
    {
        var shortcut = new KeyCombination(VkC, HotkeyModifiers.Control | HotkeyModifiers.Alt);
        Assert.True(shortcut.Matches(VkC, HotkeyModifiers.Control | HotkeyModifiers.Alt));
        Assert.False(shortcut.Matches(VkC, HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift));
        Assert.False(shortcut.Matches(VkC, HotkeyModifiers.Control));
        Assert.False(shortcut.Matches(VkF5, HotkeyModifiers.Control | HotkeyModifiers.Alt));
    }

    /// <summary>
    /// The Mac has to mask out the function and Caps Lock bits of the event. Here the modifier
    /// set has no such bits by construction, so a chord with an arrow key matches on the four
    /// modifiers alone - and a stray bit outside the allowed set is dropped at construction.
    /// </summary>
    [Fact]
    public void TestScenario005()
    {
        var left = new KeyCombination(VkLeft, HotkeyModifiers.Control);
        Assert.True(left.Matches(VkLeft, HotkeyModifiers.Control));
        var stray = new KeyCombination(VkLeft, HotkeyModifiers.Control | (HotkeyModifiers)(1 << 7));
        Assert.Equal(HotkeyModifiers.Control, stray.Modifiers);
        Assert.True(stray.Matches(VkLeft, HotkeyModifiers.Control | (HotkeyModifiers)(1 << 7)));
    }

    /// <summary>
    /// Modifier words in the order every PC menu uses - Ctrl, Alt, Shift, Win - joined with a
    /// plus, so it reads like the menus around it. (The Mac prints ⌃⌥⇧⌘C.)
    /// </summary>
    [Fact]
    public void TestScenario006()
    {
        var shortcut = new KeyCombination(
            VkC,
            HotkeyModifiers.Windows | HotkeyModifiers.Shift | HotkeyModifiers.Alt | HotkeyModifiers.Control);
        Assert.Equal("Ctrl+Alt+Shift+Win+C", shortcut.DisplayString(Windows));
        Assert.Equal("Alt+Space", new KeyCombination(VkSpace, HotkeyModifiers.Alt).DisplayString(Windows));
        Assert.Equal("Ctrl+Super+C", new KeyCombination(46, HotkeyModifiers.Control | HotkeyModifiers.Windows).DisplayString(Linux));
        Assert.Equal("Alt+Space", new KeyCombination(57, HotkeyModifiers.Alt).DisplayString(Linux));
    }

    /// <summary>
    /// It survives a trip through the settings file, which is the only way it survives a
    /// relaunch.
    /// </summary>
    [Fact]
    public void TestScenario007()
    {
        var shortcut = new KeyCombination(VkC, HotkeyModifiers.Control | HotkeyModifiers.Alt);
        Assert.Equal(shortcut, KeyCombination.Parse(shortcut.RawValue));
        Assert.Equal("67:3", shortcut.RawValue);
        Assert.Null(KeyCombination.Parse("nonsense"));
        Assert.Null(KeyCombination.Parse(""));
        Assert.Null(KeyCombination.Parse(null));
        Assert.Null(KeyCombination.Parse("67:3:1"));
        Assert.Null(KeyCombination.Parse("-1:3"));
    }

    /// <summary>
    /// Escape leaves the binding alone and Delete empties it - which is also why neither can be
    /// recorded.
    /// </summary>
    [Fact]
    public void TestScenario008()
    {
        Assert.IsType<ShortcutRecording.Outcome.Cancel>(
            ShortcutRecording.Decide(VkEscape, HotkeyModifiers.None, Windows));
        Assert.IsType<ShortcutRecording.Outcome.Clear>(
            ShortcutRecording.Decide(VkBack, HotkeyModifiers.None, Windows));
        Assert.IsType<ShortcutRecording.Outcome.Clear>(
            ShortcutRecording.Decide(VkDelete, HotkeyModifiers.None, Windows));
        Assert.IsType<ShortcutRecording.Outcome.Cancel>(
            ShortcutRecording.Decide(1, HotkeyModifiers.None, Linux)); // KEY_ESC
        Assert.IsType<ShortcutRecording.Outcome.Clear>(
            ShortcutRecording.Decide(111, HotkeyModifiers.None, Linux)); // KEY_DELETE
        // With a modifier they are ordinary keys again.
        Assert.Equal(
            new ShortcutRecording.Outcome.Commit(new KeyCombination(VkEscape, HotkeyModifiers.Windows)),
            ShortcutRecording.Decide(VkEscape, HotkeyModifiers.Windows, Windows));
    }

    /// <summary>A rejected press keeps the field listening rather than storing nonsense.</summary>
    [Fact]
    public void TestScenario009()
    {
        var outcome = Assert.IsType<ShortcutRecording.Outcome.Reject>(
            ShortcutRecording.Decide(VkC, HotkeyModifiers.None, Windows));
        Assert.Contains("Win", outcome.Reason, StringComparison.Ordinal);
        var onLinux = Assert.IsType<ShortcutRecording.Outcome.Reject>(
            ShortcutRecording.Decide(46, HotkeyModifiers.Shift, Linux));
        Assert.Contains("Super", onLinux.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stored shortcut from another keyboard still has a name: an unknown code reads as
    /// <c>Key N</c>, never as nothing.
    /// </summary>
    [Fact]
    public void TestEveryCodeHasALabel()
    {
        Assert.Equal("C", Windows.LabelFor(VkC));
        Assert.Equal("7", Windows.LabelFor(0x37));
        Assert.Equal("F13", Windows.LabelFor(VkF13));
        Assert.Equal("F24", Windows.LabelFor(0x87));
        Assert.Equal("Key 255", Windows.LabelFor(255));
        Assert.Equal("Q", Linux.LabelFor(16));
        Assert.Equal("M", Linux.LabelFor(50));
        Assert.Equal("0", Linux.LabelFor(11));
        Assert.Equal("F11", Linux.LabelFor(87));
        Assert.Equal("F24", Linux.LabelFor(194));
        Assert.Equal("Key 700", Linux.LabelFor(700));
    }
}
