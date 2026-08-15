using System.Buffers.Binary;

namespace OpenRamble.LinuxInput;

/// <summary>
/// One kernel input event, as <c>/dev/input/event*</c> delivers it.
/// </summary>
/// <remarks>
/// The <c>struct input_event</c> of <c>linux/input.h</c> with the timestamp dropped. The kernel's
/// time is deliberately not carried: the gesture machine measures the double-tap window against
/// the clock the rest of the application uses, and a second clock that can disagree with it would
/// make a gesture depend on which of the two ran first.
/// </remarks>
/// <param name="Type">The event class - <c>EV_KEY</c>, <c>EV_SYN</c>, <c>EV_MSC</c>, …</param>
/// <param name="Code">Within <c>EV_KEY</c>, the evdev keycode - the numbering
/// <see cref="OpenRamble.AppCore.LinuxHotkeyKeymap"/> is written in.</param>
/// <param name="Value">For a key: 0 release, 1 press, 2 auto-repeat.</param>
internal readonly record struct EvdevEvent(ushort Type, ushort Code, int Value)
{
    /// <summary>A key or a button changed state. Everything else is not this file's business.</summary>
    public const ushort TypeKey = 1;

    /// <summary>Key-down, but not the auto-repeat that follows it.</summary>
    public bool IsPress => Value == 1;

    /// <summary>Key-up.</summary>
    public bool IsRelease => Value == 0;

    /// <summary>
    /// The kernel repeating a key that is being held.
    /// </summary>
    /// <remarks>
    /// Passed on rather than filtered here, because the monitor already has to recognise a
    /// repeated key-down as "not an edge" - Windows delivers the same repetition as ordinary
    /// <c>WM_KEYDOWN</c> messages, and both platforms answer it in the same place.
    /// </remarks>
    public bool IsRepeat => Value == 2;
}

/// <summary>
/// Bytes from an input device into events.
/// </summary>
/// <remarks>
/// <para>
/// The evdev peer of <c>RawSampleStream</c>, and it exists for the same reason: the parsing is the
/// one place where a mistake is silent. A wrong offset does not throw - it produces a plausible
/// keycode for the wrong key, and the dictation hotkey answers to something the person never
/// pressed. Kept pure and fed a <see cref="Stream"/> so a test can hand it bytes it wrote itself.
/// </para>
/// <para>
/// A real device always returns whole events from one <c>read</c>, so the carry below is never
/// exercised in production. It is here because "always" is a property of the kernel's character
/// device and not of the <see cref="Stream"/> this takes, and because the cost is four lines.
/// </para>
/// </remarks>
internal static class EvdevEventStream
{
    /// <summary>
    /// <c>sizeof(struct input_event)</c> on this process's word size: two <c>time_t</c>-sized
    /// fields, then <c>__u16</c>, <c>__u16</c>, <c>__s32</c>.
    /// </summary>
    /// <remarks>
    /// 24 bytes on x86-64 and arm64, which is every machine this ships to. Derived rather than
    /// written down because a hard-coded 24 read on a 32-bit runtime would not fail - it would
    /// slide one event's fields into the next one's, which is the silent kind of wrong.
    /// </remarks>
    public static int EventSize { get; } = (nint.Size * 2) + 8;

    /// <summary>Where <c>type</c> starts: right after the timestamp.</summary>
    private static int TypeOffset => nint.Size * 2;

    /// <summary>How many events one read may collect. A keyboard produces a handful per press.</summary>
    private const int BatchEvents = 64;

    /// <summary>
    /// Read until <paramref name="source"/> ends, handing over every whole event it produced.
    /// </summary>
    /// <param name="deliver">
    /// Called on this thread, in order. Ordering is the whole point: a press that arrived after
    /// its own release would leave dictation switched on with nothing left to stop it.
    /// </param>
    /// <param name="keepReading">
    /// Asked before every read: <c>false</c> ends the loop.
    /// </param>
    /// <remarks>
    /// <para>
    /// Returns on end of stream, which for a real device means it was unplugged or closed under
    /// us. Whatever the read throws is the caller's to interpret - during a stop it is the stop,
    /// and at any other time it is a keyboard that has gone away.
    /// </para>
    /// <para>
    /// <paramref name="keepReading"/> exists because a blocking read on a character device cannot
    /// be cancelled: a stop closes the descriptor and the read stays parked until the next
    /// keystroke anyway. Checked between reads, it lets the woken thread leave then rather than
    /// go back for another one and hold the device for the life of the process.
    /// </para>
    /// </remarks>
    public static void Pump(Stream source, Action<EvdevEvent> deliver, Func<bool>? keepReading = null)
    {
        byte[] buffer = new byte[EventSize * BatchEvents];
        int carried = 0;

        while (keepReading?.Invoke() ?? true)
        {
            int read = source.Read(buffer, carried, buffer.Length - carried);
            if (read <= 0)
            {
                // End of stream. Anything still carried is a fragment of an event nobody finished
                // writing; there is nothing honest to do with it.
                return;
            }

            int available = carried + read;
            int whole = available / EventSize;
            for (int index = 0; index < whole; index++)
            {
                deliver(Parse(buffer.AsSpan(index * EventSize, EventSize)));
            }

            carried = available - (whole * EventSize);
            if (carried > 0)
            {
                buffer.AsSpan(whole * EventSize, carried).CopyTo(buffer);
            }
        }
    }

    /// <summary>One event out of exactly <see cref="EventSize"/> bytes, little-endian.</summary>
    /// <remarks>
    /// The kernel writes the structure in the machine's own byte order, and every machine this
    /// runs on is little-endian. Reading it explicitly rather than by reinterpreting memory keeps
    /// the layout in one readable place and costs three loads per event.
    /// </remarks>
    public static EvdevEvent Parse(ReadOnlySpan<byte> raw) => new(
        BinaryPrimitives.ReadUInt16LittleEndian(raw[TypeOffset..]),
        BinaryPrimitives.ReadUInt16LittleEndian(raw[(TypeOffset + 2)..]),
        BinaryPrimitives.ReadInt32LittleEndian(raw[(TypeOffset + 4)..]));
}
