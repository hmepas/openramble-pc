using OpenRamble.AppCore;

namespace OpenRamble.App;

/// <summary>
/// Watches the whole machine for one chord - the shortcut that copies the last dictation.
///
/// The Windows counterpart of <c>GlobalShortcutMonitor</c>. It reads the same
/// <see cref="KeyboardHook"/> the dictation key is read from rather than installing a hook of
/// its own: a second <c>WH_KEYBOARD_LL</c> is a second thread every keystroke on the machine
/// waits on, and a second thing for antivirus to flag. The hook fans out every edge with the
/// modifiers held at that instant; this type compares and drops, like everything else on that
/// path, and remembers exactly one bit - whether the chord's key is currently down.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing is swallowed.</b> The hook returns every event untouched (the only key it ever
/// eats is the dictation hotkey, and only while a dictation owns it), so a chord that happens
/// to collide with one in the front application still reaches it. Eating other applications'
/// keys from the background is how a small utility becomes the thing that broke someone's
/// editor.
/// </para>
/// <para>
/// <b>Lock keys are folded away for free.</b> The hook reports only Ctrl, Alt, Shift and Win as
/// held (<see cref="HotkeyModifiers"/> has no bit for Caps Lock or Num Lock), and
/// <see cref="KeyCombination.Matches"/> compares that set exactly, so Ctrl+C does not answer to
/// Ctrl+Shift+C and does answer with Caps Lock on.
/// </para>
/// <para>
/// <b>AltGr reads as Ctrl+Alt.</b> Windows delivers AltGr as a Left Ctrl down followed by a
/// Right Alt down, and the hook sees both held. A chord recorded through AltGr is therefore
/// stored as Ctrl+Alt+key and matched as Ctrl+Alt+key, which is exactly how Windows itself
/// treats the key in its own accelerator tables. No special case - one would only make a
/// recorded shortcut stop matching on the layout it was recorded on.
/// </para>
/// <para>
/// <b>Auto-repeat is not a second press.</b> A low-level hook receives a key-down for every
/// repeat of a held key and carries no "was already down" bit, so the monitor keeps that bit
/// itself: it fires on the down edge and re-arms on the up edge of the chord's key.
/// </para>
/// <para>
/// <b><see cref="OnPress"/> is raised on the hook thread</b>, synchronously, like every callback
/// of <see cref="HotkeyMonitor"/>. Whoever consumes it must be cheap and thread-safe - the hook
/// is removed by Windows if this ever becomes slow, and until then every keystroke on the
/// machine waits on it.
/// </para>
/// <para>
/// The dictation key itself is never a chord's key: the gesture machine treats any key pressed
/// while the hotkey is held as a shortcut that ends the hold, and the recorder keeps the
/// hotkey out of the choices. That rule lives with the settings, not here.
/// </para>
/// <para>
/// Untested on this host - the WPF app only cross-compiles here. The only decision in the file
/// is <see cref="ShouldFire"/>, kept static and obviously right for that reason.
/// </para>
/// </remarks>
public sealed class WindowsShortcutMonitor : IShortcutMonitoring, IDisposable
{
    private readonly KeyboardHook hook;
    private readonly Lock gate = new();
    private KeyCombination? shortcut;
    private bool subscribed;
    private bool keyIsDown;

    /// <param name="hook">The one hook; see <see cref="HotkeyMonitor.Hook"/>.</param>
    public WindowsShortcutMonitor(KeyboardHook hook)
    {
        this.hook = hook;
    }

    public Action? OnPress { get; set; }

    /// <summary>
    /// Whether presses can reach <see cref="OnPress"/> right now: this monitor is listening
    /// and the shared hook is installed.
    /// </summary>
    public bool IsRunning
    {
        get
        {
            lock (gate)
            {
                return subscribed && shortcut is not null && hook.IsRunning;
            }
        }
    }

    /// <summary><c>null</c> takes the shortcut off the keyboard entirely.</summary>
    public void SetShortcut(KeyCombination? shortcut)
    {
        lock (gate)
        {
            this.shortcut = shortcut;
            keyIsDown = false;
        }

        if (shortcut is null)
        {
            Stop();
        }
    }

    /// <summary>
    /// Start listening. A no-op without a shortcut: a monitor with nothing to look for is a
    /// monitor that only costs. Calling again while listening does nothing.
    /// </summary>
    public void Start()
    {
        lock (gate)
        {
            if (subscribed || shortcut is null)
            {
                return;
            }

            hook.KeyEvent += OnKeyEvent;
            subscribed = true;
            keyIsDown = false;
        }

        // The hook is shared, and starting it twice is a no-op by its own contract. On Windows
        // there is no permission to wait for before installing it, so the first monitor to
        // ask is the one that installs it.
        hook.Start();
    }

    public void Stop()
    {
        lock (gate)
        {
            if (!subscribed)
            {
                return;
            }

            hook.KeyEvent -= OnKeyEvent;
            subscribed = false;
            keyIsDown = false;
        }
    }

    public void Dispose() => Stop();

    /// <remarks>Runs on the hook thread; a lock, a comparison and a delegate call.</remarks>
    private void OnKeyEvent(HotkeyEvent keyboardEvent)
    {
        bool fire;
        lock (gate)
        {
            if (shortcut is not { } current)
            {
                return;
            }

            fire = ShouldFire(current, keyboardEvent, ref keyIsDown);
        }

        if (fire)
        {
            DiagnosticLog.Emit("shortcut.press");
            OnPress?.Invoke();
        }
    }

    /// <summary>
    /// Whether this edge is the chord being pressed, and the bookkeeping that makes a held key
    /// press once.
    /// </summary>
    /// <param name="keyIsDown">
    /// The one remembered bit: set on the chord key's down edge, cleared on its up edge. It is
    /// the key, not the whole chord, that is tracked - a modifier released and re-pressed while
    /// the key stays down is still the same press.
    /// </param>
    internal static bool ShouldFire(KeyCombination shortcut, HotkeyEvent keyboardEvent, ref bool keyIsDown)
    {
        if (keyboardEvent.KeyCode != shortcut.KeyCode)
        {
            return false;
        }

        if (!keyboardEvent.IsDown)
        {
            keyIsDown = false;
            return false;
        }

        if (keyIsDown)
        {
            // Auto-repeat: the key has not come up since it fired.
            return false;
        }

        keyIsDown = true;
        return shortcut.Matches(keyboardEvent.KeyCode, keyboardEvent.HeldModifiers);
    }
}
