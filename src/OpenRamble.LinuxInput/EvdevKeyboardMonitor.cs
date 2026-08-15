using System.Diagnostics;
using OpenRamble.AppCore;

namespace OpenRamble.LinuxInput;

/// <summary>
/// The hotkey on Linux: every keyboard on the machine, read directly, turned into the four
/// gestures the application knows.
///
/// The Linux counterpart of <c>KeyboardHook</c> and <c>HotkeyMonitor</c> together. Those are two
/// files on Windows because the hook must be a message pump on a thread of its own; here the event
/// source is a blocking read on a file, so the split would buy nothing and the two halves are one
/// class - with the same shape inside: raw edges first, then
/// <see cref="HotkeyGestureMachine"/>, then the timer that a short tap's release waits on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why evdev and not the compositor.</b> Hold-to-dictate needs the press and the release of a
/// key that the application in front is also receiving, from anywhere on the desktop. Wayland has
/// no protocol for that: the closest thing, the global-shortcuts portal, delivers activation of a
/// registered accelerator and nothing about holding, and the wlr layer-shell keyboard interactivity
/// only sees keys while our own surface has focus. Reading <c>/dev/input</c> is what remains, it is
/// what every Wayland dictation and push-to-talk tool does, and it works the same on GNOME, KDE,
/// Sway and X11 because it is below all of them.
/// </para>
/// <para>
/// <b>The price is the <c>input</c> group,</b> and it is charged at <see cref="Start"/>. A user who
/// is not in it can enumerate keyboards - <c>/proc/bus/input/devices</c> is world-readable - and
/// open none of them, which is exactly the situation
/// <see cref="IHotkeyMonitoring.OnTrackingFailure"/> exists for: without it the key would simply
/// never arrive for the rest of the session, which looks identical to a person who is not pressing
/// it.
/// </para>
/// <para>
/// <b>No suppression, deliberately.</b> The Windows hook returns 1 and swallows the hotkey; the
/// evdev equivalent is <c>EVIOCGRAB</c>, which takes the whole keyboard away from the compositor
/// for as long as it is held. <c>DictationHotkeyInfo.NeedsSuppression</c> is therefore read and not
/// acted on: choosing Caps Lock on Linux means the LED toggles with each dictation. A blinking LED
/// is a recorded platform difference; a bug that leaves a desktop with no keyboard is not something
/// to risk for it.
/// </para>
/// <para>
/// <b>Nothing is remembered.</b> Every key on the machine passes through <see cref="Handle"/>,
/// which compares one code and drops the rest without storing, counting or timing it. The single
/// key that reaches the diagnostic log is the configured hotkey, which the person chose - the same
/// rule, and the same twenty lines, as the Windows hook.
/// </para>
/// </remarks>
public sealed class EvdevKeyboardMonitor : IHotkeyMonitoring, IDisposable
{
    /// <summary><c>KEY_ESC</c>. Cancels a running dictation and is never swallowed.</summary>
    private const int KeyEscape = 1;

    /// <summary>
    /// How often the machine is asked whether it has grown a keyboard.
    /// </summary>
    /// <remarks>
    /// A rescan rather than an inotify watch on <c>/dev/input</c>. The watch is not obviously
    /// cheaper - a node appears before the kernel has finished describing it in
    /// <c>/proc/bus/input/devices</c>, so the watch needs a settle delay and a re-read anyway - and
    /// it is one more thing that can fail silently on an unusual mount. Two seconds of latency on
    /// plugging in a keyboard is not something a person notices; a hotplug path that quietly stops
    /// working is.
    /// </remarks>
    private static readonly TimeSpan DefaultRescanInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long <see cref="Stop"/> waits for the reader threads before going on without them.
    /// </summary>
    /// <remarks>
    /// They are usually blocked in a <c>read</c> that only the next keystroke on that device will
    /// return from, and closing the descriptor does not wake them: .NET keeps the handle alive for
    /// the duration of a read in progress. So the wait is a courtesy with a deadline, and
    /// abandoning it is safe by construction - a reader from a previous run finds a stale
    /// generation and delivers nothing, exactly as an abandoned capture reader hands its frames to
    /// a session that refuses them.
    /// </remarks>
    private static readonly TimeSpan ReaderStopBudget = TimeSpan.FromMilliseconds(250);

    private readonly Lock gate = new();
    private readonly IKeyboardDeviceSource source;
    private readonly TimeSpan rescanInterval;

    /// <summary>Open keyboards, by device path. One reader thread each.</summary>
    private readonly Dictionary<string, Watch> watching = [];

    /// <summary>
    /// Which chord modifiers are physically down, by evdev code.
    /// </summary>
    /// <remarks>
    /// Windows asks the system with <c>GetKeyState</c>; there is no such call here, so the state is
    /// accumulated from the same stream the gestures come from. The one thing that costs: a
    /// modifier already held when <see cref="Start"/> runs is invisible until it is pressed again.
    /// The consequence is bounded and self-healing - the first dictation started inside such a
    /// chord is allowed rather than refused - and the alternative, an <c>EVIOCGKEY</c> ioctl per
    /// device, buys that one case for a P/Invoke on every open.
    /// </remarks>
    private readonly HashSet<int> modifiersDown = [];

    private HotkeyGestureMachine machine = new(DictationHotkey.RightCtrl, LinuxHotkeyKeymap.Instance);
    private CancellationTokenSource? pendingRelease;
    private Timer? rescan;
    private bool running;

    /// <summary>
    /// Which run a reader belongs to. Bumped by every <see cref="Stop"/>.
    /// </summary>
    /// <remarks>
    /// The whole defence against a reader that outlived its stop: it wakes on the next keystroke,
    /// finds a generation that is not the current one, and delivers nothing into a session that has
    /// moved on.
    /// </remarks>
    private int generation;

    /// <summary>
    /// Whether the hotkey is physically down, as the raw edges see it.
    ///
    /// Separate from the gesture machine's own <c>IsHeld</c> for the reason the Windows monitor
    /// keeps it separate: the machine refuses a press that arrives inside a chord, and the raw
    /// edges must report that press anyway - it is the very case where a person needs to be told
    /// the key did arrive.
    /// </summary>
    private bool rawKeyDown;

    public EvdevKeyboardMonitor()
        : this(new EvdevDeviceSource())
    {
    }

    /// <summary>The same monitor with the kernel replaced - the seam the tests drive.</summary>
    internal EvdevKeyboardMonitor(IKeyboardDeviceSource source, TimeSpan? rescanInterval = null)
    {
        this.source = source;
        this.rescanInterval = rescanInterval ?? DefaultRescanInterval;
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

    /// <summary>
    /// Which chord modifiers are held right now, across every keyboard being watched.
    /// </summary>
    /// <remarks>
    /// Published because the insertion path needs it and has no other way to ask: Windows has
    /// <c>GetAsyncKeyState</c>, Wayland has nothing, and sending Ctrl+V while the person still
    /// holds Shift turns it into somebody else's combination. See
    /// <see cref="LinuxTextInserter"/>, which takes this as a delegate.
    /// </remarks>
    public HotkeyModifiers HeldModifiers
    {
        get { lock (gate) { return Chord(); } }
    }

    /// <summary>Whether any keyboard is currently being read.</summary>
    public bool IsRunning
    {
        get { lock (gate) { return running && watching.Count > 0; } }
    }

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

        // Events of the old key stop meaning anything here as well: without this its "up" would
        // never come and the try-out would show a key held down forever.
        if (wasDown) { OnHotkeyUp?.Invoke(); }

        // Always false on this platform, and written down rather than omitted: a log read next to
        // a Windows one has to show where the two behave differently, and Caps Lock still shouting
        // is the difference.
        DiagnosticLog.Emit("hotkey.configured", ("key", hotkey), ("suppressed", false));
        Dispatch(action);
    }

    /// <summary>
    /// Open every keyboard on the machine and start reading it.
    ///
    /// Calling again while it runs does nothing, for the reason the Windows hook refuses to
    /// re-install: it would drop the memory of a key currently held, and the release that followed
    /// would be lost with dictation left switched on.
    /// </summary>
    public void Start()
    {
        lock (gate)
        {
            if (running) { return; }

            running = true;
            generation += 1;
        }

        Scan scan = OpenNewDevices();
        // The one fact about this class worth writing down, and it costs nothing to be wrong
        // about: a monitor that opened nothing is silent for the rest of the process, which looks
        // exactly like a person who is not pressing the key.
        DiagnosticLog.Emit(
            "hook.install",
            ("ok", scan.Opened > 0),
            ("error", scan.Opened > 0 ? "none" : scan.Denied > 0 ? "permission" : "no-keyboard"),
            ("keyboards", scan.Found),
            ("opened", scan.Opened),
            ("denied", scan.Denied));

        if (scan.Opened == 0)
        {
            // No half-working tracking. A timer that keeps rescanning behind a failure the person
            // was told about is fine - they may fix the group membership and plug a keyboard in -
            // but the failure is reported now, once, rather than left to be inferred.
            OnTrackingFailure?.Invoke(FailureMessage(scan));
        }

        var timer = new Timer(_ => Rescan(), null, rescanInterval, rescanInterval);
        lock (gate)
        {
            if (running)
            {
                rescan = timer;
                return;
            }
        }

        // Stopped while the first scan was running. The timer belongs to nobody.
        timer.Dispose();
    }

    public void Stop()
    {
        Timer? timer;
        Watch[] closing;
        lock (gate)
        {
            if (!running) { return; }

            running = false;
            generation += 1;
            timer = rescan;
            rescan = null;
            closing = [.. watching.Values];
            watching.Clear();
        }

        timer?.Dispose();
        foreach (Watch watch in closing)
        {
            watch.Close();
        }

        // Bounded, and the deadline is shared: ten abandoned keyboards must not cost ten times the
        // budget on a stop that the shell is waiting on.
        long began = Stopwatch.GetTimestamp();
        foreach (Watch watch in closing)
        {
            TimeSpan left = ReaderStopBudget - Stopwatch.GetElapsedTime(began);
            if (left <= TimeSpan.Zero) { break; }

            // Never the current thread: a handler that stopped the monitor from inside a reader
            // callback would otherwise be waiting for itself.
            if (watch.Thread is Thread reader && reader != Thread.CurrentThread)
            {
                reader.Join(left);
            }
        }

        CancelPendingRelease();
        bool wasDown;
        lock (gate)
        {
            machine.Reset();
            modifiersDown.Clear();
            wasDown = rawKeyDown;
            rawKeyDown = false;
        }

        if (wasDown) { OnHotkeyUp?.Invoke(); }

        DiagnosticLog.Emit("hook.removed");
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Pick up keyboards that appeared since the last look.
    /// </summary>
    /// <remarks>
    /// Also the recovery path for a device that failed to open the first time: a person who was
    /// told to join the <c>input</c> group and did so gets a working hotkey when they next log in,
    /// and a keyboard that was busy for a moment is retried two seconds later. A failed open is a
    /// failed syscall and nothing more, so retrying it forever is cheaper than remembering which
    /// ones to skip.
    /// </remarks>
    private void Rescan()
    {
        try
        {
            Scan scan = OpenNewDevices();
            if (scan.Appeared > 0)
            {
                DiagnosticLog.Emit(
                    "hook.rescan",
                    ("appeared", scan.Appeared),
                    ("open", scan.Opened),
                    ("denied", scan.Denied));
            }
        }
        catch (Exception error)
        {
            // A timer callback that throws takes the process down. Whatever went wrong in an
            // enumeration is not worth that; the keyboards already open keep working.
            DiagnosticLog.EmitException("hook.rescan-failed", error);
        }
    }

    /// <summary>Open every keyboard that is not already being read, and say what was found.</summary>
    private Scan OpenNewDevices()
    {
        int currentGeneration;
        lock (gate)
        {
            if (!running) { return default; }

            currentGeneration = generation;
        }

        IReadOnlyList<KeyboardDevice> keyboards = Enumerate();
        int opened = 0;
        int appeared = 0;
        int denied = 0;

        foreach (KeyboardDevice device in keyboards)
        {
            lock (gate)
            {
                if (!running || generation != currentGeneration) { break; }

                if (watching.ContainsKey(device.Path))
                {
                    opened += 1;
                    continue;
                }
            }

            IKeyboardDeviceReader reader;
            try
            {
                reader = source.Open(device);
            }
            catch (UnauthorizedAccessException)
            {
                // The ordinary state of a machine where nobody has joined the input group. Not
                // logged per device: it is the same answer for every keyboard, and the count in
                // hook.install already says how many.
                denied += 1;
                continue;
            }
            catch (Exception error) when (error is IOException or NotSupportedException
                or ArgumentException)
            {
                // Unplugged between the enumeration and the open, or a node that is not what the
                // kernel's list said it was. Neither is worth a failure the person has to read.
                DiagnosticLog.Emit("hook.device-open-failed", ("node", device.EventNode));
                continue;
            }

            var watch = new Watch(device, reader, currentGeneration);
            bool kept;
            lock (gate)
            {
                kept = running && generation == currentGeneration && watching.TryAdd(device.Path, watch);
            }

            if (!kept)
            {
                reader.Dispose();
                break;
            }

            watch.Start(Read);
            opened += 1;
            appeared += 1;
            DiagnosticLog.Emit("hook.device", ("node", device.EventNode), ("name", device.Name));
        }

        return new Scan(keyboards.Count, opened, appeared, denied);
    }

    private IReadOnlyList<KeyboardDevice> Enumerate()
    {
        try
        {
            return source.Enumerate();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.EmitException("hook.enumerate-failed", error);
            return [];
        }
    }

    /// <summary>What the person is told when there is nothing to read the hotkey from.</summary>
    /// <remarks>
    /// Two different failures with two different remedies, and telling them apart is the entire
    /// value of the message. "Add yourself to the input group" said on a machine with no keyboard
    /// wastes an evening; "no keyboard found" said to somebody who has three is worse.
    /// </remarks>
    private static string FailureMessage(Scan scan) => scan.Denied > 0
        ? "OpenRamble couldn't read the keyboard: the devices under /dev/input belong to the "
            + "`input` group and this account isn't in it, so the dictation key does nothing. "
            + "Run `sudo gpasswd -a $USER input`, then log out and back in."
        : "OpenRamble couldn't find a keyboard to watch under /dev/input, so the dictation key "
            + "does nothing. Plugging a keyboard in is enough - it is picked up without a restart.";

    /// <summary>Read one device until it ends, then let go of it.</summary>
    private void Read(Watch watch)
    {
        try
        {
            EvdevEventStream.Pump(
                watch.Reader.Events,
                keyboardEvent => Handle(watch, keyboardEvent),
                () => IsCurrent(watch));
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException
            or InvalidOperationException or UnauthorizedAccessException)
        {
            // The device went away under the read - unplugged, or closed by a stop. Both are
            // ordinary, and the difference is not worth a branch: the rescan brings it back if it
            // comes back.
        }
        finally
        {
            Forget(watch);
        }
    }

    /// <summary>
    /// Whether this reader still belongs to the run in progress.
    /// </summary>
    /// <remarks>
    /// Asked between reads, so that a reader abandoned by a stop leaves the moment its device
    /// produces one more event instead of holding the descriptor until the process exits.
    /// </remarks>
    private bool IsCurrent(Watch watch)
    {
        lock (gate) { return running && generation == watch.Generation; }
    }

    /// <summary>Drop a keyboard that has stopped producing events.</summary>
    private void Forget(Watch watch)
    {
        bool wasWatched;
        lock (gate)
        {
            wasWatched = watching.TryGetValue(watch.Device.Path, out Watch? current)
                && ReferenceEquals(current, watch);
            if (wasWatched) { watching.Remove(watch.Device.Path); }
        }

        watch.Close();
        if (wasWatched)
        {
            DiagnosticLog.Emit("hook.device-gone", ("node", watch.Device.EventNode));
        }
    }

    /// <summary>
    /// One key event, from the kernel's numbers to a gesture.
    /// </summary>
    /// <remarks>
    /// Runs on the device's reader thread, synchronously, and the order it imposes is the product:
    /// a release that overtook its own press would leave the microphone live with nothing to close
    /// it. Cheap for the same reason the Windows callback is - a lock, a comparison and a delegate
    /// call - although nothing here is removed by the system if it gets slow; what happens instead
    /// is that this keyboard's events queue in the kernel's buffer.
    /// </remarks>
    private void Handle(Watch watch, EvdevEvent raw)
    {
        if (raw.Type != EvdevEvent.TypeKey) { return; }

        int code = raw.Code;
        // A repeat is a key-down as far as everything downstream is concerned - it is what Windows
        // delivers for a held key too, and the gesture machine already answers it with nothing.
        bool down = !raw.IsRelease;

        HotkeyAction action;
        bool? edge = null;
        bool escaped = false;
        DictationHotkey configured;
        HotkeyModifiers held;
        lock (gate)
        {
            if (!running || generation != watch.Generation) { return; }

            held = Track(code, down);
            configured = machine.Hotkey;

            if (code == KeyEscape)
            {
                // Escape never reaches the gesture machine, exactly as it never reaches the
                // Windows one: it is a cancel, not a key held during a dictation, and whether
                // there is anything to cancel is decided upstream.
                if (!down) { return; }

                escaped = true;
                action = HotkeyAction.None;
            }
            else
            {
                // Auto-repeat resends the key-down for as long as it is held; only a change of side
                // is an edge.
                if (code == machine.Keymap.CodeFor(configured) && down != rawKeyDown)
                {
                    rawKeyDown = down;
                    edge = down;
                }

                action = machine.Handle(new HotkeyEvent(code, down, held, DateTimeOffset.UtcNow));
            }
        }

        // Outside the gate, like every other callback here: what a handler does is the shell's
        // business, and holding a lock across it is how a keyboard thread ends up waiting on the
        // interface.
        if (escaped)
        {
            OnEscape?.Invoke();
            return;
        }

        // Before the gesture, deliberately. The raw edge is what says "the key arrived", and it
        // must reach the screen even when the gesture that follows decides to do nothing.
        if (edge is bool wentDown)
        {
            // An edge only ever exists for the configured key, so this is the one key name the log
            // can carry. Every other key on the machine is compared and dropped, which is the whole
            // design of the device reader and is not undone by a log.
            DiagnosticLog.Emit("hotkey.edge", ("key", configured), ("down", wentDown));
            (wentDown ? OnHotkeyDown : OnHotkeyUp)?.Invoke();
        }

        Dispatch(action);
    }

    /// <summary>
    /// Remember a modifier's new state and answer with the whole chord.
    /// </summary>
    /// <remarks>
    /// Called before the event reaches the machine, so that the hotkey's own press is included in
    /// the chord it is judged against - which is what <c>GetKeyState</c> reports inside the Windows
    /// hook, and what <c>HotkeyGestureMachine.IsExclusivelyPressed</c> is written for. The gate
    /// must be held.
    /// </remarks>
    private HotkeyModifiers Track(int code, bool down)
    {
        if (LinuxHotkeyKeymap.Instance.IsModifierCode(code))
        {
            if (down) { modifiersDown.Add(code); }
            else { modifiersDown.Remove(code); }
        }

        return Chord();
    }

    /// <summary>The modifier kinds currently down. The gate must be held.</summary>
    private HotkeyModifiers Chord()
    {
        HotkeyModifiers held = HotkeyModifiers.None;
        foreach (int code in modifiersDown)
        {
            held |= LinuxModifierCodes.KindOf(code);
        }

        return held;
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
    /// The wait happens off the reader thread deliberately: sleeping there stops this keyboard
    /// being read, and the key that ends the wait would be the one nobody sees.
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

    /// <summary>What one scan found, in the four numbers the diagnostics and the failure need.</summary>
    private readonly record struct Scan(int Found, int Opened, int Appeared, int Denied);

    /// <summary>
    /// One keyboard being read, for one run of the monitor.
    /// </summary>
    /// <remarks>
    /// The generation is the whole reason this is an object rather than a stream in a dictionary:
    /// a reader thread cannot be woken from a blocking read, so a stop leaves it alive until its
    /// device produces one more event. Finding a stale generation, it delivers nothing and leaves -
    /// the same defence a capture session uses against a reader that outlived its stop.
    /// </remarks>
    private sealed class Watch
    {
        public Watch(KeyboardDevice device, IKeyboardDeviceReader reader, int generation)
        {
            Device = device;
            Reader = reader;
            Generation = generation;
        }

        public KeyboardDevice Device { get; }

        public IKeyboardDeviceReader Reader { get; }

        /// <summary>The run this reader belongs to.</summary>
        public int Generation { get; }

        /// <summary>The one thread reading this device.</summary>
        public Thread? Thread { get; private set; }

        public void Start(Action<Watch> read)
        {
            // A thread of its own, not a pool one: this blocks on a device for the whole session,
            // and the pool grows by one thread per second under starvation - which is where the
            // rest of the application's work runs.
            Thread = new Thread(() => read(this))
            {
                Name = $"OpenRamble keys {Device.EventNode}",
                IsBackground = true,
            };
            Thread.Start();
        }

        public void Close()
        {
            try
            {
                Reader.Dispose();
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                // Closing a device that is already gone is the outcome asked for.
            }
        }
    }
}

/// <summary>
/// Which modifier kind an evdev code belongs to.
/// </summary>
/// <remarks>
/// <para>
/// The companion of <see cref="LinuxHotkeyKeymap.IsModifierCode"/>, which answers "is this a
/// modifier" and is the authority on the set. It cannot answer "which one" because nothing on
/// Windows needs it to: there, the chord is read from the system with <c>GetKeyState</c>, and only
/// this platform has to build it out of the codes it sees. There is a test that walks the whole
/// key range and fails if the two ever disagree about membership.
/// </para>
/// <para>
/// Codes from <c>linux/input-event-codes.h</c>.
/// </para>
/// </remarks>
internal static class LinuxModifierCodes
{
    public static HotkeyModifiers KindOf(int keyCode) => keyCode switch
    {
        42 or 54 => HotkeyModifiers.Shift,     // KEY_LEFTSHIFT, KEY_RIGHTSHIFT
        29 or 97 => HotkeyModifiers.Control,   // KEY_LEFTCTRL, KEY_RIGHTCTRL
        56 or 100 => HotkeyModifiers.Alt,      // KEY_LEFTALT, KEY_RIGHTALT
        125 or 126 => HotkeyModifiers.Windows, // KEY_LEFTMETA, KEY_RIGHTMETA
        _ => HotkeyModifiers.None,
    };
}
