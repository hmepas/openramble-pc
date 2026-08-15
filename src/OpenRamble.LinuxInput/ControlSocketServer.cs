using System.Net.Sockets;
using System.Text;
using OpenRamble.AppCore;

namespace OpenRamble.LinuxInput;

/// <summary>
/// The dictation key, driven by something that is not a keyboard.
/// </summary>
/// <remarks>
/// <para>
/// Three members, and all three are about one key: the configured one. There is deliberately no
/// way to ask for an arbitrary keycode - the socket is a second hand on the same hotkey, not a
/// remote keyboard, and a port that could inject any key would be one that could type.
/// </para>
/// <para>
/// <see cref="EvdevKeyboardMonitor"/> implements it, which is what makes the two sources one path:
/// the injected edge goes through the same gate, the same chord bookkeeping, the same
/// <c>HotkeyGestureMachine</c> and the same dispatch as a key read from <c>/dev/input</c>.
/// </para>
/// </remarks>
public interface IHotkeyControl
{
    /// <summary>The key the person chose. For <c>status</c>, which exists to be read by a human.</summary>
    DictationHotkey Hotkey { get; }

    /// <summary>
    /// Whether that key is down right now, from whichever source put it there.
    /// </summary>
    /// <remarks>
    /// One answer for both sources, which is what stops a toggle wedging: see
    /// <see cref="EvdevKeyboardMonitor.IsHotkeyDown"/>.
    /// </remarks>
    bool IsHotkeyDown { get; }

    /// <summary>
    /// Put the configured key at <paramref name="down"/>, as if it had been pressed or released.
    /// </summary>
    /// <returns>
    /// Whether the monitor was tracking and took it. <c>false</c> is a monitor that has been
    /// stopped - the application is shutting down - and the caller has to say so rather than
    /// report a press that went nowhere.
    /// </returns>
    bool InjectHotkeyEdge(bool down);
}

/// <summary>
/// The control socket, listening.
/// </summary>
/// <remarks>
/// <para>
/// The protocol it speaks, and why it exists, are written down once in <see cref="ControlSocket"/>.
/// This is the transport and nothing else: a Unix stream socket in an owner-only directory, an
/// accept loop, and one line in for one line out.
/// </para>
/// <para>
/// <b>Every command is serialised.</b> <c>hotkey toggle</c> reads the key's state and then moves
/// it, and two connections toggling at once would otherwise both read "up" and both press. One
/// lock over the whole of <see cref="Execute"/> costs nothing at this rate - a person's fingers -
/// and makes the pair atomic. It does not, and cannot, serialise against the keyboard: see the
/// interaction note on <see cref="EvdevKeyboardMonitor.IsHotkeyDown"/>.
/// </para>
/// <para>
/// <b>No authentication, and none is possible.</b> The directory is 0700 and the socket 0600, so
/// the boundary is the file system's: anything that can reach the socket is already running as
/// this user and could send the key events itself. What the socket adds over that is nothing, and
/// what it must therefore never grow is a command that does more than the hotkey does.
/// </para>
/// </remarks>
public sealed class ControlSocketServer : IDisposable
{
    /// <summary>
    /// Owner-only, on both the directory and the socket. See the class remarks: this is the whole
    /// access control there is.
    /// </summary>
    private const UnixFileMode PrivateDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// How many connections may be waiting to be accepted.
    /// </summary>
    /// <remarks>
    /// A press bind and a release bind can arrive within a few milliseconds of each other and the
    /// second must not be refused, but nothing here will ever queue more than a handful. Small on
    /// purpose: a backlog that absorbs a runaway loop is a backlog that hides one.
    /// </remarks>
    private const int Backlog = 8;

    /// <summary>How long a probe waits to find out whether somebody is already listening.</summary>
    private static readonly TimeSpan LivenessProbe = TimeSpan.FromMilliseconds(250);

    private readonly Lock gate = new();
    private readonly IHotkeyControl hotkey;
    private readonly Func<string> describeState;
    private Socket? listener;
    private CancellationTokenSource? stopping;
    private bool disposed;

    /// <param name="hotkey">The monitor whose gesture machine the injected edges go into.</param>
    /// <param name="describeState">
    /// What <c>status</c> answers with - the application's dictation state, read at the moment it
    /// is asked. A delegate because this assembly must not know what an <c>AppState</c> is, the
    /// same way <see cref="LinuxTextInserter"/> takes the held modifiers as one.
    /// </param>
    /// <param name="socketPath">Where to listen. Defaults to <see cref="ControlSocket.DefaultPath"/>.</param>
    public ControlSocketServer(IHotkeyControl hotkey, Func<string> describeState, string? socketPath = null)
    {
        this.hotkey = hotkey;
        this.describeState = describeState;
        SocketPath = socketPath ?? ControlSocket.DefaultPath;
    }

    /// <summary>The path being listened on, or that would be.</summary>
    public string SocketPath { get; }

    /// <summary>Whether the socket is open and accepting.</summary>
    public bool IsListening
    {
        get { lock (gate) { return listener is not null; } }
    }

    /// <summary>
    /// Create the directory, clear a socket left behind by a dead run, bind and accept.
    /// </summary>
    /// <returns>Whether it is listening.</returns>
    /// <remarks>
    /// <para>
    /// <b>A failure here is not fatal and is not shown.</b> The socket is the alternative path; the
    /// keyboard is the product. An application that refused to start because a runtime directory
    /// was read-only would be trading the feature that works for the one that does not, so the
    /// failure goes to the diagnostic log and the app carries on without it.
    /// </para>
    /// <para>
    /// <b>Stale is proved, not assumed.</b> A socket file outlives the process that bound it -
    /// nothing unlinks it on SIGKILL - so a leftover node is the ordinary state after a crash and
    /// must be removed. But an existing node that somebody is still accepting on is a running copy,
    /// and unlinking that one would leave both apps believing they own the key. So it is connected
    /// to first: an answer means live and this run does without, a refusal means stale and it goes.
    /// (The instance lock upstream should have caught the live case already. Should have.)
    /// </para>
    /// </remarks>
    public bool Start()
    {
        lock (gate)
        {
            if (disposed || listener is not null) { return listener is not null; }
        }

        Socket socket;
        try
        {
            // Not defensive programming: it is how the platform analyser is told what this
            // assembly's name already says. Everything below this line - the file mode, the Unix
            // endpoint - exists on one operating system, and asking is more honest than
            // suppressing a warning that stops being true the day somebody references this from
            // somewhere else. PlatformNotSupportedException is a NotSupportedException, so the
            // catch below already treats it as "no socket this run".
            if (!OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException("the control socket is a Unix socket");
            }

            string folder = Path.GetDirectoryName(SocketPath)
                ?? throw new IOException($"{SocketPath} has no directory to create");
            Directory.CreateDirectory(folder, PrivateDirectory);
            // Again, explicitly: CreateDirectory applies the mode only to a directory it creates,
            // and XDG_RUNTIME_DIR's own contents are whatever a previous run - or a packaging
            // mistake - left behind.
            File.SetUnixFileMode(folder, PrivateDirectory);

            if (!ClearStaleSocket()) { return false; }

            socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(SocketPath));
            socket.Listen(Backlog);
            File.SetUnixFileMode(SocketPath, PrivateFile);
        }
        catch (Exception error) when (error is IOException or SocketException
            or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            DiagnosticLog.EmitException("ctl.listen-failed", error, ("path", SocketPath));
            return false;
        }

        var cancellation = new CancellationTokenSource();
        bool kept;
        lock (gate)
        {
            kept = !disposed && listener is null;
            if (kept)
            {
                listener = socket;
                stopping = cancellation;
            }
        }

        if (!kept)
        {
            socket.Dispose();
            cancellation.Dispose();
            Remove();
            return false;
        }

        DiagnosticLog.Emit("ctl.listen", ("ok", true), ("path", SocketPath));
        _ = Task.Run(() => Accept(socket, cancellation.Token), CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Stop listening and take the socket out of the file system.
    /// </summary>
    /// <remarks>
    /// The unlink is the point. A node left behind is what the next run has to prove stale, and
    /// proving it costs a connection attempt with a timeout on the path a person is waiting on.
    /// </remarks>
    public void Dispose()
    {
        Socket? listening;
        CancellationTokenSource? cancellation;
        lock (gate)
        {
            if (disposed) { return; }

            disposed = true;
            listening = listener;
            listener = null;
            cancellation = stopping;
            stopping = null;
        }

        cancellation?.Cancel();
        listening?.Dispose();
        cancellation?.Dispose();
        if (listening is null) { return; }

        Remove();
        DiagnosticLog.Emit("ctl.stopped", ("path", SocketPath));
    }

    /// <summary>
    /// One command line to one reply line. The protocol, without the socket.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so the whole table can be checked without a file system - the
    /// same seam the device source is behind. What reaches the diagnostic log is the verb this
    /// recognised and never the caller's text: an unrecognised line is somebody else's input, and
    /// the log's rule about what may be written does not have an exception for a local socket.
    /// </remarks>
    internal string Execute(string line)
    {
        string command = line.Trim();
        lock (gate)
        {
            switch (command)
            {
                case "hotkey press": return Edge(true, command);
                case "hotkey release": return Edge(false, command);
                case "hotkey toggle": return Edge(!hotkey.IsHotkeyDown, command);
                case "status":
                    DiagnosticLog.Emit("ctl.command", ("command", command), ("ok", true));
                    return $"{ControlSocket.Ok} state={Describe()} key={hotkey.Hotkey} "
                        + $"down={(hotkey.IsHotkeyDown ? "true" : "false")}";

                default:
                    DiagnosticLog.Emit("ctl.command", ("command", "unknown"), ("ok", false));
                    return command.Length == 0
                        ? $"{ControlSocket.Error} empty command"
                        : $"{ControlSocket.Error} unknown command";
            }
        }
    }

    /// <summary>Move the key, and say where it ended up. The gate is held.</summary>
    private string Edge(bool down, string command)
    {
        bool taken = hotkey.InjectHotkeyEdge(down);
        DiagnosticLog.Emit("ctl.command", ("command", command), ("ok", taken));
        return taken
            ? $"{ControlSocket.Ok} {(down ? "down" : "up")}"
            : $"{ControlSocket.Error} the dictation key is not being tracked";
    }

    /// <summary>
    /// The application's state, or what to say when asking for it throws.
    /// </summary>
    /// <remarks>
    /// The delegate reaches into the shell, and a <c>status</c> that took the process down with it
    /// would be a diagnostic command that is worse than no diagnostic command.
    /// </remarks>
    private string Describe()
    {
        try
        {
            string state = describeState();
            return state.Length > 0 ? DiagnosticLog.Sanitize(state) : "unknown";
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException)
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Deal with a socket file that is already there.
    /// </summary>
    /// <returns>Whether the path is now free to bind.</returns>
    private bool ClearStaleSocket()
    {
        if (!File.Exists(SocketPath)) { return true; }

        if (IsSomebodyListening())
        {
            DiagnosticLog.Emit("ctl.listen", ("ok", false), ("error", "in-use"), ("path", SocketPath));
            return false;
        }

        File.Delete(SocketPath);
        DiagnosticLog.Emit("ctl.stale-removed", ("path", SocketPath));
        return true;
    }

    /// <summary>Whether a connection to the existing node is answered - which means a live copy.</summary>
    private bool IsSomebodyListening()
    {
        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            return probe.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath))
                .Wait(LivenessProbe);
        }
        catch (Exception error) when (error is SocketException or IOException
            or AggregateException or ArgumentException)
        {
            // Connection refused is the answer this is looking for: a socket node whose owner is
            // gone. So is a path too long for the kernel, which cannot be bound either - and that
            // one is caught by the bind that follows.
            return false;
        }
    }

    private void Remove()
    {
        try
        {
            File.Delete(SocketPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Somebody else's now, or on a file system that went away with the session. Either way
            // there is nothing left to do about it on the way out.
        }
    }

    /// <summary>Take connections until the socket is closed under us.</summary>
    private async Task Accept(Socket listening, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listening.AcceptAsync(token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException
                or ObjectDisposedException or SocketException)
            {
                // Dispose closed the listener. The only way out of this loop, and the ordinary one.
                return;
            }

            // Not awaited: a client that connects and then says nothing must not stop the next one
            // being accepted, and the bind style this exists for opens one connection per keypress.
            _ = Task.Run(() => Converse(client, token), CancellationToken.None);
        }
    }

    /// <summary>One connection: read lines, answer each, until it ends.</summary>
    private async Task Converse(Socket client, CancellationToken token)
    {
        try
        {
            using (client)
            using (var pipe = new NetworkStream(client, ownsSocket: false))
            using (var reader = new StreamReader(pipe, Utf8, detectEncodingFromByteOrderMarks: false))
            using (var writer = new StreamWriter(pipe, Utf8) { AutoFlush = true, NewLine = "\n" })
            {
                while (await reader.ReadLineAsync(token).ConfigureAwait(false) is string line)
                {
                    // Written synchronously: the reply is a few dozen bytes into a socket buffer
                    // that is kilobytes wide, and the caller is a shell waiting for one line.
                    writer.WriteLine(Execute(line));
                }
            }
        }
        catch (Exception error) when (error is IOException or SocketException
            or ObjectDisposedException or OperationCanceledException)
        {
            // The client hung up mid-sentence, or the server is going down. Neither is worth a line
            // in a log that exists to explain dictations.
        }
    }

    /// <summary>No byte order mark: this is a line protocol read by shells.</summary>
    private static UTF8Encoding Utf8 { get; } = new(encoderShouldEmitUTF8Identifier: false);
}
