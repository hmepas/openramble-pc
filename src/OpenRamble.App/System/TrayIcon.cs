using System.Runtime.InteropServices;
using OpenRamble.AppCore;

namespace OpenRamble.App;

/// <summary>One row of the tray menu, as the shell needs it.</summary>
/// <param name="Title">What the person reads. Empty means a separator.</param>
/// <param name="Action">What a click does. <c>null</c> - the row is present but disabled.</param>
public sealed record TrayMenuItem(string Title, Action? Action)
{
    public static TrayMenuItem Separator { get; } = new(string.Empty, null);

    /// <summary>Rows nested under this one. Empty for an ordinary row.</summary>
    public IReadOnlyList<TrayMenuItem> Children { get; init; } = [];
}

/// <summary>
/// The tray icon and its menu.
///
/// Hand-rolled on <c>Shell_NotifyIconW</c> and <c>TrackPopupMenu</c> rather than taken from a
/// package. The surface actually used is small - one icon, one tooltip, one popup - the rest of
/// this application's system edges are hand-rolled P/Invoke in exactly this style, and a tray
/// package would be the only dependency in the process whose job is drawing. The menu is a real
/// Win32 popup, which also means it looks and behaves like every other tray menu on the machine
/// and needs no focus tricks to dismiss.
/// </summary>
public sealed partial class TrayIcon : IDisposable
{
    private const int NimAdd = 0x00000000;
    private const int NimModify = 0x00000001;
    private const int NimDelete = 0x00000002;
    private const int NimSetVersion = 0x00000004;
    private const int NifMessage = 0x00000001;
    private const int NifIcon = 0x00000002;
    private const int NifTip = 0x00000004;
    private const int NotifyIconVersion4 = 4;

    private const int WmTrayCallback = 0x0400 + 1; // WM_APP + 1
    private const int WmCommand = 0x0111;
    private const int WmRightButtonUp = 0x0205;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmContextMenu = 0x007B;

    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint MfPopup = 0x00000010;
    private const uint MfGrayed = 0x00000001;

    private readonly MessageWindow window;
    private readonly Dictionary<uint, Action> commands = [];
    private uint nextCommandId = 1;
    private bool added;
    private bool disposed;

    public TrayIcon(MessageWindow window)
    {
        this.window = window;
        window.Message += OnMessage;
    }

    /// <summary>What the menu should contain right now. Asked at click time, never cached.</summary>
    public Func<IReadOnlyList<TrayMenuItem>>? MenuProvider { get; set; }

    /// <summary>A left click opens the same menu: there is no primary action worth guessing.</summary>
    public void Show(TrayBadge badge, string tooltip)
    {
        NotifyIconData data = Data();
        data.Flags = NifMessage | NifIcon | NifTip;
        data.CallbackMessage = WmTrayCallback;
        data.Icon = TrayIconArt.Icon(badge);
        data.Tip = tooltip.Length > 127 ? tooltip[..127] : tooltip;

        if (!added)
        {
            added = Shell_NotifyIconW(NimAdd, ref data);
            NotifyIconData version = Data();
            version.VersionOrTimeout = NotifyIconVersion4;
            _ = Shell_NotifyIconW(NimSetVersion, ref version);
            return;
        }

        _ = Shell_NotifyIconW(NimModify, ref data);
    }

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        window.Message -= OnMessage;
        if (added)
        {
            NotifyIconData data = Data();
            _ = Shell_NotifyIconW(NimDelete, ref data);
            added = false;
        }

        TrayIconArt.Release();
    }

    private NotifyIconData Data() => new()
    {
        Size = Marshal.SizeOf<NotifyIconData>(),
        Window = window.Handle,
        Id = 1,
    };

    private bool OnMessage(int message, nint wParam, nint lParam)
    {
        if (message == WmCommand)
        {
            uint id = (uint)(wParam & 0xFFFF);
            if (!commands.TryGetValue(id, out Action? action)) { return false; }
            action();
            return true;
        }

        if (message != WmTrayCallback) { return false; }

        // With version 4 the pointer position rides in wParam and the event in the low word of
        // lParam - the opposite of the legacy layout, and the reason SetVersion is not optional.
        int notification = (int)(lParam & 0xFFFF);
        if (notification is WmRightButtonUp or WmLeftButtonUp or WmContextMenu)
        {
            ShowMenu();
            return true;
        }

        return false;
    }

    private void ShowMenu()
    {
        IReadOnlyList<TrayMenuItem> items = MenuProvider?.Invoke() ?? [];
        if (items.Count == 0) { return; }

        commands.Clear();
        nextCommandId = 1;
        nint menu = BuildMenu(items);
        if (menu == 0) { return; }

        _ = GetCursorPos(out Point cursor);
        // Without this the menu refuses to close when the person clicks elsewhere: the shell
        // gives the popup no foreground claim of its own. The trailing WM_NULL is the documented
        // companion that lets the window lose it again.
        _ = SetForegroundWindow(window.Handle);
        _ = TrackPopupMenuEx(menu, 0x0002 /* TPM_RIGHTBUTTON */, cursor.X, cursor.Y, window.Handle, 0);
        _ = PostMessageW(window.Handle, 0x0000, 0, 0);
        _ = DestroyMenu(menu);
    }

    private nint BuildMenu(IReadOnlyList<TrayMenuItem> items)
    {
        nint menu = CreatePopupMenu();
        if (menu == 0) { return 0; }

        foreach (TrayMenuItem item in items)
        {
            if (item.Title.Length == 0)
            {
                _ = AppendMenuW(menu, MfSeparator, 0, null);
                continue;
            }

            if (item.Children.Count > 0)
            {
                nint child = BuildMenu(item.Children);
                _ = AppendMenuW(menu, MfString | MfPopup, (nuint)child, item.Title);
                continue;
            }

            if (item.Action is null)
            {
                _ = AppendMenuW(menu, MfString | MfGrayed, 0, item.Title);
                continue;
            }

            uint id = nextCommandId++;
            commands[id] = item.Action;
            _ = AppendMenuW(menu, MfString, id, item.Title);
        }

        return menu;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint VersionOrTimeout;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(int message, ref NotifyIconData data);

    [LibraryImport("user32.dll", EntryPoint = "CreatePopupMenu")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(nint menu, uint flags, nuint id, string? item);

    [LibraryImport("user32.dll", EntryPoint = "DestroyMenu")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll", EntryPoint = "TrackPopupMenuEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);

    [LibraryImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
}
