using System.Runtime.InteropServices;
using System.Threading;
using OpenRamble.AppCore;

namespace OpenRamble.App;

/// <summary>
/// Tracks the hotkey in all applications through a low-level keyboard hook.
///
/// This is the Windows counterpart of <c>GlobalHotkeyMonitor</c>'s event source. macOS asks for
/// an Accessibility grant and then hands events to an <c>NSEvent</c> monitor; Windows asks for
/// nothing and hands them to <c>WH_KEYBOARD_LL</c>. The hook sees every key on the machine, and
/// the rules that follow from that are the whole design of this file:
///
/// <list type="bullet">
/// <item><description><b>Nothing is remembered and nothing is passed on.</b> The callback compares
/// the virtual-key code against the configured hotkey and Escape and drops everything else on the
/// floor. No key is logged, counted, or timed - not even in a debug build. The privacy invariant
/// is not a policy applied on top of a keylogger; it is why this file is twenty lines of
/// comparison and no storage.</description></item>
/// <item><description><b>The callback must return fast.</b> Windows removes a hook that exceeds
/// <c>LowLevelHooksTimeout</c>, and until then every keystroke on the machine waits on this
/// function. Handlers run synchronously anyway - see <see cref="KeyEvent"/> - so they must do
/// nothing but push state into the gesture machine.</description></item>
/// <item><description><b>The hook needs its own thread with a message pump.</b> A low-level hook
/// is dispatched by the system posting to the thread that installed it; a thread that does not
/// pump receives no calls at all. It cannot ride on the WPF dispatcher either: the UI thread
/// blocks on layout and rendering, and a blocked hook thread stalls the keyboard of every
/// application on the machine.</description></item>
/// </list>
///
/// <para>
/// <b>Antivirus and anti-cheat see this.</b> <c>SetWindowsHookExW(WH_KEYBOARD_LL, …)</c> is the
/// same call a keylogger makes, and it is indistinguishable from one by signature alone. Expect,
/// and do not try to work around:
/// </para>
/// <list type="bullet">
/// <item><description>SmartScreen and consumer AV flagging an unsigned build as
/// <c>Trojan:Win32/Wacatac</c>-shaped heuristics. Code signing with a reputable certificate is
/// the answer; obfuscating the call is not, and would be dishonest.</description></item>
/// <item><description>Kernel anti-cheat (EasyAntiCheat, BattlEye, Vanguard) refusing to run, or
/// terminating its game, while any process holds a low-level keyboard hook. There is no
/// workaround and there should not be: a program that reads every keystroke on the machine is
/// exactly what they exist to notice.</description></item>
/// <item><description>The hook silently receiving nothing while an elevated window has focus.
/// UIPI blocks a medium-integrity process from hooking a high-integrity one, so dictation into an
/// elevated app cannot work unless OpenRamble is elevated too - which it must not be by
/// default.</description></item>
/// </list>
/// </summary>
public sealed partial class KeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int HcAction = 0;

    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmQuit = 0x0012;

    private const int VkEscape = 0x1B;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLeftWindows = 0x5B;
    private const int VkRightWindows = 0x5C;

    /// <summary>Kept alive for the hook's lifetime: a collected delegate is a crash in the kernel's callback.</summary>
    private readonly HookProc callback;

    private readonly Lock gate = new();
    private Thread? pump;
    private uint pumpThreadId;
    private nint hook;
    private DictationHotkey hotkey = DictationHotkey.RightCtrl;
    private bool suppressesHotkey;

    public KeyboardHook()
    {
        callback = OnKey;
    }

    /// <summary>
    /// Every key event that concerns dictation: the hotkey itself, Escape, and anything pressed
    /// while the hotkey is held (which is how a shortcut ends the gesture).
    /// </summary>
    /// <remarks>
    /// Raised synchronously on the hook thread, deliberately. Marshalling to another thread would
    /// not preserve order, and a press arriving after its own release leaves dictation switched
    /// on: the microphone live, the recording running, nothing left to stop it. Whatever consumes
    /// this must be cheap and thread-safe.
    /// </remarks>
    public event Action<HotkeyEvent>? KeyEvent;

    /// <summary>
    /// Escape while a dictation is running - cancel it.
    /// </summary>
    /// <remarks>
    /// Escape is never swallowed. Whether there is anything to cancel is decided upstream, and
    /// while there is not, Escape is an ordinary key that must reach the application in front.
    /// </remarks>
    public event Action? Escape;

    public bool IsRunning
    {
        get
        {
            lock (gate)
            {
                return hook != 0;
            }
        }
    }

    public DictationHotkey Hotkey
    {
        get
        {
            lock (gate)
            {
                return hotkey;
            }
        }

        set
        {
            lock (gate)
            {
                hotkey = value;
            }
        }
    }

    /// <summary>
    /// Whether the hotkey's own events are swallowed instead of reaching the application in
    /// front.
    ///
    /// Set while a dictation owns the key. Caps Lock is the reason this exists: it toggles on
    /// key-down, so without swallowing, every dictation would leave the keyboard shouting. It
    /// also keeps a Right Ctrl hold from arriving in the destination app as a stray modifier.
    /// Only the hotkey is ever swallowed - never a letter, never Escape.
    /// </summary>
    public bool SuppressesHotkey
    {
        get
        {
            lock (gate)
            {
                return suppressesHotkey;
            }
        }

        set
        {
            lock (gate)
            {
                suppressesHotkey = value;
            }
        }
    }

    /// <summary>
    /// Install the hook on a thread of its own and start pumping.
    ///
    /// Calling again while it runs does nothing. Re-installing would be worse than a no-op: it
    /// would drop the memory of a key currently held, and the release that followed would be lost
    /// with dictation left switched on. Only <see cref="Stop"/> may erase that memory.
    /// </summary>
    public void Start()
    {
        lock (gate)
        {
            if (pump is not null)
            {
                return;
            }

            var ready = new ManualResetEventSlim();
            pump = new Thread(() => Pump(ready))
            {
                Name = "OpenRamble hotkey",
                IsBackground = true,
            };
            pump.Start();
            ready.Wait();
        }
    }

    public void Stop()
    {
        Thread? thread;
        uint threadId;
        lock (gate)
        {
            thread = pump;
            threadId = pumpThreadId;
            pump = null;
            pumpThreadId = 0;
        }

        if (thread is null)
        {
            return;
        }

        PostThreadMessageW(threadId, WmQuit, 0, 0);
        thread.Join(TimeSpan.FromSeconds(2));
    }

    public void Dispose() => Stop();

    private void Pump(ManualResetEventSlim ready)
    {
        nint installed = SetWindowsHookExW(WhKeyboardLl, callback, GetModuleHandleW(null), 0);
        lock (gate)
        {
            hook = installed;
            pumpThreadId = GetCurrentThreadId();
        }

        ready.Set();

        if (installed == 0)
        {
            // No half-working tracking. Leaving the pump alive without a hook would look healthy
            // and never deliver a key.
            lock (gate)
            {
                pump = null;
            }

            return;
        }

        try
        {
            while (GetMessageW(out MSG message, 0, 0, 0) > 0)
            {
                TranslateMessage(in message);
                DispatchMessageW(in message);
            }
        }
        finally
        {
            UnhookWindowsHookEx(installed);
            lock (gate)
            {
                hook = 0;
            }
        }
    }

    private nint OnKey(int code, nint wParam, nint lParam)
    {
        if (code != HcAction)
        {
            return CallNextHookEx(0, code, wParam, lParam);
        }

        int message = (int)wParam;
        bool isDown = message is WmKeyDown or WmSysKeyDown;
        bool isUp = message is WmKeyUp or WmSysKeyUp;
        if (!isDown && !isUp)
        {
            return CallNextHookEx(0, code, wParam, lParam);
        }

        KBDLLHOOKSTRUCT keyboard = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        int virtualKey = (int)keyboard.VkCode;

        DictationHotkey current;
        bool suppress;
        lock (gate)
        {
            current = hotkey;
            suppress = suppressesHotkey;
        }

        if (virtualKey == VkEscape && isDown)
        {
            Escape?.Invoke();
            // Escape keeps its ordinary path: it is a key the application in front may need.
            return CallNextHookEx(0, code, wParam, lParam);
        }

        KeyEvent?.Invoke(new HotkeyEvent(virtualKey, isDown, HeldModifiers(), DateTimeOffset.UtcNow));

        if (virtualKey == current.VirtualKey && suppress)
        {
            // Swallowed: 1 means "handled", and the event never reaches anyone else.
            return 1;
        }

        return CallNextHookEx(0, code, wParam, lParam);
    }

    /// <summary>
    /// Which chord modifiers are down right now.
    /// </summary>
    /// <remarks>
    /// Asked of the system rather than tracked, because the hook can be installed with keys
    /// already held, and because a modifier released while another application had a modal loop
    /// open never reaches this callback. The high bit of <c>GetKeyState</c> is the down bit; on
    /// the hook thread it reflects the queue the hook is draining, which is the state the event
    /// being processed belongs to.
    /// </remarks>
    private static HotkeyModifiers HeldModifiers()
    {
        HotkeyModifiers held = HotkeyModifiers.None;
        if (IsDown(VkControl))
        {
            held |= HotkeyModifiers.Control;
        }

        if (IsDown(VkMenu))
        {
            held |= HotkeyModifiers.Alt;
        }

        if (IsDown(VkShift))
        {
            held |= HotkeyModifiers.Shift;
        }

        if (IsDown(VkLeftWindows) || IsDown(VkRightWindows))
        {
            held |= HotkeyModifiers.Windows;
        }

        return held;
    }

    private static bool IsDown(int virtualKey) => (GetKeyState(virtualKey) & 0x8000) != 0;

    private delegate nint HookProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowsHookExW(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in MSG lpMsg);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(in MSG lpMsg);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint idThread, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial short GetKeyState(int nVirtKey);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? lpModuleName);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
