using DictationCore;

namespace OpenRamble.LinuxInput.Tests;

/// <summary>
/// Naming the focused window by its class, so the text can be shaped for the kind of app it is
/// going to - and "unknown" whenever the desktop's answer is anything but clear.
/// </summary>
public sealed class FocusedWindowTests
{
    [Fact]
    public void HyprlandsActiveWindowIsNamedByItsClassAndNeverItsTitle()
    {
        TargetApplication? window = FocusedWindow.FromHyprland(
            """{"address":"0x5f","class":"com.mitchellh.ghostty","title":"~/secret-plans.md","initialClass":"com.mitchellh.ghostty","pid":1503}""");

        Assert.Equal(new TargetApplication("com.mitchellh.ghostty", 1503, null), window);
        Assert.Equal(ApplicationCategory.Terminal, PromptModePolicy.Category(window));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"class":""}""")]
    [InlineData("""{"class":null}""")]
    [InlineData("not json")]
    [InlineData("")]
    public void AnythingButAClassIsUnknown(string answer)
    {
        Assert.Null(FocusedWindow.FromHyprland(answer));
    }

    [Fact]
    public void AnX11WindowIsNamedByTheClassHalfOfWmClass()
    {
        Assert.Equal("0x3a00007", FocusedWindow.ActiveWindowId("_NET_ACTIVE_WINDOW(WINDOW): window id # 0x3a00007\n"));
        Assert.Null(FocusedWindow.ActiveWindowId("_NET_ACTIVE_WINDOW(WINDOW): window id # 0x0\n"));
        Assert.Null(FocusedWindow.ActiveWindowId("_NET_ACTIVE_WINDOW:  not found.\n"));

        TargetApplication? window = FocusedWindow.FromXprop(
            "WM_CLASS(STRING) = \"xterm\", \"XTerm\"\n_NET_WM_PID(CARDINAL) = 4242\n");
        Assert.Equal(new TargetApplication("XTerm", 4242, null), window);
        Assert.Equal(ApplicationCategory.Terminal, PromptModePolicy.Category(window));
        Assert.Null(FocusedWindow.FromXprop("WM_CLASS:  not found.\n"));
    }

    [Fact]
    public void TheInserterTargetsTheNamedWindowOrAPlaceholderThatIsNeverNull()
    {
        var named = new TargetApplication("kitty", 7, null);
        Assert.Equal(named, new LinuxTextInserter(new FakeWaylandDesktop(), focusedWindow: () => named).FrontmostApplication());

        // Unknown is still somewhere to insert: verbatim, and no Return.
        TargetApplication? unknown = new LinuxTextInserter(new FakeWaylandDesktop()).FrontmostApplication();
        Assert.NotNull(unknown);
        Assert.Equal(ApplicationCategory.Unknown, PromptModePolicy.Category(unknown));
    }
}
