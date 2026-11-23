using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using DictationCore;
using LocalASR;
using OpenRamble.AppCore;
using OpenRamble.LinuxAudio;
using OpenRamble.LinuxInput;

namespace OpenRamble.App.Linux;

/// <summary>
/// The composition root: the one place that knows which real edge stands behind every port.
/// </summary>
/// <remarks>
/// <para>
/// The peer of the Windows shell's <c>App.xaml.cs</c>, and deliberately the same file in the same
/// order: the same ports, wired to the Linux adapters. Everything above it is testable without a
/// machine - <see cref="AppState"/> takes an <see cref="AppEnvironment"/> full of interfaces, and
/// the tests fill it with fakes. This file is the only one that names
/// <c>PipeWireCapture</c>, <c>EvdevKeyboardMonitor</c>, the autostart directory and the tray, and
/// it holds no logic of its own beyond wiring them together.
/// </para>
/// <para>
/// <b>Three of the Windows ports have no Linux edge behind them yet</b>, and each says so where it
/// is implemented rather than here: sleep, wake and audio-device changes in
/// <see cref="LinuxSystemEventSource"/>, the microphone settings page in
/// <see cref="LinuxMicrophoneAccess"/>, and the identity of the window being typed into in
/// <c>LinuxTextInserter.FrontmostApplication</c>.
/// </para>
/// </remarks>
public sealed partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? lifetime;
    private SingleInstanceLock? instance;
    private TrayIcon? tray;
    private LinuxSystemEventSource? systemEvents;
    private EvdevKeyboardMonitor? hotkeys;
    private EvdevShortcutMonitor? copyShortcut;
    private EvdevShortcutMonitor? recordingShortcut;
    private ControlSocketServer? control;
    private OverlayWindow? overlay;
    private PipeWireCapture? capture;
    private AppState? state;
    private SettingsWindow? settings;
    private LibraryWindow? library;
    private OnboardingWindow? onboarding;
    private DiagnosticLog? diagnostics;
    private PosixSignalRegistration[] signals = [];
    private bool reportedTrayFailure;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();

        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) { return; }

        lifetime = desktop;
        // There is no main window: closing Settings must not end a dictation product that lives in
        // the tray. This is the Avalonia counterpart of WPF's implicit "no StartupUri" behaviour,
        // and unlike it, it has to be said out loud.
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        desktop.Exit += (_, exit) => ShutDown(exit.ApplicationExitCode);

        StartDiagnostics(desktop.Args ?? []);
        InstallSignalHandlers();

        // The rules for what counts as "already running" - and why a copy killed with SIGKILL must
        // not count - live in SingleInstanceLock, where they can be checked without a machine.
        instance = SingleInstanceLock.Acquire(SingleInstanceLock.ApplicationName);
        if (!instance.IsHeld)
        {
            // Nothing is shown. The person did not ask for a window, they asked for the app to be
            // running - and it already is.
            instance.Dispose();
            instance = null;
            desktop.Shutdown();
            return;
        }

        Compose();
    }

    private void Compose()
    {
        var events = new LinuxSystemEventSource();
        var monitor = new EvdevKeyboardMonitor();
        // The copy chord reads the same devices through the same reader threads; a second
        // monitor opening /dev/input on its own would double every descriptor for one chord's
        // worth of comparison. A compositor keybind driving the control socket cannot press it -
        // see IRawKeySource.
        var chord = new EvdevShortcutMonitor(monitor);
        // A second subscriber on the same keyboard reader, not a second reader.
        var recordChord = new EvdevShortcutMonitor(monitor);
        var panel = new OverlayWindow();
        systemEvents = events;
        hotkeys = monitor;
        copyShortcut = chord;
        recordingShortcut = recordChord;
        overlay = panel;

        AppPaths paths = AppPaths.Standard();
        // Parakeet Ultra on transcribe.cpp. Parakeet v3 on sherpa-onnx stays only for a machine
        // that already holds it: it dictates with that until Ultra is downloaded and loaded.
        var transcriber = new LocalTranscriber(
            new EngineByModelFiles(new TranscribeCppAdapter(), new SherpaOnnxAdapter()));
        var inserter = new LinuxTextInserter(
            reportRestoreFailure: _ => state?.ReportClipboardRestoreFailure(),
            // The one wire without which the insertion path silently loses a guarantee: nothing on
            // Wayland reports keyboard state, so the monitor reading every key is the only thing
            // that can say whether Ctrl+V is safe to send yet.
            heldModifiers: () => monitor.HeldModifiers);
        var microphone = new LinuxMicrophoneAccess(ActivateOwnWindows, paths.Takes);

        var built = new AppState(new AppEnvironment
        {
            Paths = paths,
            Settings = SettingsStore.Under(paths),
            ModelManifest = ModelManifest.BundledUltra(),
            PreviousModelManifest = ModelManifest.Bundled(),
            Permissions = microphone,
            HotkeyMonitor = monitor,
            LastTakeShortcutMonitor = chord,
            // Ctrl+Alt+V rather than upstream's Control-Command-V: Super+letter is where keyd
            // setups put copy/paste remaps, and a remapped chord never arrives. 47 is KEY_V.
            DefaultLastTakeShortcut = new KeyCombination(47, HotkeyModifiers.Control | HotkeyModifiers.Alt),
            RecordingShortcutMonitor = recordChord,
            // Ctrl+Alt+R for upstream's Control-Command-R, for the same reason. 19 is KEY_R.
            DefaultRecordingShortcut = new KeyCombination(19, HotkeyModifiers.Control | HotkeyModifiers.Alt),
            ShortcutKeymap = LinuxShortcutKeymap.Instance,
            InputDevices = new PipeWireInputDevices(),
            // Our own windows are XWayland, where wtype's keymap does not apply; they take the
            // text directly. Everything else still goes through the inserter above.
            Inserter = new OwnWindowTextInserter(inserter, ActiveOwnSink),
            // The inserter waits up to three seconds for the paste to be read.
            InsertionDeadline = LinuxTextInserter.InsertionDeadline,
            Overlay = panel,
            Sounds = new SoundPlayer(),
            MakeCapture = (takes, onFailure, onSamples, preferredDeviceUid) =>
            {
                // The chosen microphone is a PipeWire node name; a missing one is linked to the
                // default source by WirePlumber, so the take never fails for it.
                var device = new PipeWireCapture(takes)
                {
                    Failed = onFailure,
                    PreferredDeviceUid = preferredDeviceUid,
                };
                // The capture already reduces each frame to its peak, and the peak is all the
                // waveform needs. Handing whole frames up would move that arithmetic across a
                // thread boundary hundreds of times a second for the same number.
                device.OnInputLevel = peak => onSamples([peak]);
                capture = device;
                return device;
            },
            Transcribe = engineDirectory => async (path, token) =>
            {
                // The recognized text never reaches the log - only how long it took and how many
                // characters came back, which is what tells "the engine answered" apart from "the
                // engine answered with nothing".
                long began = Stopwatch.GetTimestamp();
                // Read once, at the start: the recognition of a long dictation outlives the
                // session that asked for it often enough that the number must be the one this
                // call belongs to, not the one running by the time it answers.
                int session = DiagnosticLog.Session;
                DiagnosticLog.Emit("transcribe.begin", ("session", session));
                try
                {
                    await transcriber.Prepare(engineDirectory).ConfigureAwait(false);
                    ASRResult result =
                        await transcriber.Transcribe(path, token).ConfigureAwait(false);
                    DiagnosticLog.Emit(
                        "transcribe.end",
                        ("session", session),
                        ("ms", Stopwatch.GetElapsedTime(began)),
                        ("audio_ms", result.AudioDuration),
                        ("chars", result.Text.Length),
                        ("words", result.Words.Count));
                    return result;
                }
                catch (Exception error)
                {
                    DiagnosticLog.EmitException(
                        "transcribe.failed",
                        error,
                        ("session", session),
                        ("ms", Stopwatch.GetElapsedTime(began)));
                    throw;
                }
            },
            // One second: the promise is a tray icon that lies for no longer than two.
            PermissionPollInterval = TimeSpan.FromSeconds(1),
            ModelDownloader = new HttpClientModelDownloader(),
            MicrophoneConsent = microphone,
            SystemEvents = events,
            LocalTranscriber = transcriber,
            FocusedFieldReader = new FocusedFieldReader(),
            LaunchAtLogin = new LaunchAtLogin(),
            Clipboard = new HostOnlyClipboard(inserter),
            Folders = new FolderOpening(),
            // Imported files share the dictation engine; the store lives at AppPaths.Recordings().
            ImportRecognizer = new SharedTranscriberRecognizer(transcriber),
            RecordingTrasher = new GioTrasher(),
            RecordingCompressor = new FfmpegAudioCompressor(),
            // Meetings: the chosen microphone (a PipeWire node name, null - the default) and the
            // default output's monitor, each its own pw-record.
            MakeMeetingCapture = MeetingCaptures.FromSources(
                uid => PipeWireMeetingSource.Microphone(
                    uid,
                    uid is null ? null : new PipeWireInputDevices().Available().FirstOrDefault(device => device.Uid == uid)?.Name),
                PipeWireMeetingSource.SystemAudio),
            SupportsSystemAudio = true,
            LayoutHasAltGraph = KeyboardLayoutFacts.HasAltGraph,
            HotkeySettingsHint = HotkeyAdvice.LinuxSettingsHint,
            // evdev reads alongside and never grabs, so the key still reaches everything else. With
            // no keyboard open the only way in is a compositor keybind on the control socket, and
            // a keybind consumes its key.
            HotkeySourceSwallowsKey = _ => !monitor.IsRunning,
            KeydFoldsRightModifiers = Keyd.FoldsRightModifiers,
            LastTakeShortcutRewrite = shortcut => Keyd.FoldsRightModifiers()
                ? KeydConfig.Warning(KeydConfig.Read(), shortcut, LinuxShortcutKeymap.Instance)
                : null,
            MicrophoneRemedy = MicrophoneRemedy.Linux,
        });

        state = built;
        // The panel's button copies; the dictation key is the way to paste again. See OverlayWindow.
        panel.OnWaitingCopy = built.CopyWaitingWords;
        // An exception nobody caught ends the process; a sanitized record of it stays in the
        // journal's folder for the next error report. No core dump, no message, no upload.
        AppDomain.CurrentDomain.UnhandledException += (_, unhandled) =>
        {
            if (unhandled.ExceptionObject is Exception error) { built.RecordCrash(error); }
        };
        Apply(built.Appearance);
        built.OnInputLevel = level =>
        {
            panel.UpdateInputLevel(level);
            // The setup window shows the same signal while it is open: on a first launch it is
            // the only place a person can see that the microphone is actually delivering sound.
            onboarding?.UpdateInputLevel(level);
        };
        built.PropertyChanged += (_, args) =>
        {
            // The meters tick ten times a second and the menu shows none of them; rebuilding the
            // exported menu that often is a bus storm for nothing.
            if (args.PropertyName != nameof(AppState.LiveLevels)) { Dispatcher.UIThread.Post(RefreshTray); }
            // The panel is the only thing on screen while a dictation finishes, and a reload that
            // rides under the person's voice is the one thing that can keep it there for a minute.
            // Push it in as it ticks: nothing else redraws the panel between the key release and
            // the result.
            if (args.PropertyName == nameof(AppState.EnginePreparation))
            {
                panel.UpdateEnginePreparation(built.EnginePreparation);
            }
            // The capture is long-lived and speaks PipeWire's own name for the microphone; the
            // shared seam names it by its Windows shape. Bridged here rather than in the audio
            // package, which does not know the application's ports.
            if (args.PropertyName == nameof(AppState.InputDeviceUid) && capture is { } live)
            {
                live.PreferredDeviceUid = built.InputDeviceUid;
            }
            if (args.PropertyName == nameof(AppState.Appearance))
            {
                Dispatcher.UIThread.Post(() => Apply(built.Appearance));
            }
        };

        // The second way to the same key, always on. It costs a socket in the runtime directory and
        // buys the whole product back on a machine where /dev/input cannot be read - see
        // ControlSocket for the protocol and EvdevKeyboardMonitor.IsHotkeyDown for what happens
        // when both sources move the key. Failing to listen is not fatal and is not shown: the
        // keyboard is the product, this is the alternative, and it says so in the log.
        control = new ControlSocketServer(
            monitor,
            () => state?.DictationStateValue.ToString() ?? "starting",
            openLibrary: () => Dispatcher.UIThread.Post(ShowLibrary));
        control.Start();

        RefreshTray();

        if (!built.OnboardingCompleted)
        {
            ShowOnboarding();
        }
    }

    private void ShutDown(int code)
    {
        DiagnosticLog.Emit("app.exit", ("code", code));
        // A recording in progress is filed as ended by the quit; one that does not stop in time
        // is left to the next launch's recovery - the audio is on disk either way.
        if (state is { IsRecording: true } recording) { _ = recording.StopRecordingBeforeQuit().Wait(TimeSpan.FromSeconds(5)); }
        state?.Dispose();
        // Before the monitor it drives, and explicitly rather than at process exit: the socket file
        // outlives an unlinked process, and the next run would have to prove it stale.
        control?.Dispose();
        control = null;
        copyShortcut?.Dispose();
        copyShortcut = null;
        recordingShortcut?.Dispose();
        recordingShortcut = null;
        hotkeys?.Dispose();
        capture?.Dispose();
        if (tray is not null)
        {
            // Explicitly, and before the process goes: a status notifier item that is never
            // unregistered leaves a dead square on the person's panel until the host notices.
            tray.IsVisible = false;
            tray.Dispose();
            tray = null;
        }

        TrayIconArt.Release();
        overlay?.Close();
        instance?.Dispose();
        instance = null;
        foreach (PosixSignalRegistration registration in signals) { registration.Dispose(); }
        signals = [];
        diagnostics?.Dispose();
        diagnostics = null;
    }

    /// <summary>
    /// Leave the way a service is asked to leave.
    /// </summary>
    /// <remarks>
    /// A tray-only application is stopped by a signal far more often than by its own Quit item -
    /// a session logging out, a <c>systemctl --user stop</c>, a terminal's Ctrl+C. The default
    /// answer to SIGTERM ends the process where it stands: the notifier item is never withdrawn,
    /// the instance lock is left abandoned, and the diagnostic log loses whatever was buffered.
    /// Routing it through the lifetime's own shutdown makes an interrupted run indistinguishable
    /// from a chosen one.
    /// </remarks>
    private void InstallSignalHandlers()
    {
        signals =
        [
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, Handle),
            PosixSignalRegistration.Create(PosixSignal.SIGINT, Handle),
            PosixSignalRegistration.Create(PosixSignal.SIGHUP, Handle),
        ];

        void Handle(PosixSignalContext context)
        {
            context.Cancel = true;
            DiagnosticLog.Emit("app.signal", ("signal", context.Signal));
            Dispatcher.UIThread.Post(() => lifetime?.Shutdown());
        }
    }

    /// <summary>
    /// Open the diagnostic log, if this run was asked for one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// First thing in the startup, before the instance lock: a second copy that exits immediately
    /// is one of the things a person may be trying to explain, and it has to be able to say so.
    /// </para>
    /// <para>
    /// A failure to open the file is shown, not swallowed. Somebody asked for a log; leaving them
    /// to watch an empty path for an hour is worse than the missing log itself. And it is not
    /// fatal - the application runs without diagnostics, which is how it runs every other time.
    /// </para>
    /// <para>
    /// Shared for reading while we hold it, so the file can be tailed from another terminal while
    /// the very hang it is recording is still going on.
    /// </para>
    /// </remarks>
    private void StartDiagnostics(string[] arguments)
    {
        string? path = DiagnosticLog.PathFrom(
            Environment.GetEnvironmentVariable(DiagnosticLog.EnvironmentVariable),
            arguments);
        if (path is null) { return; }

        try
        {
            string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (folder is { Length: > 0 }) { Directory.CreateDirectory(folder); }

            var file = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            diagnostics = new DiagnosticLog(new StreamWriter(file), ownsSink: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            Notice.Show(
                $"OpenRamble couldn't open its diagnostic log: {error.Message}\n\n"
                + "The app will run normally, without diagnostics.");
            return;
        }

        DiagnosticLog.Install(diagnostics);
        DiagnosticLog.Emit(
            "app.start",
            ("pid", Environment.ProcessId),
            ("version", typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown"),
            ("os", Environment.OSVersion.VersionString),
            ("session_type", Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "unknown"));

        // The three ways a .NET exception escapes everything. Each one otherwise ends a dictation
        // - or the process - with nothing anywhere saying which line let go.
        Dispatcher.UIThread.UnhandledException += (_, unhandled) =>
            DiagnosticLog.EmitException("app.unhandled", unhandled.Exception, ("where", "dispatcher"));
        AppDomain.CurrentDomain.UnhandledException += (_, unhandled) =>
        {
            if (unhandled.ExceptionObject is Exception error)
            {
                DiagnosticLog.EmitException("app.unhandled", error, ("where", "domain"));
            }
        };
        TaskScheduler.UnobservedTaskException += (_, unobserved) =>
            DiagnosticLog.EmitException("app.unobserved", unobserved.Exception, ("where", "task"));
    }

    /// <summary>
    /// The tray icon, its tooltip and its menu, brought up to date.
    /// </summary>
    /// <remarks>
    /// <b>The menu is pushed, not pulled.</b> The Windows shell asks for the rows at click time,
    /// because a Win32 popup is built by the process that shows it. A status notifier item's menu
    /// is exported over D-Bus and drawn by the panel, which never asks us again - so the rows have
    /// to be rebuilt on every state change instead. They are cheap, and at rest nothing changes:
    /// the permission poll deliberately does not raise a property unless the answer moved.
    /// </remarks>
    private void RefreshTray()
    {
        if (state is null || lifetime is null) { return; }

        TrayActivity activity = TrayStatus.Activity(
            state.DictationStateValue,
            state.RecordingState == MeetingRecordingState.Recording);
        bool needsAttention = TrayStatus.SetupNeedsUserAction(state.MicrophoneState, state.ModelStateValue);
        TrayBadge badge = TrayStatus.Badge(
            activity,
            needsAttention,
            state.RecordingState == MeetingRecordingState.Recording
                && (state.LiveCaptureHealth.MarksRecordingDegraded || state.LiveMicrophoneHealth.MarksRecordingDegraded));

        if (tray is null)
        {
            tray = new TrayIcon();
            if (IsTrayReachable())
            {
                // Said once, and it is the strongest claim this platform allows: the item was
                // published. Whether a panel picked it up is what IsTrayReachable cannot answer.
                DiagnosticLog.Emit("tray.show", ("ok", true), ("published", true));
            }
            else
            {
                ReportTrayFailure();
            }
        }

        Bitmap? picture = TrayIconArt.Icon(badge);
        if (picture is not null) { tray.Icon = new WindowIcon(picture); }

        // The tooltip is the icon's accessibility label: a picture without words does not exist
        // for a blind person, and the tray is the app's only permanent presence.
        tray.ToolTipText = TrayStatus.AccessibilityLabel(
            state.DictationStateValue,
            state.IsDictationReady,
            state.RecoveredText is not null);
        tray.Menu = BuildMenu();
        tray.IsVisible = true;
    }

    /// <summary>
    /// Whether there is anywhere for a tray icon to go.
    /// </summary>
    /// <remarks>
    /// A weaker question than the Windows one, and honestly so. There the shell answers "I took
    /// it" or "I did not"; here the item is announced over the session bus to whichever panel
    /// registered as a status notifier host, and Avalonia reports neither the announcement nor its
    /// fate. What can be told apart is the case that is certain - no session bus at all, which is
    /// a bare compositor or a login shell - and that is worth telling apart, because it is the
    /// whole interface gone. A session bus with no host on it still slips through, and that is a
    /// gap: it takes a D-Bus client of our own to close.
    /// </remarks>
    private static bool IsTrayReachable() =>
        Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") is { Length: > 0 };

    /// <summary>
    /// Nowhere to put the icon.
    ///
    /// This is the app's whole visible presence, so the alternative to saying it is a process that
    /// is running, reading the keyboard, and showing nothing at all - indistinguishable from a
    /// launch that failed. Said once per run.
    /// </summary>
    private void ReportTrayFailure()
    {
        if (reportedTrayFailure) { return; }
        reportedTrayFailure = true;

        DiagnosticLog.Emit("tray.show", ("ok", false), ("error", "no session bus"));
        Notice.Show(
            "OpenRamble couldn't put its icon in the notification area, so its menu can't be "
            + "reached. The dictation key still works. A panel that shows status notifier icons - "
            + "Waybar, GNOME with the AppIndicator extension, KDE's system tray - is what it needs.");
    }

    /// <summary>
    /// The menu, from <see cref="MenuSections"/>. Which rows exist in which state is policy and is
    /// checked as a table; this only turns each row into a clickable one.
    /// </summary>
    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();
        if (state is null) { return menu; }

        IReadOnlyList<IReadOnlyList<MenuRow>> sections = MenuSections.Sections(
            state.DictationStateValue,
            state.IsDictationReady,
            state.RecoveredText is not null,
            state.MenuTakes.Count > 0,
            state.RecordingState,
            state.CanRecord,
            state.SupportsSystemAudio);

        foreach (IReadOnlyList<MenuRow> section in sections)
        {
            if (menu.Items.Count > 0) { menu.Items.Add(new NativeMenuItemSeparator()); }
            foreach (MenuRow row in section)
            {
                if (row == MenuRow.Takes)
                {
                    foreach (NativeMenuItem take in TakeRows()) { menu.Items.Add(take); }
                }
                else
                {
                    // The Library sits beside Settings, as upstream's "Library…" does. Added here
                    // rather than as a MenuRow: the Windows shell has no Library yet.
                    if (row == MenuRow.Settings) { menu.Items.Add(Item("Library…", ShowLibrary)); }
                    menu.Items.Add(Row(row));
                }
            }
        }

        return menu;
    }

    private NativeMenuItem Row(MenuRow row)
    {
        AppState current = state!;
        return row switch
        {
            MenuRow.StatusLine => Item(
                TrayStatus.StatusLine(
                    current.DictationStateValue,
                    current.IsDictationReady,
                    current.IsHandsFreeActive,
                    current.Hotkey.Title,
                    current.RecoveredText is not null),
                null),

            MenuRow.StopAndInsert => Item("Stop and Insert", current.FinishCurrentDictation),
            MenuRow.CancelDictation => Item("Cancel Dictation", current.CancelCurrentDictation),
            MenuRow.SetupHints => Item(
                DictationReadiness.Reason(
                    current.MicrophoneGranted,
                    current.ModelStateValue,
                    current.IsEngineReady,
                    current.HasEngineBeenReady) ?? "Setup needed",
                null),
            MenuRow.FinishSetup => Item("Finish Setup…", ShowOnboarding),
            MenuRow.InsertLastDictation => Item("Insert Last Dictation", current.RetryRecoveredText),
            MenuRow.CopyTakes => Nested(
                "Copy",
                current.MenuTakes.Select(take =>
                    Item(current.TakeMenuTitle(take, isLastTake: false), () => current.CopyRecentDictation(take)))),
            MenuRow.RecordingLine => Item(RecordingTitle(row), null),
            MenuRow.StartRecording => Item(RecordingTitle(row), () => _ = current.StartRecording(includeSystemAudio: true)),
            MenuRow.StartVoiceNote => Item(RecordingTitle(row), () => _ = current.StartRecording(includeSystemAudio: false)),
            MenuRow.PauseRecording => Item(RecordingTitle(row), () => _ = current.PauseRecording()),
            MenuRow.ResumeRecording => Item(RecordingTitle(row), () => _ = current.ResumeRecording()),
            MenuRow.StopRecording => Item(RecordingTitle(row), () => _ = current.StopRecording()),
            MenuRow.Settings => Item("Settings…", ShowSettings),
            MenuRow.Quit => Item("Quit OpenRamble", () => lifetime?.Shutdown()),
            _ => Item(string.Empty, null),
        };
    }

    private string RecordingTitle(MenuRow row)
    {
        AppState current = state!;
        return MenuSections.RecordingTitle(
            row,
            current.RecordingState,
            current.LiveDuration,
            current.RecordingShortcutDisplay,
            current.LiveCaptureHealth.MarksRecordingDegraded,
            current.LiveMicrophoneHealth.MarksRecordingDegraded);
    }

    /// <summary>
    /// The menu's takes, the last one first; a click pastes one into the app in front.
    /// </summary>
    /// <remarks>
    /// The paste waits for the menu to be gone. The menu is drawn by the panel, not by us, and
    /// while it is open the panel's popup may hold the keyboard; a Ctrl+V sent then would land in
    /// the popup, or nowhere. Upstream needs none of this: a macOS menu runs its action after it
    /// has closed, with the app in front never having lost focus.
    /// </remarks>
    private IEnumerable<NativeMenuItem> TakeRows()
    {
        AppState current = state!;
        return current.MenuTakes.Select((take, index) => Item(
            current.TakeMenuTitle(take, isLastTake: index == 0),
            () => _ = PasteAfterMenuCloses(current, take)));
    }

    private static readonly TimeSpan MenuCloseAllowance = TimeSpan.FromMilliseconds(300);

    private static async Task PasteAfterMenuCloses(AppState current, AppState.RecentDictation take)
    {
        await Task.Delay(MenuCloseAllowance).ConfigureAwait(false);
        current.PasteTake(take);
    }

    /// <summary>A row. No action means the row is present but disabled - a readout, not a button.</summary>
    private static NativeMenuItem Item(string title, Action? action)
    {
        var item = new NativeMenuItem(title) { IsEnabled = action is not null };
        if (action is not null) { item.Click += (_, _) => action(); }
        return item;
    }

    private static NativeMenuItem Nested(string title, IEnumerable<NativeMenuItem> children)
    {
        var submenu = new NativeMenu();
        foreach (NativeMenuItem child in children) { submenu.Items.Add(child); }
        return new NativeMenuItem(title) { Menu = submenu };
    }

    /// <summary>The window of ours that is in front, if one is; asked on the UI thread.</summary>
    private IDictationSink? ActiveOwnSink()
    {
        if (onboarding is { IsActive: true }) { return onboarding; }
        if (settings is { IsActive: true }) { return settings; }
        if (library is { IsActive: true }) { return library; }
        return null;
    }

    private void ShowSettings()
    {
        if (state is null) { return; }
        if (settings is null)
        {
            settings = new SettingsWindow(state);
            settings.Closed += (_, _) => settings = null;
        }

        settings.Show();
        settings.Activate();
    }

    /// <summary>One Library: a second click brings the open one forward.</summary>
    private void ShowLibrary()
    {
        if (state is null) { return; }
        if (library is null)
        {
            library = new LibraryWindow(state);
            library.Closed += (_, _) => library = null;
        }

        library.Show();
        library.Activate();
    }

    /// <summary>
    /// The setup window - the only path a new person has to a working product.
    /// </summary>
    /// <remarks>
    /// A failure to open it is caught and said out loud rather than taken down the unhandled path.
    /// On a first launch it is the only thing on screen, so a window that silently never appears
    /// leaves a person with a running process, no instructions and nothing to read.
    /// </remarks>
    private void ShowOnboarding()
    {
        if (state is null) { return; }

        try
        {
            if (onboarding is null)
            {
                var window = new OnboardingWindow(state);
                onboarding = window;
                window.Closed += (_, _) => onboarding = null;
            }

            onboarding.Show();
            onboarding.Activate();
        }
        catch (Exception error) when (error is InvalidOperationException or XamlLoadException)
        {
            Notice.Show($"OpenRamble couldn't open its setup window: {error.Message}");
        }
    }

    /// <summary>
    /// Bring our own windows back after something else took the focus. There is no main window to
    /// activate, so whatever the person was in the middle of is what comes forward.
    /// </summary>
    private void ActivateOwnWindows() => Dispatcher.UIThread.Post(() =>
    {
        if (lifetime is null) { return; }
        foreach (Window open in lifetime.Windows)
        {
            if (open is OverlayWindow) { continue; }
            open.Activate();
        }

        // The permission is re-read when we come back, and this is the only place on this platform
        // that knows we did: nothing tells an X11 or Wayland client that its application - rather
        // than one of its windows - became active.
        systemEvents?.Raise();
    });

    /// <summary>
    /// Light, dark, or whatever the desktop is doing. Handed to the application, which owns every
    /// window including the ones not made yet - the same reason the macOS original hands it to
    /// AppKit rather than to each view.
    /// </summary>
    private void Apply(AppAppearance appearance) =>
        RequestedThemeVariant = appearance switch
        {
            AppAppearance.Light => ThemeVariant.Light,
            AppAppearance.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
}

/// <summary>
/// Reading the field we inserted into - the only place the app looks inside another window, and
/// only when the person switched it on.
/// </summary>
/// <remarks>
/// Deferred, and on this platform further away than on the other one. Windows has UI Automation
/// and needs the work done against it; Wayland has no interface at all for reading another
/// application's text, and the nearest thing - AT-SPI, over the accessibility bus - reaches only
/// applications that publish themselves on it. Until then this returns nothing, which makes "learn
/// from my corrections" a switch that changes nothing rather than one that quietly reads windows
/// it cannot read correctly.
/// </remarks>
public sealed class FocusedFieldReader : IFocusedFieldReading
{
    public FocusedFieldHandle? CaptureFocusedField() => null;
}
