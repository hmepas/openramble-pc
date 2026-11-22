using System.Globalization;
using System.Text;

namespace OpenRamble.AppCore;

/// <summary>
/// The opt-in diagnostic log: one line per lifecycle event, appended and flushed as it happens.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off unless asked for.</b> Nothing is written until <see cref="Install"/> has been called,
/// and the application only calls it when <c>OPENRAMBLE_DIAG</c> (or <c>--diag &lt;path&gt;</c>)
/// names a file. A dictation product that writes a file about every session by default is a
/// product that keeps a record of when a person spoke, and that is not ours to keep.
/// </para>
/// <para>
/// <b>What may be written, and the rule that decides.</b> Only what the application <i>did</i>:
/// state names, gesture kinds, durations, counts, error codes. Never what the person
/// <i>said</i> or <i>typed</i>. Concretely, the following are forbidden in every field of every
/// event, and no future event may relax it:
/// </para>
/// <list type="bullet">
/// <item><description>dictated text, recognized words, any substring of either - lengths in
/// characters are the only permitted shadow of them;</description></item>
/// <item><description>keystroke identities, other than the configured hotkey itself. The
/// keyboard hook sees every key on the machine and must stay the twenty lines of comparison it
/// is;</description></item>
/// <item><description>clipboard contents, in whole or in part;</description></item>
/// <item><description>the person's file or window names. Our own take path is not written
/// either: it carries a session identifier and nothing else worth having.</description></item>
/// </list>
/// <para>
/// Notice messages are the one piece of prose that reaches the log, and they are safe because
/// they are ours: every one of them is a fixed template written in this repository. A notice
/// that ends in an exception's message is logged as its template and the exception's type name
/// (<c>DictationNotice.LogMessage</c>). The recognized text a failure hands back travels in
/// <c>DictationNotice.RecoverableText</c>, which is never logged.
/// </para>
/// <para>
/// <b>One line, flushed.</b> The process this log exists to explain is one that hangs and gets
/// killed, so a buffered tail is a tail that is never read. Every write ends in a flush, and
/// every event is a single line so that the file can be read with <c>findstr</c> while the
/// application is still running.
/// </para>
/// <para>
/// <b>Written where the event happens</b> - which includes the keyboard hook's thread and the
/// inside of the dictation kernel's lock. That is a deliberate trade and the reason a write is
/// one format and one flush and nothing else. Handing lines to a background writer would make
/// those callers cheaper and lose exactly the lines that matter: the last ones before the hang.
/// When no log is installed - every run but the one being debugged - all of this is a null check.
/// </para>
/// </remarks>
public sealed class DiagnosticLog : IDisposable
{
    /// <summary>The environment variable that names the file. Its presence is the switch.</summary>
    public const string EnvironmentVariable = "OPENRAMBLE_DIAG";

    /// <summary>The command-line spelling of the same switch, for a shortcut's target field.</summary>
    public const string CommandLineFlag = "--diag";

    /// <summary>
    /// The longest a single field may be. Long enough for a notice template and a stack, short
    /// enough that a runaway value cannot turn the log into the thing that fills the disk.
    /// </summary>
    private const int MaximumValueLength = 2_000;

    private readonly Lock gate = new();
    private readonly TextWriter sink;
    private readonly Func<DateTimeOffset> now;
    private readonly bool ownsSink;
    private bool disposed;

    /// <param name="sink">Where the lines go. Disposed with this log when <paramref name="ownsSink"/>.</param>
    /// <param name="now">The clock, injectable so the format can be checked without waiting.</param>
    public DiagnosticLog(TextWriter sink, Func<DateTimeOffset>? now = null, bool ownsSink = false)
    {
        this.sink = sink;
        this.now = now ?? (static () => DateTimeOffset.UtcNow);
        this.ownsSink = ownsSink;
    }

    /// <summary>
    /// The log the application is writing to, or <c>null</c> - which is the normal case.
    /// </summary>
    /// <remarks>
    /// Ambient on purpose. The alternative is threading a logger through the keyboard hook, the
    /// capture, the inserter and every port between them, which changes the shape of code whose
    /// shape is the product; and the events worth having are exactly the ones from the places a
    /// parameter cannot reach - a hook callback, an audio thread, an unhandled exception.
    /// </remarks>
    public static DiagnosticLog? Current { get; private set; }

    public static bool IsEnabled => Current is not null;

    /// <summary>
    /// The dictation session the events being written right now belong to.
    /// </summary>
    /// <remarks>
    /// Ambient for the same reason the log itself is: one dictation is written by the keyboard
    /// hook, the capture, the engine and the inserter, and threading a number through four ports
    /// would change the shape of code whose shape is the product. It is published once, when the
    /// kernel opens a session, and every event written afterwards carries it - which is what
    /// makes an <c>insert.begin</c> attributable to the <c>capture.start</c> it came from in a
    /// file holding a dozen sessions.
    /// </remarks>
    public static int Session
    {
        get => Volatile.Read(ref session);
        set => Volatile.Write(ref session, value);
    }

    private static int session;

    /// <summary>
    /// Point the ambient log at <paramref name="log"/>, returning what undoes it.
    /// </summary>
    /// <remarks>
    /// The application installs one at startup and never removes it. The handle exists for the
    /// tests, which must not leave a log installed for whichever test runs next.
    /// </remarks>
    public static IDisposable Install(DiagnosticLog log)
    {
        DiagnosticLog? previous = Current;
        Current = log;
        return new Installation(previous);
    }

    /// <summary>Write to the ambient log if there is one. Cheap and silent if there is not.</summary>
    public static void Emit(string name, params (string Key, object? Value)[] fields) =>
        Current?.Write(name, fields);

    /// <summary>
    /// Write an event carrying an exception: its type, its message and its stack, on one line.
    /// </summary>
    /// <remarks>
    /// The message is included because a Win32 or ONNX failure says everything useful in it, and
    /// none of those messages can contain a dictation - the pipeline's text never reaches an
    /// exception constructor.
    /// </remarks>
    public static void EmitException(string name, Exception error, params (string Key, object? Value)[] fields)
    {
        // Read once: a log uninstalled on another thread between a check and a write would
        // turn the write into a NullReferenceException in whatever path was reporting a failure.
        if (Current is not { } log) { return; }

        var all = new List<(string Key, object? Value)>(fields.Length + 3);
        all.AddRange(fields);
        all.Add(("error", error.GetType().Name));
        all.Add(("message", error.Message));
        all.Add(("stack", error.StackTrace ?? "none"));
        log.Write(name, all);
    }

    /// <summary>
    /// Where the log should go, from the environment and the command line.
    /// </summary>
    /// <remarks>
    /// The command line wins: a person who typed <c>--diag</c> into a shortcut is answering the
    /// question more recently than whoever set the variable. A flag with no path after it is
    /// ignored rather than guessed at - a log in a surprise location is worse than none.
    /// </remarks>
    public static string? PathFrom(string? environmentValue, IReadOnlyList<string> arguments)
    {
        for (int index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], CommandLineFlag, StringComparison.Ordinal)
                && arguments[index + 1].Trim().Length > 0)
            {
                return arguments[index + 1].Trim();
            }
        }

        string? fromEnvironment = environmentValue?.Trim();
        return fromEnvironment is { Length: > 0 } ? fromEnvironment : null;
    }

    /// <summary>
    /// One event, as it appears in the file.
    /// </summary>
    /// <remarks>
    /// <c>key=value</c> after an ISO instant and the event name, because that is what survives a
    /// grep on a Windows machine with no tools on it. Values are sanitised, never quoted: a
    /// quoting scheme invites a value that needs it, and a value that needs it is a value that
    /// should not have been logged.
    /// </remarks>
    public static string FormatLine(
        DateTimeOffset at,
        string name,
        IReadOnlyList<(string Key, object? Value)> fields)
    {
        var line = new StringBuilder();
        line.Append(at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
        line.Append(' ');
        line.Append(Sanitize(name));

        foreach ((string key, object? value) in fields)
        {
            line.Append(' ');
            line.Append(Sanitize(key));
            line.Append('=');
            line.Append(Sanitize(Render(value)));
        }

        return line.ToString();
    }

    /// <summary>
    /// Make a value safe to put on a line: no newlines, no tabs, no spaces, bounded length.
    /// </summary>
    /// <remarks>
    /// A stack trace arrives as a dozen lines and has to stay one event - a reader following a
    /// hang cannot tell an interleaved half-event from a whole one. Line breaks become
    /// <c>" | "</c> rather than disappearing, so the frames stay readable.
    /// </remarks>
    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) { return "none"; }

        var text = new StringBuilder(raw.Length);
        bool pendingSeparator = false;
        foreach (char unit in raw)
        {
            if (unit is '\r' or '\n')
            {
                pendingSeparator = text.Length > 0;
                continue;
            }

            // The indentation a stack trace puts in front of every frame belongs to the line
            // break, not to the frame. Kept, it would be three underscores per frame.
            if (pendingSeparator && (unit == ' ' || unit == '\t'))
            {
                continue;
            }

            if (pendingSeparator)
            {
                text.Append(" | ".AsSpan());
                pendingSeparator = false;
            }

            text.Append(char.IsControl(unit) || unit == ' ' ? '_' : unit);
        }

        string flattened = text.ToString();
        if (flattened.Length == 0) { return "none"; }

        return flattened.Length <= MaximumValueLength
            ? flattened
            : string.Concat(flattened.AsSpan(0, MaximumValueLength), "…");
    }

    /// <summary>Milliseconds, whole: the log answers "how long", not "how long exactly".</summary>
    public static string Milliseconds(TimeSpan span) =>
        ((long)Math.Round(span.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture);

    public void Write(string name, params (string Key, object? Value)[] fields) =>
        Write(name, (IReadOnlyList<(string, object?)>)fields);

    public void Write(string name, IReadOnlyList<(string Key, object? Value)> fields)
    {
        string line = FormatLine(now(), name, fields);
        lock (gate)
        {
            if (disposed) { return; }

            try
            {
                sink.WriteLine(line);
                sink.Flush();
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException
                or UnauthorizedAccessException)
            {
                // A diagnostic that can take the application down is worse than no diagnostic.
                // The disk filled or the file was deleted under us; either way the session the
                // log exists to explain must keep running.
                disposed = true;
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) { return; }
            disposed = true;
        }

        if (ownsSink) { sink.Dispose(); }
    }

    private static string Render(object? value) => value switch
    {
        null => "none",
        string text => text,
        bool flag => flag ? "true" : "false",
        TimeSpan span => Milliseconds(span),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "none",
    };

    private sealed class Installation : IDisposable
    {
        private readonly DiagnosticLog? previous;

        public Installation(DiagnosticLog? previous)
        {
            this.previous = previous;
        }

        public void Dispose() => Current = previous;
    }
}
