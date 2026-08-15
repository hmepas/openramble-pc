using DictationCore;
using OpenRamble.AppCore;

namespace OpenRamble.App.Linux;

/// <summary>
/// What happens to the computer outside of us - as much of it as this wave can honestly report.
/// </summary>
/// <remarks>
/// <para>
/// Windows broadcasts all five of these as window messages to a hidden top-level window. Linux
/// publishes none of them to an ordinary application, and each one has a different owner:
/// </para>
/// <list type="bullet">
/// <item><description><b>Sleep and wake</b> are <c>org.freedesktop.login1</c>'s
/// <c>PrepareForSleep</c> signal on the system bus. A real subscription, a real D-Bus client, and
/// the reason it is not here: this wave brings up the shell, and a bus connection with its own
/// reconnect and lifetime is a piece of work with its own failure modes.</description></item>
/// <item><description><b>Audio devices</b> are PipeWire graph events, which means either
/// <c>libpipewire</c> in-process - the very dependency <c>PwRecordProcess</c> exists to avoid - or
/// watching a <c>pw-mon</c> subprocess for the life of the session.</description></item>
/// <item><description><b>Memory pressure</b> is the kernel's PSI file,
/// <c>/proc/pressure/memory</c>, which answers a <c>poll()</c> on a registered trigger. Cheap at
/// rest, like the Windows notification handle - but it needs <c>epoll</c> by hand.</description></item>
/// </list>
/// <para>
/// <b>What is missing is written down here rather than left to be discovered.</b> Without sleep,
/// a dictation held across a suspend stays in "listening" until the person presses Escape - the
/// release that happened while the machine was down reaches nobody. Without the audio event, a
/// microphone unplugged mid-sentence is discovered as an empty recognition instead of a sentence
/// saying so. Without memory pressure, <see cref="EngineResidencyPolicy"/> never gets the tier
/// that would make it release the model early; it still unloads on its idle timer, so the engine
/// is held longer on a loaded machine, not forever.
/// </para>
/// <para>
/// <see cref="ApplicationBecameActive"/> is the one that is real, and it is the one that matters
/// most: it is what re-reads the microphone after the person has been away fixing it. The shell
/// raises it - see <see cref="Raise"/> - because on this platform our own windows are the only
/// part of the desktop we are told about.
/// </para>
/// </remarks>
public sealed class LinuxSystemEventSource : ISystemEventSource
{
    /// <inheritdoc cref="LinuxSystemEventSource"/>
    public event Action? WillSleep;

    /// <inheritdoc cref="LinuxSystemEventSource"/>
    public event Action? DidWake;

    /// <inheritdoc cref="LinuxSystemEventSource"/>
    public event Action? AudioDeviceChanged;

    public event Action? ApplicationBecameActive;

    /// <inheritdoc cref="LinuxSystemEventSource"/>
    public event Action<MemoryPressureTier>? MemoryPressureChanged;

    /// <summary>One of our windows came to the front.</summary>
    public void Raise() => ApplicationBecameActive?.Invoke();

    /// <summary>
    /// Keeps the three unraised events from reading as dead code to the compiler.
    /// </summary>
    /// <remarks>
    /// Never called. It exists because an event with no <c>Invoke</c> anywhere in the assembly is
    /// a warning, and the two ways to silence that warning - deleting the events or suppressing
    /// the diagnostic - would both hide the gap this class is documenting. This keeps the gap
    /// visible and named at the one place a reader would look for it.
    /// </remarks>
    private void NotRaisedOnThisPlatform(MemoryPressureTier tier)
    {
        WillSleep?.Invoke();
        DidWake?.Invoke();
        AudioDeviceChanged?.Invoke();
        MemoryPressureChanged?.Invoke(tier);
    }
}
