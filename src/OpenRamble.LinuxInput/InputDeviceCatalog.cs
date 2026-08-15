using System.Globalization;

namespace OpenRamble.LinuxInput;

/// <summary>One keyboard the monitor is willing to listen to.</summary>
/// <param name="Name">What the kernel calls it. Shown in diagnostics, never in a gesture.</param>
/// <param name="EventNode">The bare node name, <c>event7</c>. The path is derived, never parsed.</param>
internal readonly record struct KeyboardDevice(string Name, string EventNode)
{
    /// <summary>The character device to open.</summary>
    public string Path => $"{InputDeviceCatalog.DeviceDirectory}/{EventNode}";
}

/// <summary>
/// Which of the machine's input devices are keyboards, read out of
/// <c>/proc/bus/input/devices</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the proc file and not the ioctl.</b> The canonical answer is <c>EVIOCGBIT</c> on every
/// <c>/dev/input/event*</c> in turn - but that requires opening each device first, and opening is
/// exactly what a user outside the <c>input</c> group may not do. The proc file is world-readable
/// and carries the same two bitmaps, so the enumeration can tell "there are three keyboards here
/// and I am not allowed to read them" apart from "this machine has no keyboard", which is the
/// difference between a message a person can act on and a shrug. It is also plain text, which
/// means the classification below is checked against real fixtures instead of against a mock of
/// an ioctl.
/// </para>
/// <para>
/// <b>What counts as a keyboard.</b> The heuristic is systemd's, from its <c>input_id</c> udev
/// builtin, and it is used here because it is the one every desktop already agrees with: a device
/// is a keyboard when it reports <c>EV_KEY</c> and its key bitmap contains all of
/// <c>KEY_ESC</c>‥<c>KEY_S</c> - the first thirty-one codes, which is Esc, the number row, Q‥P and
/// A, S. Nothing that is not a typing keyboard has all of them: a mouse has buttons at 0x110, a
/// power button has one bit, and the consumer-control half of a keyboard - which is a separate
/// device node with its own volume and media keys - has a handful. Being strict here matters more
/// than being generous: every device that passes gets a thread and an open file descriptor for
/// the life of the session.
/// </para>
/// </remarks>
internal static class InputDeviceCatalog
{
    /// <summary>The kernel's own list. World-readable, which is the whole reason it is used.</summary>
    public const string DevicesFile = "/proc/bus/input/devices";

    /// <summary>Where the character devices live.</summary>
    public const string DeviceDirectory = "/dev/input";

    /// <summary><c>EV_KEY</c> in the <c>B: EV=</c> bitmap.</summary>
    private const ulong EventTypeKeyBit = 1UL << 1;

    /// <summary>
    /// <c>KEY_ESC</c>‥<c>KEY_S</c> - codes 1 to 31 - in the low word of the key bitmap.
    /// </summary>
    /// <remarks>
    /// Bit 0 is <c>KEY_RESERVED</c> and is left out: no device sets it, and requiring it would
    /// classify every keyboard as something else.
    /// </remarks>
    private const ulong TypingKeyMask = 0xFFFF_FFFEUL;

    /// <summary>Read the machine's keyboards, or nothing if the kernel's list cannot be read.</summary>
    /// <remarks>
    /// An unreadable proc file is not a failure worth throwing over: the caller's next question -
    /// "is there a keyboard I may open?" - is answered "none found", and that is the honest answer
    /// when the list itself is missing. The monitor says so out loud rather than looking healthy.
    /// </remarks>
    public static IReadOnlyList<KeyboardDevice> Read()
    {
        try
        {
            return Parse(File.ReadAllText(DevicesFile));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>
    /// The keyboards described by the contents of <c>/proc/bus/input/devices</c>.
    /// </summary>
    /// <remarks>
    /// One device per blank-line-separated block. A block missing any of the three things this
    /// needs - a name, an <c>event*</c> handler, the two bitmaps - is skipped rather than guessed
    /// at: the file grows new line kinds between kernel versions, and a parser that invents a
    /// device out of a block it does not understand would open something that is not a keyboard.
    /// </remarks>
    public static IReadOnlyList<KeyboardDevice> Parse(string catalog)
    {
        var keyboards = new List<KeyboardDevice>();
        string name = string.Empty;
        string? node = null;
        ulong eventTypes = 0;
        ulong typingKeys = 0;

        void Finish()
        {
            if (node is not null
                && (eventTypes & EventTypeKeyBit) != 0
                && (typingKeys & TypingKeyMask) == TypingKeyMask)
            {
                keyboards.Add(new KeyboardDevice(name, node));
            }

            name = string.Empty;
            node = null;
            eventTypes = 0;
            typingKeys = 0;
        }

        foreach (string raw in catalog.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                Finish();
                continue;
            }

            if (line.StartsWith("N: Name=", StringComparison.Ordinal))
            {
                name = line["N: Name=".Length..].Trim('"');
            }
            else if (line.StartsWith("H: Handlers=", StringComparison.Ordinal))
            {
                node = EventNodeIn(line["H: Handlers=".Length..]);
            }
            else if (line.StartsWith("B: EV=", StringComparison.Ordinal))
            {
                eventTypes = FirstWord(line["B: EV=".Length..]);
            }
            else if (line.StartsWith("B: KEY=", StringComparison.Ordinal))
            {
                typingKeys = LowWord(line["B: KEY=".Length..]);
            }
        }

        // A file that does not end in a blank line still describes a device in its last block.
        Finish();
        return keyboards;
    }

    /// <summary>The <c>event*</c> among the handlers, which is the one that delivers events.</summary>
    /// <remarks>
    /// The others - <c>kbd</c>, <c>leds</c>, <c>sysrq</c>, <c>mouse0</c>, <c>js0</c> - are the
    /// kernel's own consumers or legacy nodes, and none of them speaks <c>input_event</c>.
    /// </remarks>
    private static string? EventNodeIn(string handlers)
    {
        foreach (string handler in handlers.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (handler.StartsWith("event", StringComparison.Ordinal)
                && handler.Length > "event".Length
                && handler["event".Length..].All(char.IsAsciiDigit))
            {
                return handler;
            }
        }

        return null;
    }

    /// <summary>
    /// The lowest-numbered word of a bitmap, which is where codes 0‥63 live.
    /// </summary>
    /// <remarks>
    /// The kernel prints a bitmap most-significant word first, so the word holding the low codes
    /// is the <b>last</b> one on the line. The word is 64 bits on a 64-bit kernel and 32 on a
    /// 32-bit one; either way it covers the whole of <see cref="TypingKeyMask"/>, so the test does
    /// not need to know which it got.
    /// </remarks>
    private static ulong LowWord(string bitmap)
    {
        string[] words = bitmap.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? 0 : Hex(words[^1]);
    }

    /// <summary>The <c>EV=</c> bitmap, which never needs more than one word.</summary>
    private static ulong FirstWord(string bitmap) => LowWord(bitmap);

    private static ulong Hex(string word) =>
        ulong.TryParse(word, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong value)
            ? value
            : 0;
}
