using System.Net.Sockets;
using System.Runtime.Versioning;
using OpenRamble.AppCore;

namespace OpenRamble.LinuxInput.Tests;

/// <summary>
/// The second way to the dictation key: the protocol, the socket file, and the promise that both
/// ways end up in the same gesture machine.
/// </summary>
/// <remarks>
/// Three kinds of test, and they are kept apart on purpose. The protocol is checked against a fake
/// hotkey, because a reply table is a table. The sequencing is checked against a real
/// <see cref="EvdevKeyboardMonitor"/> with a fake keyboard behind it, because "the socket drives
/// the same machine the keyboard drives" is the entire claim and a fake monitor would let it
/// quietly stop being true. The socket itself is checked against a real one in a temporary
/// directory, because the parts that go wrong - a node left behind by a crash, a mode nobody set,
/// a file still there after a clean exit - are all file system facts.
/// </remarks>
[Collection(LinuxInputCollection.Name)]
public sealed class ControlSocketTests
{
    private static readonly TimeSpan Rescan = TimeSpan.FromMilliseconds(30);

    private static readonly TimeSpan Reply = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("hotkey press", "ok down")]
    [InlineData("hotkey release", "ok up")]
    [InlineData("status", "ok state=Idle key=RightCtrl down=false")]
    // A shell that writes CRLF, and a config file with a stray space, are both people typing.
    [InlineData("  hotkey press  ", "ok down")]
    [InlineData("hotkey press\r", "ok down")]
    [InlineData("", "error empty command")]
    [InlineData("   ", "error empty command")]
    [InlineData("hotkey", "error unknown command")]
    [InlineData("hotkey press please", "error unknown command")]
    [InlineData("HOTKEY PRESS", "error unknown command")]
    [InlineData("quit", "error unknown command")]
    public void EveryLineHasOneAnswer(string command, string expected)
    {
        var hotkey = new FakeHotkeyControl();
        using var server = new ControlSocketServer(hotkey, () => "Idle");

        Assert.Equal(expected, server.Execute(command));
    }

    [Fact]
    public void ToggleAsksTheKeyWhereItIsAndPutsItTheOtherWay()
    {
        var hotkey = new FakeHotkeyControl();
        using var server = new ControlSocketServer(hotkey, () => "Idle");

        Assert.Equal("ok down", server.Execute("hotkey toggle"));
        Assert.True(hotkey.IsHotkeyDown);
        Assert.Equal("ok up", server.Execute("hotkey toggle"));
        Assert.False(hotkey.IsHotkeyDown);
    }

    [Fact]
    public void AskingForAnEdgeTheKeyIsAlreadyAtIsNotAnError()
    {
        // The caller asked for a state, not for an event: a compositor that sent two presses
        // because a key repeated must not be told it did something wrong.
        var hotkey = new FakeHotkeyControl();
        using var server = new ControlSocketServer(hotkey, () => "Idle");

        Assert.Equal("ok down", server.Execute("hotkey press"));
        Assert.Equal("ok down", server.Execute("hotkey press"));
        Assert.Equal(1, hotkey.Edges);
    }

    [Fact]
    public void StatusSaysWhatTheApplicationIsDoing()
    {
        var hotkey = new FakeHotkeyControl { Hotkey = DictationHotkey.CapsLock };
        using var server = new ControlSocketServer(hotkey, () => "Listening");

        server.Execute("hotkey press");

        Assert.Equal("ok state=Listening key=CapsLock down=true", server.Execute("status"));
    }

    [Fact]
    public void AMonitorThatIsNotTrackingSaysSoRatherThanReportingAPress()
    {
        // Otherwise a bind on a machine where the app is shutting down answers "ok" and the person
        // spends the evening looking for the dictation that never started.
        var hotkey = new FakeHotkeyControl { IsTracking = false };
        using var server = new ControlSocketServer(hotkey, () => "Idle");

        Assert.Equal("error the dictation key is not being tracked", server.Execute("hotkey press"));
        Assert.Equal("error the dictation key is not being tracked", server.Execute("hotkey toggle"));
    }

    [Fact]
    public void AStatusThatThrowsIsAnUnknownStateAndNotAnEndedProcess()
    {
        var hotkey = new FakeHotkeyControl();
        using var server = new ControlSocketServer(
            hotkey,
            () => throw new ObjectDisposedException("state"));

        Assert.Equal("ok state=unknown key=RightCtrl down=false", server.Execute("status"));
    }

    [Fact]
    public void TheLogRecordsTheVerbAndNeverTheCallersText()
    {
        // The socket is a place where text from outside this process reaches a logger whose whole
        // rule is that only our own words are written.
        using var diagnostics = new DiagnosticRecorder();
        var hotkey = new FakeHotkeyControl();
        using var server = new ControlSocketServer(hotkey, () => "Idle");

        server.Execute("hunter2 was here");

        Assert.Equal("unknown", diagnostics.Field("ctl.command", "command"));
        Assert.Equal("false", diagnostics.Field("ctl.command", "ok"));
        Assert.DoesNotContain(diagnostics.Lines, line => line.Contains("hunter2", StringComparison.Ordinal));
    }

    [Fact]
    public void ASocketPressAndReleaseIsTheSameGestureAKeyboardPressAndReleaseIs()
    {
        using var diagnostics = new DiagnosticRecorder();
        var source = new FakeKeyboardDeviceSource();
        source.Add();
        using var monitor = new EvdevKeyboardMonitor(source, Rescan);
        using var server = new ControlSocketServer(monitor, () => "Idle");
        int presses = 0;
        int releases = 0;
        monitor.OnPress = () => Interlocked.Increment(ref presses);
        monitor.OnRelease = () => Interlocked.Increment(ref releases);
        monitor.Start();

        Assert.Equal("ok down", server.Execute("hotkey press"));

        Assert.True(Until.True(() => Volatile.Read(ref presses) == 1));

        Assert.Equal("ok up", server.Execute("hotkey release"));

        Assert.True(Until.True(() => Volatile.Read(ref releases) == 1));
        // The one field that tells the two ways in apart, and the reason it exists: a log from a
        // machine using compositor binds has to be readable as such.
        Assert.Equal("ctl", diagnostics.Field("hotkey.edge", "source"));
        Assert.Equal("RightCtrl", diagnostics.Field("hotkey.edge", "key"));
    }

    [Fact]
    public void AKeyboardEdgeStillSaysItCameFromTheKeyboard()
    {
        using var diagnostics = new DiagnosticRecorder();
        var source = new FakeKeyboardDeviceSource();
        KeyFeed keyboard = source.Add();
        using var monitor = new EvdevKeyboardMonitor(source, Rescan);
        int downs = 0;
        monitor.OnHotkeyDown = () => Interlocked.Increment(ref downs);
        monitor.Start();

        keyboard.Press(Keys.RightCtrl);

        Assert.True(Until.True(() => Volatile.Read(ref downs) == 1));
        Assert.Equal("evdev", diagnostics.Field("hotkey.edge", "source"));
    }

    [Fact]
    public void TheSocketWorksOnTheMachineWhereTheKeyboardCannotBeRead()
    {
        // The entire point of the wave: no input group, no readable device, and the key still works
        // because the compositor is driving it.
        var source = new FakeKeyboardDeviceSource();
        source.AddUnreadable("event2", "Forbidden Keyboard");
        using var monitor = new EvdevKeyboardMonitor(source, Rescan);
        using var server = new ControlSocketServer(monitor, () => "Idle");
        int presses = 0;
        monitor.OnPress = () => Interlocked.Increment(ref presses);
        monitor.OnTrackingFailure = _ => { };
        monitor.Start();
        Assert.False(monitor.IsRunning);

        Assert.Equal("ok down", server.Execute("hotkey press"));

        Assert.True(Until.True(() => Volatile.Read(ref presses) == 1));
    }

    [Fact]
    public void TogglingTwiceOverTheSocketIsAWholeDictation()
    {
        var source = new FakeKeyboardDeviceSource();
        source.Add();
        using var monitor = new EvdevKeyboardMonitor(source, Rescan);
        using var server = new ControlSocketServer(monitor, () => "Idle");
        int presses = 0;
        int releases = 0;
        monitor.OnPress = () => Interlocked.Increment(ref presses);
        monitor.OnRelease = () => Interlocked.Increment(ref releases);
        monitor.Start();

        Assert.Equal("ok down", server.Execute("hotkey toggle"));
        Assert.True(Until.True(() => Volatile.Read(ref presses) == 1));
        Assert.True(monitor.IsHotkeyDown);

        Assert.Equal("ok up", server.Execute("hotkey toggle"));

        Assert.True(Until.True(() => Volatile.Read(ref releases) == 1));
        Assert.False(monitor.IsHotkeyDown);
    }

    [Fact]
    public void AToggleWhileTheKeyIsPhysicallyHeldEndsTheDictationAndNothingWedges()
    {
        // The interaction a person creates by binding the same key in their compositor while also
        // being in the input group. There is one key state and both sources move it, so the
        // dictation ends at whichever edge arrives first - here the socket's - and the physical
        // release that follows finds the key already up and raises nothing at all. The failure this
        // rules out is the other one: a second press that can never be released.
        var source = new FakeKeyboardDeviceSource();
        KeyFeed keyboard = source.Add();
        using var monitor = new EvdevKeyboardMonitor(source, Rescan);
        using var server = new ControlSocketServer(monitor, () => "Idle");
        int presses = 0;
        int releases = 0;
        int ups = 0;
        int escapes = 0;
        monitor.OnPress = () => Interlocked.Increment(ref presses);
        monitor.OnRelease = () => Interlocked.Increment(ref releases);
        monitor.OnHotkeyUp = () => Interlocked.Increment(ref ups);
        monitor.OnEscape = () => Interlocked.Increment(ref escapes);
        monitor.Start();

        keyboard.Press(Keys.RightCtrl);
        Assert.True(Until.True(() => Volatile.Read(ref presses) == 1));
        Assert.True(monitor.IsHotkeyDown);

        Assert.Equal("ok up", server.Execute("hotkey toggle"));

        Assert.True(Until.True(() => Volatile.Read(ref releases) == 1));
        Assert.Equal(1, Volatile.Read(ref ups));

        // Escape rides the same reader thread behind the physical release, so seeing it means the
        // release before it has already been through the machine. A negative assertion needs
        // something positive to wait on.
        keyboard.Release(Keys.RightCtrl);
        keyboard.Press(Keys.Escape);

        Assert.True(Until.True(() => Volatile.Read(ref escapes) == 1));
        Assert.Equal(1, Volatile.Read(ref ups));
        Assert.Equal(1, Volatile.Read(ref releases));
        Assert.Equal(1, Volatile.Read(ref presses));
        Assert.False(monitor.IsHotkeyDown);
    }

    [Fact]
    public void APhysicalTapEndsAHoldTheSocketStarted()
    {
        // The mirror image, and it has to hold too: the socket pressed, the finger released.
        var source = new FakeKeyboardDeviceSource();
        KeyFeed keyboard = source.Add();
        using var monitor = new EvdevKeyboardMonitor(source, Rescan);
        using var server = new ControlSocketServer(monitor, () => "Idle");
        int presses = 0;
        int releases = 0;
        monitor.OnPress = () => Interlocked.Increment(ref presses);
        monitor.OnRelease = () => Interlocked.Increment(ref releases);
        monitor.Start();

        Assert.Equal("ok down", server.Execute("hotkey press"));
        Assert.True(Until.True(() => Volatile.Read(ref presses) == 1));

        // The press finds the key already down and is deduplicated away; the release is the edge.
        keyboard.Press(Keys.RightCtrl);
        keyboard.Release(Keys.RightCtrl);

        Assert.True(Until.True(() => Volatile.Read(ref releases) == 1));
        Assert.Equal(1, Volatile.Read(ref presses));
        Assert.False(monitor.IsHotkeyDown);
    }

    [Fact]
    public void AStoppedMonitorRefusesTheEdgeInsteadOfSwallowingIt()
    {
        var source = new FakeKeyboardDeviceSource();
        source.Add();
        using var monitor = new EvdevKeyboardMonitor(source, Rescan);
        using var server = new ControlSocketServer(monitor, () => "Idle");

        Assert.Equal("error the dictation key is not being tracked", server.Execute("hotkey press"));

        monitor.Start();
        Assert.Equal("ok down", server.Execute("hotkey press"));

        monitor.Stop();
        Assert.Equal("error the dictation key is not being tracked", server.Execute("hotkey release"));
    }

    [Fact]
    public void TheSocketIsThereWhileItRunsAndGoneAfterwards()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("ctl.sock");
        var server = new ControlSocketServer(new FakeHotkeyControl(), () => "Idle", path);

        Assert.True(server.Start());
        Assert.True(server.IsListening);
        Assert.True(File.Exists(path));

        server.Dispose();

        // The unlink is the point: a node left behind is what the next run has to prove stale.
        Assert.False(File.Exists(path));
        Assert.False(server.IsListening);
    }

    // The attribute is for the platform analyser, not for a runner: this assembly is the Linux
    // input layer's and never builds anywhere else. It is on these two because they are the only
    // tests that ask the file system a question only Unix can answer.
    [Fact]
    [SupportedOSPlatform("linux")]
    public void TheDirectoryAndTheSocketAreTheOwnersAlone()
    {
        using var directory = new TemporaryDirectory();
        string folder = Path.Combine(directory.Path, "openramble");
        using var server = new ControlSocketServer(
            new FakeHotkeyControl(),
            () => "Idle",
            Path.Combine(folder, ControlSocket.FileName));

        Assert.True(server.Start());

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(folder));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(server.SocketPath));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void ADirectoryLeftTooOpenIsClosedRatherThanUsed()
    {
        using var directory = new TemporaryDirectory();
        string folder = Path.Combine(directory.Path, "openramble");
        Directory.CreateDirectory(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        using var server = new ControlSocketServer(
            new FakeHotkeyControl(),
            () => "Idle",
            Path.Combine(folder, ControlSocket.FileName));

        Assert.True(server.Start());

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(folder));
    }

    [Fact]
    public void ASocketLeftBehindByADeadRunIsRemovedAndRebound()
    {
        using var diagnostics = new DiagnosticRecorder();
        using var directory = new TemporaryDirectory();
        string path = directory.File("ctl.sock");

        // Exactly what a SIGKILL leaves: a bound node with nobody accepting on it. Bound but never
        // listened on, so connecting to it is refused, and deliberately never disposed - .NET
        // unlinks a Unix socket it bound when it closes it, which is the one courtesy a killed
        // process cannot perform and the whole reason a leftover node exists to be dealt with.
        var abandoned = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        abandoned.Bind(new UnixDomainSocketEndPoint(path));

        Assert.True(File.Exists(path));
        using var server = new ControlSocketServer(new FakeHotkeyControl(), () => "Idle", path);

        Assert.True(server.Start());

        Assert.Equal(path, diagnostics.Field("ctl.stale-removed", "path"));
        Assert.Equal("ok up", Talk(path, "hotkey release"));
        // The finaliser would close it - and unlink the node this test is about - at whatever
        // moment the collector chose.
        GC.KeepAlive(abandoned);
    }

    [Fact]
    public void ASocketSomebodyIsStillAnsweringOnIsNotStolen()
    {
        // A live node and a stale one look identical in the file system, and unlinking the live one
        // would leave two copies both believing they own the dictation key.
        using var diagnostics = new DiagnosticRecorder();
        using var directory = new TemporaryDirectory();
        string path = directory.File("ctl.sock");
        using var first = new ControlSocketServer(new FakeHotkeyControl(), () => "Idle", path);
        Assert.True(first.Start());

        using var second = new ControlSocketServer(new FakeHotkeyControl(), () => "Idle", path);

        Assert.False(second.Start());
        Assert.False(second.IsListening);
        // The second ctl.listen of the run, not the first: the one that succeeded is also in there.
        Assert.Contains(
            diagnostics.Lines,
            line => line.Contains("ctl.listen ", StringComparison.Ordinal)
                && line.Contains("error=in-use", StringComparison.Ordinal));
        // And the one that was there is untouched.
        Assert.True(File.Exists(path));
        Assert.Equal("ok state=Idle key=RightCtrl down=false", Talk(path, "status"));
    }

    [Fact]
    public void OneConnectionCanCarryAWholeDictation()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("ctl.sock");
        var hotkey = new FakeHotkeyControl();
        using var server = new ControlSocketServer(hotkey, () => "Idle", path);
        Assert.True(server.Start());

        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
        {
            ReceiveTimeout = (int)Reply.TotalMilliseconds,
        };
        client.Connect(new UnixDomainSocketEndPoint(path));
        using var pipe = new NetworkStream(client, ownsSocket: false);
        using var writer = new StreamWriter(pipe) { AutoFlush = true, NewLine = "\n" };
        using var reader = new StreamReader(pipe);

        writer.WriteLine("hotkey press");
        Assert.Equal("ok down", reader.ReadLine());
        writer.WriteLine("status");
        Assert.Equal("ok state=Idle key=RightCtrl down=true", reader.ReadLine());
        writer.WriteLine("nonsense");
        Assert.Equal("error unknown command", reader.ReadLine());
        writer.WriteLine("hotkey release");
        Assert.Equal("ok up", reader.ReadLine());
    }

    [Fact]
    public void TheClientSendsALineAndPrintsWhatCameBack()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("ctl.sock");
        using var server = new ControlSocketServer(new FakeHotkeyControl(), () => "Idle", path);
        Assert.True(server.Start());

        Assert.Equal("ok down", ControlSocketClient.Send("hotkey press", path, Reply));
        Assert.Equal("ok up", ControlSocketClient.Send("hotkey toggle", path, Reply));
    }

    [Fact]
    public void TheVerbAnswersWithTheExitCodeAShellCanBranchOn()
    {
        using var directory = new TemporaryDirectory();
        string? previous = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        try
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", directory.Path);
            using var server = new ControlSocketServer(
                new FakeHotkeyControl { IsTracking = false },
                () => "Idle",
                ControlSocket.DefaultPath);
            Assert.True(server.Start());

            var output = new StringWriter();
            var errors = new StringWriter();

            Assert.Equal(
                ControlSocketClient.Misused,
                ControlSocketClient.Run([], output, errors));
            Assert.Contains("usage", errors.ToString(), StringComparison.Ordinal);

            // The app answered, and said no. Different from not answering at all, and a bind that
            // treats the two the same cannot be debugged.
            Assert.Equal(
                ControlSocketClient.Refused,
                ControlSocketClient.Run(["hotkey", "press"], output, errors));
            Assert.Contains("not being tracked", output.ToString(), StringComparison.Ordinal);

            Assert.Equal(
                ControlSocketClient.Success,
                ControlSocketClient.Run(["status"], output, errors));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previous);
        }
    }

    [Fact]
    public void WithNobodyListeningTheClientSaysWhichPathAndWhatToCheck()
    {
        using var directory = new TemporaryDirectory();
        string? previous = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        try
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", directory.Path);
            var output = new StringWriter();
            var errors = new StringWriter();

            Assert.Equal(
                ControlSocketClient.Unreachable,
                ControlSocketClient.Run(["hotkey", "press"], output, errors));

            Assert.Contains(ControlSocket.FileName, errors.ToString(), StringComparison.Ordinal);
            Assert.Contains("Is OpenRamble running?", errors.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previous);
        }
    }

    [Theory]
    [InlineData("/run/user/1000", "/run/user/1000/openramble")]
    [InlineData("/run/user/1000/", "/run/user/1000/openramble")]
    public void TheRuntimeDirectoryIsHonouredWhenItIsOne(string runtime, string expected) =>
        Assert.Equal(expected, ControlSocket.DirectoryFor(runtime, 1000));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    // Relative is treated as absent: a socket resolved against whatever the working directory
    // happened to be is worse than one in a boring place.
    [InlineData("run/user/1000")]
    public void WithoutARuntimeDirectoryTheFallbackNamesTheUser(string? runtime)
    {
        string directory = ControlSocket.DirectoryFor(runtime, 1000);

        Assert.Equal(Path.Combine(Path.GetTempPath(), "openramble-1000"), directory);
        // /tmp is shared, so the user id has to be in the name and not merely in the permissions.
        Assert.NotEqual(ControlSocket.DirectoryFor(runtime, 1001), directory);
    }

    [Fact]
    public void TheDefaultPathIsTheSocketInsideTheDefaultDirectory() =>
        Assert.Equal(
            Path.Combine(ControlSocket.DefaultDirectory, ControlSocket.FileName),
            ControlSocket.DefaultPath);

    [Fact]
    public void TheInputGroupNoticeOffersTheCompositorAsWell()
    {
        // The notice is the only place this alternative can be discovered by the person who needs
        // it: they are looking at a tray that says the key does nothing, and the remedy they were
        // given until now is one they may not be allowed to take.
        var source = new FakeKeyboardDeviceSource();
        source.AddUnreadable("event2", "SIGMACHIP USB Keyboard");
        using var monitor = new EvdevKeyboardMonitor(source, Rescan);
        string? told = null;
        monitor.OnTrackingFailure = message => told = message;

        monitor.Start();

        Assert.NotNull(told);
        // Joining the group is still the first answer offered - it is the one that gives the whole
        // product - and the second one has to be findable, which means naming the file.
        Assert.Contains("gpasswd", told, StringComparison.Ordinal);
        Assert.Contains("compositor", told, StringComparison.Ordinal);
        Assert.Contains("hyprland-example.conf", told, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNoKeyboardNoticeStillOffersNothingOfTheSort()
    {
        // A person with no keyboard plugged in is not helped by a compositor bind for a key that
        // does not exist, and the two remedies must not blur into one paragraph.
        using var monitor = new EvdevKeyboardMonitor(new FakeKeyboardDeviceSource(), Rescan);
        string? told = null;
        monitor.OnTrackingFailure = message => told = message;

        monitor.Start();

        Assert.NotNull(told);
        Assert.DoesNotContain("compositor", told, StringComparison.Ordinal);
    }

    /// <summary>One command over a real socket, the way a shell would send it.</summary>
    private static string Talk(string path, string command) =>
        ControlSocketClient.Send(command, path, Reply);
}

/// <summary>
/// The dictation key with no keyboard and no gesture machine behind it - just the two facts the
/// protocol reads and the one thing it changes.
/// </summary>
internal sealed class FakeHotkeyControl : IHotkeyControl
{
    public DictationHotkey Hotkey { get; set; } = DictationHotkey.RightCtrl;

    public bool IsHotkeyDown { get; private set; }

    /// <summary>Whether the monitor would take an edge at all. False is an application shutting down.</summary>
    public bool IsTracking { get; set; } = true;

    /// <summary>How many times the key actually moved - a repeated press is not a second edge.</summary>
    public int Edges { get; private set; }

    public bool InjectHotkeyEdge(bool down)
    {
        if (!IsTracking) { return false; }

        if (IsHotkeyDown != down)
        {
            IsHotkeyDown = down;
            Edges += 1;
        }

        return true;
    }
}

/// <summary>A directory of one's own, gone when the test ends.</summary>
/// <remarks>
/// Short-named deliberately: a Unix socket path is limited to about a hundred bytes by the kernel,
/// and a temporary directory built out of a full GUID plus a nested folder gets close enough to it
/// that the failure would be a bind error nobody expects.
/// </remarks>
internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"orctl-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A test that left a descriptor open is not a test that should fail here.
        }
    }
}
