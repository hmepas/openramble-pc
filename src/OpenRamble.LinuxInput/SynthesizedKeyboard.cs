using DictationCore;
using OpenRamble.AppCore;

namespace OpenRamble.LinuxInput;

/// <summary>
/// Typing into somebody else's window: <c>wtype</c>, and <c>ydotool</c> where that cannot work.
/// </summary>
/// <remarks>
/// <para>
/// The replacement for <c>SendInput</c> with <c>KEYEVENTF_UNICODE</c>, and it takes two tools
/// because Linux has no single answer:
/// </para>
/// <list type="bullet">
/// <item><description><b><c>wtype</c></b> speaks the <c>virtual-keyboard</c> Wayland protocol.
/// Where it works it is the right tool - it types real Unicode by building a keymap for the text,
/// it needs no daemon and no extra privilege, and it goes to whichever surface has focus. GNOME
/// does not implement that protocol, and neither does anything running under X11, so it is not the
/// only tool.</description></item>
/// <item><description><b><c>ydotool</c></b> writes to <c>/dev/uinput</c> through a daemon and is
/// therefore below the display server: it works on GNOME, on KDE, on Sway and on X11 alike. The
/// price is the daemon and the privilege it holds, which is why it is the fallback and not the
/// default.</description></item>
/// </list>
/// <para>
/// <b>Order matters more than it looks.</b> A tool that fails, fails at startup - <c>wtype</c>
/// exits before typing a character when the compositor will not give it the protocol, and
/// <c>ydotool</c> exits before typing one when its daemon is not running. That is what makes
/// falling through to the next tool safe: a half-typed sentence followed by a retry would say
/// everything twice, and neither failure happens after the first keystroke. The tool that worked is
/// remembered for the rest of the session, so the failing one is not paid for on every dictation.
/// </para>
/// <para>
/// <b>The dictation is never an argument.</b> Both tools are handed the text on standard input -
/// see <see cref="SubprocessCommandRunner"/> for why a command line is the wrong place for it.
/// </para>
/// </remarks>
internal sealed class SynthesizedKeyboard
{
    /// <summary>The Wayland-native tool, tried first.</summary>
    public const string Wtype = "wtype";

    /// <summary>The uinput tool, which works where the protocol is missing.</summary>
    public const string Ydotool = "ydotool";

    /// <summary>Evdev keycode for <c>KEY_LEFTCTRL</c> - the numbering <c>ydotool key</c> takes.</summary>
    private const int KeyLeftCtrl = 29;

    /// <summary>Evdev keycode for <c>KEY_V</c>.</summary>
    private const int KeyV = 47;

    /// <summary>Evdev keycode for <c>KEY_ENTER</c>.</summary>
    private const int KeyEnter = 28;

    /// <summary>
    /// How long a typing run may take.
    /// </summary>
    /// <remarks>
    /// Generous because <c>ydotool</c> pauses between keystrokes by default, and the direct path
    /// carries up to <c>InsertionPathPolicy.SynthesizedKeystrokeLimit</c> characters. The deadline
    /// is not a performance budget - it is the guarantee that a tool which has wedged does not hold
    /// the dictation open for the rest of the session.
    /// </remarks>
    private static readonly TimeSpan TypingBudget = TimeSpan.FromSeconds(15);

    /// <summary>How long a single chord may take. Three keystrokes and no text.</summary>
    private static readonly TimeSpan KeystrokeBudget = TimeSpan.FromSeconds(3);

    private readonly ICommandRunner runner;
    private readonly Lock gate = new();

    /// <summary>The tool that last did the job, tried first from then on.</summary>
    private string? proven;

    public SynthesizedKeyboard(ICommandRunner runner)
    {
        this.runner = runner;
    }

    /// <summary>The tool that last ran, for the diagnostics. <c>null</c> until one has.</summary>
    public string? LastTool
    {
        get { lock (gate) { return proven; } }
    }

    /// <summary>Type text, one tool or the other.</summary>
    /// <remarks>
    /// <c>wtype -</c> and <c>ydotool type --file /dev/stdin</c> both read the whole of standard
    /// input and type it. <c>/dev/stdin</c> rather than <c>-</c>: <c>ydotool</c> takes a path, and
    /// whether it also understands the dash has changed between its versions, while the path is
    /// the pipe itself on every Linux.
    /// </remarks>
    public static IReadOnlyList<string> TypeArguments(string tool) => tool switch
    {
        Wtype => ["-"],
        Ydotool => ["type", "--file", "/dev/stdin"],
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "not a typing tool"),
    };

    /// <summary>Ctrl+V, which is what the clipboard path is for.</summary>
    /// <remarks>
    /// <c>wtype</c> takes modifier names and key names; <c>ydotool key</c> takes evdev codes with
    /// the state after a colon, and the four of them are press-Ctrl, press-V, release-V,
    /// release-Ctrl in that order. Getting the order wrong leaves a modifier stuck down in the
    /// person's session.
    /// </remarks>
    public static IReadOnlyList<string> PasteArguments(string tool) => tool switch
    {
        Wtype => ["-M", "ctrl", "-k", "v", "-m", "ctrl"],
        Ydotool => ["key", $"{KeyLeftCtrl}:1", $"{KeyV}:1", $"{KeyV}:0", $"{KeyLeftCtrl}:0"],
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "not a typing tool"),
    };

    /// <summary>Return, for the dictation that ended with "send".</summary>
    public static IReadOnlyList<string> ReturnArguments(string tool) => tool switch
    {
        Wtype => ["-k", "Return"],
        Ydotool => ["key", $"{KeyEnter}:1", $"{KeyEnter}:0"],
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "not a typing tool"),
    };

    /// <summary>Type the dictation where the cursor is.</summary>
    /// <exception cref="TextInsertionError.TargetUnavailable">
    /// Neither tool could type. Reported rather than swallowed: a dictation that produced nothing
    /// and said nothing is the one failure this product must never have.
    /// </exception>
    public void Type(string text) => Perform("type", TypeArguments, text, TypingBudget);

    /// <summary>Send Ctrl+V to whatever has focus.</summary>
    /// <exception cref="TextInsertionError.TargetUnavailable">Neither tool could.</exception>
    public void Paste() => Perform("paste", PasteArguments, input: null, KeystrokeBudget);

    /// <summary>Press Return.</summary>
    /// <exception cref="TextInsertionError.TargetUnavailable">Neither tool could.</exception>
    public void PressReturn() => Perform("return", ReturnArguments, input: null, KeystrokeBudget);

    /// <summary>
    /// Try the tools in order until one of them does the thing.
    /// </summary>
    /// <remarks>
    /// A tool that is not installed and a tool that ran and refused are treated alike, because from
    /// here they are the same fact: this way of typing is not available on this machine. Both are
    /// logged with the tool's own words, since "wtype: Compositor does not support the virtual
    /// keyboard protocol" is the whole diagnosis and no sentence of ours improves on it.
    /// </remarks>
    private void Perform(
        string what,
        Func<string, IReadOnlyList<string>> arguments,
        string? input,
        TimeSpan budget)
    {
        foreach (string tool in Order())
        {
            CommandResult result = runner.Run(new CommandRequest(
                tool,
                arguments(tool),
                input,
                budget,
                CaptureOutput: true));

            if (result.Succeeded)
            {
                lock (gate) { proven = tool; }
                return;
            }

            DiagnosticLog.Emit(
                "insert.tool-failed",
                ("what", what),
                ("tool", tool),
                ("installed", result.Started),
                ("why", result.Failure));
        }

        // Half a sentence in somebody's editor would be worse than a reported failure, and nothing
        // here types half a sentence: both tools fail before their first keystroke. The recognized
        // text stays recoverable through the notice either way.
        throw new TextInsertionError.TargetUnavailable();
    }

    /// <summary>The tools to try, best first.</summary>
    private IEnumerable<string> Order()
    {
        string? first;
        lock (gate) { first = proven; }

        if (first is not null)
        {
            yield return first;
        }

        if (first != Wtype) { yield return Wtype; }

        if (first != Ydotool) { yield return Ydotool; }
    }
}
