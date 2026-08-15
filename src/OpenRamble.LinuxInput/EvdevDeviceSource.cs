namespace OpenRamble.LinuxInput;

/// <summary>
/// An opened keyboard: the bytes it produces, and a way to stop wanting them.
/// </summary>
/// <remarks>
/// Two members, for the same reason <c>IRecorderProcess</c> has five: everything the monitor must
/// be able to do to a device that has stopped cooperating. Behind the real file both the
/// permission failure and the unplug would only be observable on a machine set up to reproduce
/// them; behind this they are ordinary tests.
/// </remarks>
internal interface IKeyboardDeviceReader : IDisposable
{
    /// <summary>The device this came from, for diagnostics.</summary>
    string Path { get; }

    /// <summary>A blocking stream of <c>struct input_event</c>, and nothing else.</summary>
    Stream Events { get; }
}

/// <summary>Where keyboards come from. The one part of the monitor that touches the kernel.</summary>
internal interface IKeyboardDeviceSource
{
    /// <summary>Every keyboard the machine currently has, whether or not it can be opened.</summary>
    IReadOnlyList<KeyboardDevice> Enumerate();

    /// <summary>
    /// Open one for reading.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">
    /// The process may not read <c>/dev/input</c> - the ordinary state of a user who is not in the
    /// <c>input</c> group, and the one failure this whole seam exists to surface.
    /// </exception>
    /// <exception cref="IOException">The device went away between the enumeration and the open.</exception>
    IKeyboardDeviceReader Open(KeyboardDevice device);
}

/// <summary>The real thing: <c>/proc/bus/input/devices</c> and an open file descriptor.</summary>
/// <remarks>
/// <para>
/// <b>Read-only, and never a grab.</b> <c>EVIOCGRAB</c> would let the hotkey be swallowed the way
/// the Windows hook swallows it - and it takes the device away from the compositor for as long as
/// it is held, so a bug anywhere in this process leaves the machine with no keyboard at all. The
/// start is without it, at the price of Caps Lock still toggling while it is used as the dictation
/// key. That is the recorded platform difference, and it is a blinking LED rather than a keyboard
/// nobody can type on.
/// </para>
/// <para>
/// <b>Nothing is remembered.</b> This is the file that sees every key on the machine, so it does
/// the same twenty lines of comparison the Windows hook does and keeps no more: the events go
/// straight to the monitor, which compares a code and drops it. Not logged, not counted, not
/// timed - see <c>DiagnosticLog</c>'s rule about keystroke identities.
/// </para>
/// </remarks>
internal sealed class EvdevDeviceSource : IKeyboardDeviceSource
{
    public IReadOnlyList<KeyboardDevice> Enumerate() => InputDeviceCatalog.Read();

    public IKeyboardDeviceReader Open(KeyboardDevice device)
    {
        // No buffer: a buffered FileStream would ask the kernel for more than the event that is
        // ready and hand the monitor a press only once the next key arrives. FileShare.ReadWrite
        // because the compositor is reading the same device and neither of us owns it.
        var events = new FileStream(
            device.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 0,
            useAsync: false);
        return new OpenDevice(device.Path, events);
    }

    private sealed class OpenDevice : IKeyboardDeviceReader
    {
        private readonly FileStream events;

        public OpenDevice(string path, FileStream events)
        {
            Path = path;
            this.events = events;
        }

        public string Path { get; }

        public Stream Events => events;

        public void Dispose() => events.Dispose();
    }
}
