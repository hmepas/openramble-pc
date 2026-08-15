using System.Net.Sockets;
using System.Text;

namespace OpenRamble.LinuxInput;

/// <summary>
/// The other end of <see cref="ControlSocket"/>: one command, one reply, one exit code.
/// </summary>
/// <remarks>
/// <para>
/// This is what <c>OpenRamble.App.Linux ctl hotkey press</c> runs. It exists so the feature can be
/// used, and demonstrated, with nothing installed beyond the application itself - a compositor
/// config that assumes <c>socat</c> is a config that does not work on the machine it is written on
/// until somebody notices why.
/// </para>
/// <para>
/// <b>It is not the fast path, and the difference matters on the release edge.</b> Every run of
/// this is a .NET process start: the runtime, the assemblies, the JIT, and only then a connect and
/// a write that take microseconds. On the press edge the cost is invisible - dictation starts a
/// fraction later. On the release edge it is added to the time between letting the key go and the
/// text appearing, which is the one latency in this product a person actually feels. Where that
/// matters, <c>socat</c> or <c>nc -U</c> speaks the same protocol in a fraction of the time, and
/// the README recommends it for hold binds for exactly this reason.
/// </para>
/// </remarks>
public static class ControlSocketClient
{
    /// <summary>The verb on the application's own command line.</summary>
    public const string Verb = "ctl";

    /// <summary>Everything went as asked - the app answered, and it answered <c>ok</c>.</summary>
    public const int Success = 0;

    /// <summary>The app answered, and the answer was an error. Its text is on standard output.</summary>
    public const int Refused = 1;

    /// <summary>Nothing was asked: no command after the verb.</summary>
    public const int Misused = 2;

    /// <summary>Nobody was listening, or the conversation broke. OpenRamble is probably not running.</summary>
    public const int Unreachable = 3;

    /// <summary>
    /// How long to wait for a reply.
    /// </summary>
    /// <remarks>
    /// Generous, because the only thing that can make it slow is the application being busy with
    /// the very dictation this command is about - and finite, because a bind that hangs is a key
    /// that hangs. Two seconds is longer than any answer here has ever taken and shorter than a
    /// person's patience with a keypress that did nothing.
    /// </remarks>
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(2);

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Run the <c>ctl</c> verb: everything after it is the command, joined with spaces.
    /// </summary>
    /// <remarks>
    /// Joined rather than required as one argument, so that <c>ctl hotkey press</c> works next to
    /// <c>ctl "hotkey press"</c>. A compositor config quotes neither reliably, and refusing the
    /// spelling somebody typed teaches nothing.
    /// </remarks>
    public static int Run(IReadOnlyList<string> arguments, TextWriter output, TextWriter errors)
    {
        string command = string.Join(' ', arguments).Trim();
        if (command.Length == 0)
        {
            errors.WriteLine(
                $"usage: OpenRamble.App.Linux {Verb} <command>\n"
                + "commands: hotkey press | hotkey release | hotkey toggle | status");
            return Misused;
        }

        string path = ControlSocket.DefaultPath;
        try
        {
            string reply = Send(command, path, ReplyTimeout);
            output.WriteLine(reply);
            return reply.StartsWith(ControlSocket.Ok, StringComparison.Ordinal) ? Success : Refused;
        }
        catch (Exception error) when (error is SocketException or IOException
            or ObjectDisposedException or ArgumentException)
        {
            errors.WriteLine(
                $"OpenRamble.App.Linux {Verb}: {path}: {error.Message}\n"
                + "Is OpenRamble running?");
            return Unreachable;
        }
    }

    /// <summary>
    /// Connect, send one line, read one line back.
    /// </summary>
    /// <exception cref="SocketException">Nothing is listening on that path.</exception>
    /// <exception cref="IOException">The connection ended before a reply arrived.</exception>
    public static string Send(string command, string socketPath, TimeSpan timeout)
    {
        int milliseconds = (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
        {
            SendTimeout = milliseconds,
            ReceiveTimeout = milliseconds,
        };
        socket.Connect(new UnixDomainSocketEndPoint(socketPath));

        using var pipe = new NetworkStream(socket, ownsSocket: false);
        using var writer = new StreamWriter(pipe, Utf8) { AutoFlush = true, NewLine = "\n" };
        writer.WriteLine(command);
        // Half-closed rather than left open: it tells the server this connection has nothing more
        // to say, so a reply is the only thing left in the exchange.
        socket.Shutdown(SocketShutdown.Send);

        using var reader = new StreamReader(pipe, Utf8, detectEncodingFromByteOrderMarks: false);
        return reader.ReadLine()
            ?? throw new IOException("the control socket closed without answering");
    }
}
