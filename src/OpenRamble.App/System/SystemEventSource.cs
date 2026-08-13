using System.Runtime.InteropServices;
using OpenRamble.AppCore;

namespace OpenRamble.App;

/// <summary>
/// What happens to the computer outside of us: sleep, wake, audio devices, memory pressure.
/// </summary>
/// <remarks>
/// <para>
/// macOS gets three of these from <c>NSWorkspace</c> and <c>AVAudioEngine</c> notifications;
/// Windows broadcasts them as window messages, which is why <see cref="MessageWindow"/> is a
/// top-level window. <c>WM_DEVICECHANGE</c> is taken at its coarse, registration-free level:
/// any device arriving or leaving re-checks the recording, because the only question asked is
/// "is the microphone we are using still there", and the answer costs a state read.
/// </para>
/// <para>
/// <b>Memory pressure has no three-tier event here.</b> macOS hands a
/// <c>DispatchSource.makeMemoryPressureSource</c> with normal/warning/critical.
/// <c>CreateMemoryResourceNotification</c> is a single low-memory edge, so the tier is derived
/// from the system-wide commit load once the edge fires. At rest the watcher is blocked in
/// <c>WaitForSingleObject</c> and costs nothing - the "the app does nothing while idle" promise
/// survives. Only while the machine is actually low does it re-read every 30 seconds, because
/// the notification stays signalled and busy-waiting on it would be the one thing worse than
/// polling.
/// </para>
/// </remarks>
public sealed partial class SystemEventSource : ISystemEventSource, IDisposable
{
    private const int WmPowerBroadcast = 0x0218;
    private const int WmDeviceChange = 0x0219;
    private const int WmActivateApp = 0x001C;
    private const int PbtApmSuspend = 0x0004;
    private const int PbtApmResumeAutomatic = 0x0012;
    private const int PbtApmResumeSuspend = 0x0007;

    private const int LowMemoryResourceNotification = 0;

    private readonly MessageWindow window;
    private readonly CancellationTokenSource watcher = new();
    private readonly Thread? pressureThread;
    private readonly nint notification;

    public SystemEventSource(MessageWindow window)
    {
        this.window = window;
        window.Message += OnMessage;

        notification = CreateMemoryResourceNotification(LowMemoryResourceNotification);
        if (notification != 0)
        {
            pressureThread = new Thread(WatchMemory)
            {
                Name = "OpenRamble memory pressure",
                IsBackground = true,
            };
            pressureThread.Start();
        }
    }

    public event Action? WillSleep;

    public event Action? DidWake;

    public event Action? AudioDeviceChanged;

    public event Action? ApplicationBecameActive;

    public event Action<MemoryPressureTier>? MemoryPressureChanged;

    public void Dispose()
    {
        window.Message -= OnMessage;
        watcher.Cancel();
        if (notification != 0) { _ = CloseHandle(notification); }
        pressureThread?.Join(TimeSpan.FromSeconds(1));
        watcher.Dispose();
    }

    private bool OnMessage(int message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WmPowerBroadcast when (int)wParam == PbtApmSuspend:
                WillSleep?.Invoke();
                return false;

            case WmPowerBroadcast when (int)wParam is PbtApmResumeAutomatic or PbtApmResumeSuspend:
                DidWake?.Invoke();
                return false;

            case WmDeviceChange:
                AudioDeviceChanged?.Invoke();
                return false;

            case WmActivateApp when wParam != 0:
                ApplicationBecameActive?.Invoke();
                return false;

            default:
                return false;
        }
    }

    private void WatchMemory()
    {
        var tier = MemoryPressureTier.Normal;
        while (!watcher.IsCancellationRequested)
        {
            // Blocks until the system says memory is low. No timer, no tick, no wake-up.
            uint waited = WaitForSingleObject(notification, tier == MemoryPressureTier.Normal ? 0xFFFFFFFF : 30_000);
            if (watcher.IsCancellationRequested) { return; }
            if (waited is not (0 or 0x00000102)) { return; }

            MemoryPressureTier observed = Observe();
            if (observed == tier)
            {
                // Still low and unchanged: the notification stays signalled, so sleep out the
                // re-check window instead of spinning on it.
                if (observed == MemoryPressureTier.Normal) { Thread.Sleep(1000); }
                continue;
            }

            tier = observed;
            MemoryPressureChanged?.Invoke(tier);
        }
    }

    /// <summary>
    /// The tier, from the commit load the OS reports.
    ///
    /// The thresholds mirror what the memory notification itself means on a desktop: above 90%
    /// the machine is already trimming working sets, and at that point holding several hundred
    /// megabytes of model weights is taking them from the application the person is looking at.
    /// </summary>
    private static MemoryPressureTier Observe()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) { return MemoryPressureTier.Normal; }

        return status.MemoryLoad switch
        {
            >= 90 => MemoryPressureTier.Critical,
            >= 80 => MemoryPressureTier.Warning,
            _ => MemoryPressureTier.Normal,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateMemoryResourceNotification")]
    private static partial nint CreateMemoryResourceNotification(int type);

    [LibraryImport("kernel32.dll", EntryPoint = "WaitForSingleObject")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
