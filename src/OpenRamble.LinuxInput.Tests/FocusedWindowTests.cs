using System.Diagnostics;
using System.Net.Sockets;
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

    /// <summary>
    /// A compositor that takes the connection and never answers, on both sockets: one budget for
    /// the whole question, not one per socket or per read.
    /// </summary>
    [Fact]
    public void ACompositorThatNeverAnswersIsUnknownWithinOneBudget()
    {
        string directory = Directory.CreateTempSubdirectory("hypr-").FullName;
        try
        {
            string first = Path.Combine(directory, "a.sock");
            string second = Path.Combine(directory, "b.sock");
            using Socket silentA = Listen(first, backlog: 8);
            using Socket silentB = Listen(second, backlog: 8);
            var budget = TimeSpan.FromMilliseconds(200);

            long began = Stopwatch.GetTimestamp();
            TargetApplication? window = FocusedWindow.AskHyprland([first, second], budget);
            TimeSpan took = Stopwatch.GetElapsedTime(began);

            Assert.Null(window);
            // It did ask: an answer of "unknown" without waiting would prove nothing.
            Assert.True(took >= budget - TimeSpan.FromMilliseconds(20), $"the question took only {took}");
            Assert.True(took < budget + TimeSpan.FromMilliseconds(150), $"the question took {took}");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A listener that stopped accepting: once its queue is full, a connect blocks. That connect
    /// used to have no bound at all.
    /// </summary>
    [Fact]
    public void AConnectThatCannotCompleteIsUnknownWithinTheBudget()
    {
        string directory = Directory.CreateTempSubdirectory("hypr-").FullName;
        var fillers = new List<Socket>();
        try
        {
            string path = Path.Combine(directory, "full.sock");
            using Socket stuck = Listen(path, backlog: 0);
            // Fill the accept queue without ever accepting, until a further connect would block.
            for (int i = 0; i < 64; i += 1)
            {
                var filler = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { Blocking = false };
                fillers.Add(filler);
                try
                {
                    filler.Connect(new UnixDomainSocketEndPoint(path));
                }
                catch (SocketException)
                {
                    break;
                }
            }

            var budget = TimeSpan.FromMilliseconds(200);
            long began = Stopwatch.GetTimestamp();
            TargetApplication? window = FocusedWindow.AskHyprland([path], budget);
            TimeSpan took = Stopwatch.GetElapsedTime(began);

            // A blocking connect here waited for an accept that never comes.
            Assert.Null(window);
            Assert.True(took < budget + TimeSpan.FromMilliseconds(150), $"the question took {took}");
        }
        finally
        {
            foreach (Socket filler in fillers) { filler.Dispose(); }
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Socket Listen(string path, int backlog)
    {
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(backlog);
        return listener;
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
