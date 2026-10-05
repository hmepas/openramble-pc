using OpenRamble.AppCore;

namespace OpenRamble.LinuxInput;

/// <summary>
/// Watches the whole machine for one shortcut - the chord that copies the last dictation.
///
/// The Linux peer of the macOS <c>GlobalShortcutMonitor</c>, on the same event source the
/// dictation key uses: <see cref="EvdevKeyboardMonitor"/> reads every keyboard once and
/// publishes what it read through <see cref="IRawKeySource"/>, and this compares each key-down
/// against one chord. Nothing is swallowed - evdev is read-only from here, so a shortcut that
/// happens to collide with one in the front application still reaches it.
/// </summary>
/// <remarks>
/// <para>
/// <b>It runs exactly as long as the keyboard monitor does.</b> There is no reader of its own to
/// start; <see cref="Start"/> subscribes and <see cref="Stop"/> unsubscribes, and whether keys
/// arrive at all is decided by the keyboard monitor's own start - which is the same thing the
/// original does by starting the copy monitor only once the hotkey monitor is running.
/// </para>
/// <para>
/// <b>Auto-repeat is not a press.</b> A held Ctrl+C would otherwise copy the last dictation
/// thirty times a second; the kernel marks a repeat, and it is dropped here.
/// </para>
/// <para>
/// <b>The modifier set is compared exactly,</b> as <see cref="KeyCombination.Matches"/> does:
/// Ctrl+C must not answer to Ctrl+Shift+C. The set arrives with the event, tracked by the
/// keyboard monitor from the same stream, with lock keys already folded away.
/// </para>
/// </remarks>
public sealed class EvdevShortcutMonitor : IShortcutMonitoring, IDisposable
{
    private readonly Lock gate = new();
    private readonly IRawKeySource keys;
    private KeyCombination? shortcut;
    private bool subscribed;

    public EvdevShortcutMonitor(IRawKeySource keys)
    {
        this.keys = keys;
    }

    /// <summary>Raised on the reader thread of the device the chord came from.</summary>
    public Action? OnPress { get; set; }

    public bool IsRunning
    {
        get { lock (gate) { return subscribed; } }
    }

    public void SetShortcut(KeyCombination? shortcut)
    {
        lock (gate) { this.shortcut = shortcut; }

        // A monitor with nothing to look for is a monitor that only costs.
        if (shortcut is null) { Stop(); }
    }

    public void Start()
    {
        lock (gate)
        {
            if (subscribed || shortcut is null) { return; }

            subscribed = true;
        }

        keys.KeyObserved += Handle;
    }

    public void Stop()
    {
        lock (gate)
        {
            if (!subscribed) { return; }

            subscribed = false;
        }

        keys.KeyObserved -= Handle;
    }

    public void Dispose() => Stop();

    private void Handle(RawKeyEvent key)
    {
        if (!key.Down || key.Repeat) { return; }

        KeyCombination? bound;
        lock (gate)
        {
            if (!subscribed) { return; }

            bound = shortcut;
        }

        if (bound is not { } chord || !chord.Matches(key.Code, key.Held)) { return; }

        // Outside the gate, like every callback on this platform: what the handler does is the
        // shell's business, and a lock held across it is a keyboard thread waiting on the interface.
        OnPress?.Invoke();
    }
}
