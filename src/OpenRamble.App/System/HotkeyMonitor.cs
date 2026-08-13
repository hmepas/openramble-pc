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

    public HotkeyMonitor()
    {
        hook.KeyEvent += OnKeyEvent;
        hook.Escape += () => OnEscape?.Invoke();
        hook.SuppressesHotkey = hook.Hotkey.NeedsSuppression;
    }

    public Action? OnPress { get; set; }

    public Action? OnRelease { get; set; }

    public Action? OnDoubleTap { get; set; }

    public Action? OnSingleTapWhileHandsFree { get; set; }

    public Action? OnAbortShortcut { get; set; }

    public Action? OnEscape { get; set; }

    public bool IsHandsFreeActive
    {
        get { lock (gate) { return machine.IsHandsFreeActive; } }
        set { lock (gate) { machine.IsHandsFreeActive = value; } }
    }

    public void SetHotkey(DictationHotkey hotkey)
    {
        HotkeyAction action;
        lock (gate)
        {
            action = machine.SetHotkey(hotkey);
        }

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
        lock (gate) { machine.Reset(); }
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
        lock (gate)
        {
            action = machine.Handle(keyboardEvent);
        }

        Dispatch(action);
    }

    private void Dispatch(HotkeyAction action)
    {
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
