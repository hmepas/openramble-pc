using System.Text;
using DictationCore;
using OpenRamble.AppCore;

namespace OpenRamble.LinuxInput;

/// <summary>What was on the clipboard, as far as text-only tooling can tell.</summary>
/// <param name="Text">The text that was there, or <c>null</c> if there was none.</param>
/// <param name="HasUnrestorableContents">
/// Something was offered that is not text - an image, a file list, a rich-text-only offer. The
/// fact matters even though the bytes cannot be had: putting the clipboard back afterwards is
/// impossible, and that has to be said out loud rather than silently turned into an empty
/// clipboard.
/// </param>
internal readonly record struct ClipboardContents(string? Text, bool HasUnrestorableContents)
{
    public static ClipboardContents Nothing { get; } = new(null, false);

    public bool IsEmpty => Text is null && !HasUnrestorableContents;
}

/// <summary>
/// The synthetic format numbers a Linux snapshot uses inside
/// <see cref="ClipboardSnapshot"/>.
/// </summary>
/// <remarks>
/// The snapshot is keyed by <c>uint</c> because on Windows those are registered clipboard format
/// ids. Linux has MIME strings instead, and exactly two states worth telling apart, so two numbers
/// are enough - and reusing the type is what lets the whole of
/// <see cref="ClipboardSnapshotLedger"/> be shared. That ledger is where the hard part lives: a
/// second dictation landing inside the first one's restore window must inherit the person's
/// contents rather than snapshot our own text over them.
/// </remarks>
internal static class LinuxClipboardFormats
{
    /// <summary>UTF-8 text, the only thing that can be put back.</summary>
    public const uint Utf8Text = 1;

    /// <summary>
    /// A marker: there was something here and it cannot be restored. Carries no bytes - its
    /// presence is the whole message, the way Windows's exclusion format works.
    /// </summary>
    public const uint Unrestorable = 2;
}

/// <summary>
/// A stand-in for <c>GetClipboardSequenceNumber</c>, which Wayland has no equivalent of.
/// </summary>
/// <remarks>
/// <para>
/// The restore has one question to answer: is the clipboard still holding <b>our</b> dictation, or
/// has the person copied something of their own since? Windows answers it with a counter the
/// system owns. Nothing on this platform counts selections, so the fingerprint of the contents
/// stands in: the dictation is written, its fingerprint is remembered, and the restore only acts if
/// the clipboard still fingerprints the same.
/// </para>
/// <para>
/// Two differences from a real counter, both harmless and both deliberate. A person who copies the
/// exact text that was just dictated is indistinguishable from nobody having copied anything - and
/// the outcome there is that we replace their copy with content identical to it. And a person who
/// copies something, then copies it back, looks like no change at all - which is also true.
/// </para>
/// <para>
/// The number is never logged and never leaves the process. It is a 32-bit digest of what a person
/// had on their clipboard, which is not something to write to a file.
/// </para>
/// </remarks>
internal static class ClipboardFingerprint
{
    private const uint FnvOffsetBasis = 2_166_136_261;
    private const uint FnvPrime = 16_777_619;

    /// <summary>Nothing on the clipboard fingerprints as this.</summary>
    public const uint Empty = 0;

    /// <summary>Anything we cannot read as text fingerprints alike: it is one state, not many.</summary>
    public const uint Unrestorable = 1;

    public static uint Of(ClipboardContents contents) => contents.Text is string text
        ? Of(text)
        : contents.HasUnrestorableContents ? Unrestorable : Empty;

    public static uint Of(string text)
    {
        uint hash = FnvOffsetBasis;
        foreach (byte unit in Encoding.UTF8.GetBytes(text))
        {
            hash = (hash ^ unit) * FnvPrime;
        }

        // The two low values are reserved for "nothing" and "not text", and a text that hashed to
        // one of them would be mistaken for them.
        return hash <= Unrestorable ? hash + 2 : hash;
    }
}

/// <summary>
/// The clipboard, through <c>wl-clipboard</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the tools and not the protocol.</b> Owning a Wayland selection means being a Wayland
/// client with a surface and a serial from a recent input event, and offering data means answering
/// a file-descriptor request on the event loop. <c>wl-copy</c> already is that client: it forks,
/// holds the offer, and serves it until somebody takes the selection away. Re-implementing it in
/// this process would add an event loop to an application whose whole clipboard interaction lasts a
/// second.
/// </para>
/// <para>
/// <b>What is promised, and what cannot be.</b> <see cref="LinuxClipboardRestrictions"/> is the
/// policy; this is the half that carries it out. The promise kept here is the clearing: the
/// dictation does not stay on the clipboard after the paste - the person's own text comes back, or
/// the clipboard is emptied. The best-effort marker is <c>wl-copy --sensitive</c>, which offers
/// KDE's <c>x-kde-passwordManagerHint</c> so that Klipper does not record the entry; nothing else
/// on Linux honours anything, and a wl-clipboard too old to know the flag is detected once and not
/// asked again.
/// </para>
/// <para>
/// <b>The limitation, stated rather than hidden.</b> Only text can be put back. If the person had
/// an image on the clipboard, <c>wl-paste</c> can be asked for its bytes but <c>wl-copy</c> cannot
/// be given them back with their MIME type through this seam, so the honest outcome is to clear the
/// clipboard and report that the restore failed. Losing an image is bad; leaving a dictation
/// sitting in the clipboard afterwards would be worse, and pretending it was restored would be
/// worst.
/// </para>
/// </remarks>
internal sealed class WaylandClipboard
{
    public const string Copy = "wl-copy";

    public const string Paste = "wl-paste";

    /// <summary>
    /// The type asked for and offered.
    /// </summary>
    /// <remarks>
    /// Written explicitly on both sides rather than left to be inferred, and both directions have a
    /// reason. Reading: <c>wl-paste</c> with no type guesses from the offer and gets it wrong -
    /// against a clipboard filled by a browser it decides on <c>application/octet-stream</c> and
    /// refuses. Writing: <c>wl-copy</c> with no type runs the text through the system's MIME
    /// detection, and a dictation that happens to begin with a tag is offered as <c>text/html</c>,
    /// which a plain-text field will not take. Naming the type costs nothing - wl-copy still offers
    /// the whole text family (<c>text/plain;charset=utf-8</c>, <c>STRING</c>, <c>UTF8_STRING</c>,
    /// <c>TEXT</c>) alongside it, which is what makes the paste work everywhere.
    /// </remarks>
    public const string TextType = "text/plain";

    /// <summary>
    /// The other names for text, in the order they are worth trying.
    /// </summary>
    /// <remarks>
    /// Only reached when an application offers text without offering <see cref="TextType"/>. Rare -
    /// GTK, Qt and every browser offer it - but the alternative is calling that person's clipboard
    /// unrestorable and clearing it, which is the one outcome worth a second look.
    /// </remarks>
    private static readonly string[] TextAliases =
    [
        "text/plain;charset=utf-8",
        "UTF8_STRING",
        "STRING",
        "TEXT",
    ];

    /// <summary>Long enough for a tool that talks to the compositor, short enough to notice.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    private readonly ICommandRunner runner;
    private readonly Lock gate = new();

    /// <summary>Whether this machine's <c>wl-copy</c> knows <c>--sensitive</c>. Assumed until denied.</summary>
    private bool offersSensitiveHint = true;

    public WaylandClipboard(ICommandRunner runner)
    {
        this.runner = runner;
    }

    /// <summary>
    /// Reading the clipboard as text.
    /// </summary>
    /// <remarks>
    /// <c>--no-newline</c> because wl-paste appends one otherwise, and a restore that grew a
    /// newline every time would be a restore that quietly edits the person's clipboard.
    /// </remarks>
    public static IReadOnlyList<string> ReadArguments() => ReadArguments(TextType);

    /// <summary>Reading one particular type of the offer.</summary>
    public static IReadOnlyList<string> ReadArguments(string type) => ["--no-newline", "--type", type];

    /// <summary>Asking what is on the clipboard at all, when it turned out not to be text.</summary>
    public static IReadOnlyList<string> ListTypesArguments() => ["--list-types"];

    /// <summary>
    /// Writing, with the one opt-out any clipboard manager honours where it is supported.
    /// </summary>
    public static IReadOnlyList<string> WriteArguments(bool sensitive) =>
        sensitive ? ["--sensitive", "--type", TextType] : ["--type", TextType];

    /// <summary>Emptying the clipboard, which is how a dictation stops existing.</summary>
    public static IReadOnlyList<string> ClearArguments() => ["--clear"];

    /// <summary>What is on the clipboard now.</summary>
    /// <remarks>
    /// A failed read is not an error here. An empty clipboard, a compositor that is not there and a
    /// clipboard holding a photograph all end up as "no text to put back", and the difference
    /// between the last one and the first two is asked for separately - and only on the path where
    /// it is already going wrong.
    /// </remarks>
    public ClipboardContents Read()
    {
        CommandResult text = runner.Run(new CommandRequest(Paste, ReadArguments(), Budget: Budget));
        if (text.Succeeded)
        {
            return new ClipboardContents(text.Output, HasUnrestorableContents: false);
        }

        // Either there is nothing on the clipboard - wl-paste says so on both calls - or what is
        // there is not offered as text/plain. Only the second case needs a second look.
        CommandResult types = runner.Run(new CommandRequest(Paste, ListTypesArguments(), Budget: Budget));
        if (!types.Succeeded || types.Output.Trim().Length == 0)
        {
            return ClipboardContents.Nothing;
        }

        string[] offered = types.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string alias in TextAliases)
        {
            if (!offered.Contains(alias, StringComparer.Ordinal)) { continue; }

            CommandResult again = runner.Run(new CommandRequest(Paste, ReadArguments(alias), Budget: Budget));
            if (again.Succeeded)
            {
                return new ClipboardContents(again.Output, HasUnrestorableContents: false);
            }
        }

        return new ClipboardContents(null, HasUnrestorableContents: true);
    }

    /// <summary>Put text on the clipboard, with the restrictions this platform can carry.</summary>
    /// <exception cref="TextInsertionError.ClipboardWriteFailed">
    /// The tool is missing or refused. The insertion stops here, which is the point of doing this
    /// before the paste.
    /// </exception>
    public void Write(string text)
    {
        bool sensitive;
        lock (gate) { sensitive = offersSensitiveHint; }

        if (TryWrite(text, sensitive)) { return; }

        if (!sensitive)
        {
            throw new TextInsertionError.ClipboardWriteFailed();
        }

        // wl-clipboard learned --sensitive late. An older one exits with a usage error, and the
        // choice is between a dictation that fails on a supported desktop and one marker that KDE
        // alone would have honoured.
        if (!TryWrite(text, sensitive: false))
        {
            throw new TextInsertionError.ClipboardWriteFailed();
        }

        lock (gate) { offersSensitiveHint = false; }

        DiagnosticLog.Emit("insert.clipboard.no-sensitive-hint");
    }

    /// <summary>Empty the clipboard. Best effort: there is nothing better to do when it fails.</summary>
    public bool Clear() =>
        runner.Run(new CommandRequest(Copy, ClearArguments(), Budget: Budget, CaptureOutput: false))
            .Succeeded;

    private bool TryWrite(string text, bool sensitive) =>
        runner.Run(new CommandRequest(
            Copy,
            WriteArguments(sensitive),
            text,
            Budget,
            // Not captured: wl-copy forks a daemon that owns the selection and keeps our pipes, so
            // reading its output to the end would wait for the offer to be withdrawn.
            CaptureOutput: false)).Succeeded;
}
