using OpenRamble.AppCore;

namespace OpenRamble.App;

/// <summary>
/// The hotkey, from raw key events to the four gestures the application knows.
///
/// Two halves that must not be one: <see cref="KeyboardHook"/> sees every key on the machine and
/// is therefore twenty lines of comparison with no memory, and
/// <see cref="HotkeyGestureMachine"/> holds the whole gesture table and is tested without a
/// keyboard. This joins them, and owns the one thing neither can: the timer that a short tap's
/// release waits on, so a double tap has room to arrive.
/// </summary>
public sealed class HotkeyMonitor : IHotkeyMonitoring, IDisposable
{
    private readonly KeyboardHook hook = new();
    private readonly Lock gate = new();
    private HotkeyGestureMachine machine = new(DictationHotkey.RightCtrl);
    private CancellationTokenSource? pendingRelease;

    /// <summary>
    /// Whether the hotkey is physically down, as the raw edges see it.
    ///
    /// Separate from the gesture machine's own <c>IsHeld</c> on purpose: the machine refuses a
    /// press that arrives inside a chord, and the raw edges must report that press anyway - it is
    /// the very case where a person needs to be told the key did arrive.
    /// </summary>
    private bool rawKeyDown;

    public HotkeyMonitor()
    {
        hook.KeyEvent += OnKeyEvent;
        hook.Escape += () => OnEscape?.Invoke();
        hook.Failed = error => OnTrackingFailure?.Invoke(
            "OpenRamble couldn't start watching the dictation key"
            + (error == 0 ? "" : $" (Windows error {error})")
            + ", so the key does nothing. Restarting OpenRamble usually fixes this.");
        hook.SuppressesHotkey = hook.Hotkey.NeedsSuppression;
    }

    public Action? OnPress { get; set; }

    public Action? OnRelease { get; set; }

    public Action? OnDoubleTap { get; set; }

    public Action? OnSingleTapWhileHandsFree { get; set; }

    public Action? OnAbortShortcut { get; set; }

    public Action? OnEscape { get; set; }

    public Action? OnHotkeyDown { get; set; }

    public Action? OnHotkeyUp { get; set; }

    public Action<string>? OnTrackingFailure { get; set; }

    public bool IsHandsFreeActive
    {
        get { lock (gate) { return machine.IsHandsFreeActive; } }
        set { lock (gate) { machine.IsHandsFreeActive = value; } }
    }

    public void SetHotkey(DictationHotkey hotkey)
    {
        HotkeyAction action;
        bool wasDown;
        lock (gate)
        {
            action = machine.SetHotkey(hotkey);
            wasDown = rawKeyDown;
            rawKeyDown = false;
        }

        // Events of the old key stop arriving here as well: without this its "up" would never
        // come and the try-out would show a key held down forever.
        if (wasDown) { OnHotkeyUp?.Invoke(); }

        DiagnosticLog.Emit("hotkey.configured", ("key", hotkey), ("suppressed", hotkey.NeedsSuppression));
        hook.Hotkey = hotkey;
        hook.SuppressesHotkey = hotkey.NeedsSuppression;
        // Changing the key mid-hold ends the hold: the machine says so, and ignoring it would
        // leave a dictation running with no key left to stop it.
        Dispatch(action);
    }

    public void Start() => hook.Start();

    public void Stop()
    {
        hook.Stop();
        CancelPendingRelease();
        bool wasDown;
        lock (gate)
        {
            machine.Reset();
            wasDown = rawKeyDown;
            rawKeyDown = false;
        }

        if (wasDown) { OnHotkeyUp?.Invoke(); }
    }

    public void Dispose()
    {
        Stop();
        hook.Dispose();
    }

    /// <remarks>
    /// Runs on the hook thread, synchronously. Everything here is a lock and a delegate call -
    /// the hook is removed by Windows if this ever becomes slow, and until then every keystroke
    /// on the machine waits on it.
    /// </remarks>
    private void OnKeyEvent(HotkeyEvent keyboardEvent)
    {
        HotkeyAction action;
        bool? edge = null;
        DictationHotkey configured;
        lock (gate)
        {
            configured = machine.Hotkey;
            // Auto-repeat resends the key-down for as long as it is held; only a change of side
            // is an edge.
            if (keyboardEvent.VirtualKey == configured.VirtualKey
                && keyboardEvent.IsDown != rawKeyDown)
            {
                rawKeyDown = keyboardEvent.IsDown;
                edge = keyboardEvent.IsDown;
            }

            action = machine.Handle(keyboardEvent);
        }

        // Before the gesture, deliberately. The raw edge is what says "the key arrived", and it
        // must reach the screen even when the gesture that follows decides to do nothing.
        if (edge is bool down)
        {
            // An edge only ever exists for the configured key, so this is the one key name the
            // log can carry. Every other key on the machine is compared and dropped, which is
            // the whole design of KeyboardHook and is not undone by a log.
            //
            // This is a file write on the hook thread, and the hook thread is the one Windows
            // unhooks if it gets slow. It is two lines per dictation and only when somebody
            // switched diagnostics on for the run - when they are off, which is every other run,
            // this is a null check. Queueing it instead would defeat the point: the log exists
            // for the run that is about to be killed, and a queued line is the one that is lost.
            DiagnosticLog.Emit("hotkey.edge", ("key", configured), ("down", down));
            (down ? OnHotkeyDown : OnHotkeyUp)?.Invoke();
        }

        Dispatch(action);
    }

    private void Dispatch(HotkeyAction action)
    {
        if (action.Kind != HotkeyActionKind.None)
        {
            DiagnosticLog.Emit("hotkey.gesture", ("kind", action.Kind), ("delay_ms", action.ReleaseDelay));
        }

        switch (action.Kind)
        {
            case HotkeyActionKind.None:
                break;

            case HotkeyActionKind.Press:
                CancelPendingRelease();
                OnPress?.Invoke();
                break;

            case HotkeyActionKind.Release:
                ScheduleRelease(action.ReleaseDelay);
                break;

            case HotkeyActionKind.DoubleTap:
                CancelPendingRelease();
                OnDoubleTap?.Invoke();
                break;

            case HotkeyActionKind.StopHandsFree:
                CancelPendingRelease();
                OnSingleTapWhileHandsFree?.Invoke();
                break;

            case HotkeyActionKind.AbortShortcut:
                CancelPendingRelease();
                OnAbortShortcut?.Invoke();
                break;
        }
    }

    /// <summary>
    /// A short tap's stop waits out the double-tap window; a long hold stops at once.
    ///
    /// The wait happens off the hook thread deliberately: sleeping on the hook is how a keyboard
    /// freezes for every application on the machine.
    /// </summary>
    private void ScheduleRelease(TimeSpan delay)
    {
        CancelPendingRelease();
        if (delay <= TimeSpan.Zero)
        {
            OnRelease?.Invoke();
            return;
        }

        var cancellation = new CancellationTokenSource();
        lock (gate) { pendingRelease = cancellation; }

        _ = Task.Delay(delay, cancellation.Token).ContinueWith(
            task =>
            {
                if (task.IsCanceled) { return; }
                OnRelease?.Invoke();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void CancelPendingRelease()
    {
        CancellationTokenSource? pending;
        lock (gate)
        {
            pending = pendingRelease;
            pendingRelease = null;
        }

        pending?.Cancel();
        pending?.Dispose();
    }
}
