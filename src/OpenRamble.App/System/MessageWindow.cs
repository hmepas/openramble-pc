using System.Windows.Interop;

namespace OpenRamble.App;

/// <summary>
/// The application's one real window - and it is never shown.
///
/// A tray-only app still needs an <c>HWND</c>: <c>Shell_NotifyIconW</c> posts its callbacks to
/// one, <c>TrackPopupMenu</c> needs an owner, and the shell broadcasts sleep and device changes
/// to top-level windows. This is that window, zero-sized and never made visible.
/// </summary>
/// <remarks>
/// <b>Top-level, not message-only.</b> A message-only window (<c>HWND_MESSAGE</c>) would be
/// enough for the tray callback and is cheaper, but it is excluded from broadcasts - so
/// <c>WM_POWERBROADCAST</c> and <c>WM_DEVICECHANGE</c> would never arrive, and the app would keep
/// a live microphone through a suspend. The whole reason this window exists is to hear them.
///
/// <para>
/// Built on <see cref="HwndSource"/> rather than a hand-registered window class: WPF already owns
/// a window class and a dispatcher-bound <c>WndProc</c>, and <see cref="HwndSource.AddHook"/> is
/// the same hook a hand-rolled procedure would give, minus the delegate-lifetime hazard.
/// </para>
/// </remarks>
public sealed class MessageWindow : IDisposable
{
    private readonly HwndSource source;

    public MessageWindow()
    {
        source = new HwndSource(new HwndSourceParameters("OpenRamble")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            ExtendedWindowStyle = 0x00000080, // WS_EX_TOOLWINDOW - never in the taskbar or Alt+Tab
        });
        source.AddHook(Hook);
    }

    public nint Handle => source.Handle;

    /// <summary>
    /// Every message the window receives, before WPF sees it.
    ///
    /// Handlers return <c>true</c> once they have consumed the message.
    /// </summary>
    public event Func<int, nint, nint, bool>? Message;

    public void Dispose()
    {
        source.RemoveHook(Hook);
        source.Dispose();
    }

    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (Message is null) { return 0; }

        foreach (Func<int, nint, nint, bool> handler in Message.GetInvocationList().Cast<Func<int, nint, nint, bool>>())
        {
            if (handler(message, wParam, lParam))
            {
                handled = true;
                return 0;
            }
        }

        return 0;
    }
}
