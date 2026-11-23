using System.Runtime.InteropServices;

namespace OpenRamble.LinuxInput;

/// <summary>
/// The control socket: where it lives, and the whole of what may be said over it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why there is a socket at all.</b> Reading <c>/dev/input</c> needs the <c>input</c> group, and
/// a person who will not - or may not - join it has no hotkey. The compositor, however, already
/// sees every key and can be told to run a command on a key's press and on its release. This
/// socket is the other end of those two commands: a second way in for the same gesture, for the
/// machine where the first one is closed. <see cref="EvdevKeyboardMonitor"/> stays the primary
/// path and is untouched by any of this - both sources are live at once, by design.
/// </para>
/// <para>
/// <b>The protocol, in full.</b> A Unix stream socket. One command per line, LF-terminated, UTF-8,
/// one reply line per command, and the connection may carry as many commands as it likes before it
/// is closed. Surrounding whitespace and a trailing CR are ignored; nothing else is.
/// </para>
/// <list type="table">
/// <listheader><term>Command</term><description>Reply, and what it did</description></listheader>
/// <item>
/// <term><c>hotkey press</c></term>
/// <description><c>ok down</c> - the configured key's press edge, injected into the same gesture
/// machine the keyboard feeds. Already down: nothing happens and the reply is the same, because
/// the caller asked for a state, not for an event.</description>
/// </item>
/// <item>
/// <term><c>hotkey release</c></term>
/// <description><c>ok up</c> - the release edge. This is the one that ends a hold and inserts.</description>
/// </item>
/// <item>
/// <term><c>hotkey toggle</c></term>
/// <description><c>ok down</c> or <c>ok up</c> - whichever edge the key was not already at. For a
/// compositor that can only bind one edge of a bare modifier.</description>
/// </item>
/// <item>
/// <term><c>status</c></term>
/// <description><c>ok state=&lt;dictation state&gt; key=&lt;hotkey&gt; down=&lt;true|false&gt;</c>
/// - enough to tell a running app from a dead socket, and a wedged hold from an idle one.</description>
/// </item>
/// <item>
/// <term><c>library</c></term>
/// <description><c>ok library</c> - the Library window opens, or comes forward if it is open, as
/// the tray menu's "Library…" does.</description>
/// </item>
/// <item>
/// <term>anything else</term>
/// <description><c>error unknown command</c>, and the connection stays open.</description>
/// </item>
/// </list>
/// <para>
/// <b>Deliberately dumb.</b> No framing, no handshake, no version, no quoting - which is what makes
/// <c>printf 'hotkey press\n' | socat - UNIX-CONNECT:$XDG_RUNTIME_DIR/openramble/ctl.sock</c> a
/// complete client, and the compositor's config file needs a complete client that costs no process
/// start worth measuring. The one thing the protocol does promise is the reply line: without it a
/// caller cannot tell "the app took it" from "the app is not running", and a hold-to-dictate bind
/// that silently does nothing is the failure this whole path exists to avoid.
/// </para>
/// </remarks>
public static class ControlSocket
{
    /// <summary>The socket's own name inside <see cref="DefaultDirectory"/>.</summary>
    public const string FileName = "ctl.sock";

    /// <summary>Replies that mean the command was carried out start with this word.</summary>
    public const string Ok = "ok";

    /// <summary>Replies that mean it was not start with this one.</summary>
    public const string Error = "error";

    private const string DirectoryName = "openramble";

    /// <summary>
    /// <c>$XDG_RUNTIME_DIR/openramble/ctl.sock</c>, or the fallback under the temporary directory.
    /// </summary>
    public static string DefaultPath => Path.Combine(DefaultDirectory, FileName);

    /// <summary>
    /// The directory the socket is created in, owner-only.
    /// </summary>
    /// <remarks>
    /// <c>XDG_RUNTIME_DIR</c> is the right place and is almost always set: it is per-user, already
    /// 0700, on a tmpfs, and cleaned out when the session ends - which is exactly the lifetime a
    /// socket naming a running process should have. It is honoured rather than assumed, in the
    /// shape <c>LaunchAtLogin</c> honours <c>XDG_CONFIG_HOME</c>, because a person who moved it has
    /// moved this with it.
    /// </remarks>
    public static string DefaultDirectory =>
        DirectoryFor(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), EffectiveUserId());

    /// <summary>
    /// The same decision without the environment, so both halves of it can be checked.
    /// </summary>
    /// <remarks>
    /// The fallback carries the user id in its name and not in a parent directory, because
    /// <c>/tmp</c> is shared: two people signed into one machine must not be handed the same path,
    /// and the first of them to create it would own it. A relative <c>XDG_RUNTIME_DIR</c> is
    /// treated as no <c>XDG_RUNTIME_DIR</c> - a socket resolved against whatever the working
    /// directory happened to be is worse than one in a boring place.
    /// </remarks>
    internal static string DirectoryFor(string? runtimeDirectory, uint userId) =>
        runtimeDirectory is { Length: > 0 } && Path.IsPathRooted(runtimeDirectory)
            ? Path.Combine(runtimeDirectory, DirectoryName)
            : Path.Combine(Path.GetTempPath(), $"{DirectoryName}-{userId}");

    /// <summary>
    /// The effective user id, for the fallback path and for checking who owns a private folder.
    /// </summary>
    /// <remarks>
    /// One of the two calls into libc in this assembly (<c>PasteReadReceipts</c> has the other). <c>Environment.UserName</c> would do the same job
    /// with characters in it that a path should not have to survive, and there is no managed way to
    /// ask this question.
    /// </remarks>
    /// <remarks>
    /// <c>DllImport</c> and not the generated <c>LibraryImport</c>: the generator wants
    /// <c>AllowUnsafeBlocks</c> across the whole assembly, and this assembly is the one that reads
    /// <c>/dev/input</c>. A blanket <c>unsafe</c> is too much to pay for one argument-less call
    /// returning an integer, which has nothing to marshal in the first place.
    /// </remarks>
    [DllImport("libc", EntryPoint = "geteuid")]
    internal static extern uint EffectiveUserId();
}
