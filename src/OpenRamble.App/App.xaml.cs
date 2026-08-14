using System.Windows;
using System.Windows.Markup;
using DictationCore;
using LocalASR;
using OpenRamble.AppCore;

namespace OpenRamble.App;

/// <summary>
/// The composition root: the one place that knows which real edge stands behind every port.
/// </summary>
/// <remarks>
/// Everything above this file is testable without a machine - <see cref="AppState"/> takes an
/// <see cref="AppEnvironment"/> full of interfaces, and the tests fill it with fakes. This file is
/// the only one that names <c>WasapiCapture</c>, <c>TextInserter</c>, the registry and the tray,
/// and it holds no logic of its own beyond wiring them together.
/// </remarks>
public partial class App : Application
{
    private SingleInstanceLock? instance;
    private MessageWindow? window;
    private TrayIcon? tray;
    private SystemEventSource? systemEvents;
    private HotkeyMonitor? hotkeys;
    private OverlayWindow? overlay;
    private WasapiCapture? capture;
    private AppState? state;
    private SettingsWindow? settings;
    private OnboardingWindow? onboarding;
    private bool reportedTrayFailure;

    protected override void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);

        // The rules for what counts as "already running" - and why a copy killed from Task Manager
        // must not count - live in SingleInstanceLock, where they can be checked without a machine.
        instance = SingleInstanceLock.Acquire(SingleInstanceLock.ApplicationName);
        if (!instance.IsHeld)
        {
            // Nothing is shown. The person did not ask for a window, they asked for the app to be
            // running - and it already is.
            instance.Dispose();
            instance = null;
            Shutdown();
            return;
        }

        window = new MessageWindow();
        tray = new TrayIcon(window);
        systemEvents = new SystemEventSource(window);
        hotkeys = new HotkeyMonitor();
        overlay = new OverlayWindow();

        AppPaths paths = AppPaths.Standard();
        var transcriber = new LocalTranscriber(new SherpaOnnxAdapter());
        var inserter = new TextInserter(reportRestoreFailure: _ => state?.ReportClipboardRestoreFailure());

        state = new AppState(new AppEnvironment
        {
            Paths = paths,
            Settings = SettingsStore.Under(paths),
            Permissions = new MicrophonePermissions(),
            HotkeyMonitor = hotkeys,
            Inserter = inserter,
            Overlay = overlay,
            Sounds = new SoundPlayer(),
            MakeCapture = (takes, onFailure, onSamples) =>
            {
                var device = new WasapiCapture(takes) { Failed = onFailure };
                // The capture already reduces each frame to its peak, and the peak is all the
                // waveform needs. Handing whole frames up would move that arithmetic across a
                // thread boundary hundreds of times a second for the same number.
                device.OnInputLevel = peak => onSamples([peak]);
                capture = device;
                return device;
            },
            Transcribe = (engineDirectory, languageHint) => async (path, token) =>
            {
                await transcriber.Prepare(engineDirectory).ConfigureAwait(false);
                return await transcriber.Transcribe(path, languageHint(), token).ConfigureAwait(false);
            },
            // One second: the promise is a tray icon that lies for no longer than two.
            PermissionPollInterval = TimeSpan.FromSeconds(1),
            ModelDownloader = new HttpClientModelDownloader(),
            MicrophoneConsent = new MicrophoneConsentFlow(ActivateOwnWindows),
            SystemEvents = systemEvents,
            LocalTranscriber = transcriber,
            FocusedFieldReader = new FocusedFieldReader(),
            LaunchAtLogin = new LaunchAtLogin(),
            Clipboard = new HostOnlyClipboard(),
            Folders = new FolderOpening(),
            LayoutHasAltGraph = KeyboardLayoutFacts.HasAltGraph,
        });

        state.OnInputLevel = level =>
        {
            overlay.UpdateInputLevel(level);
            // The setup window shows the same signal while it is open: on a first launch it is
            // the only place a person can see that the microphone is actually delivering sound.
            onboarding?.UpdateInputLevel(level);
        };
        state.PropertyChanged += (_, args) =>
        {
            Dispatcher.BeginInvoke(RefreshTray);
            // The panel is the only thing on screen while a dictation finishes, and a reload that
            // rides under the person's voice is the one thing that can keep it there for a minute.
            // Push it in as it ticks: nothing else redraws the panel between the key release and
            // the result.
            if (args.PropertyName == nameof(AppState.EnginePreparation))
            {
                overlay.UpdateEnginePreparation(state.EnginePreparation);
            }
        };
        tray.MenuProvider = BuildMenu;
        RefreshTray();

        if (!state.OnboardingCompleted)
        {
            ShowOnboarding();
        }
    }

    protected override void OnExit(ExitEventArgs args)
    {
        state?.Dispose();
        hotkeys?.Dispose();
        capture?.Dispose();
        tray?.Dispose();
        systemEvents?.Dispose();
        window?.Dispose();
        instance?.Dispose();
        base.OnExit(args);
    }

    private void RefreshTray()
    {
        if (tray is null || state is null) { return; }

        TrayActivity activity = TrayStatus.Activity(state.DictationStateValue);
        bool needsAttention = TrayStatus.SetupNeedsUserAction(state.MicrophoneState, state.ModelStateValue);
        TrayBadge badge = TrayStatus.Badge(activity, needsAttention);

        // The tooltip is the icon's accessibility label: a picture without words does not exist
        // for a blind person, and the tray is the app's only permanent presence.
        bool showing = tray.Show(
            badge,
            TrayStatus.AccessibilityLabel(
                state.DictationStateValue,
                state.IsDictationReady,
                state.RecoveredText is not null));

        if (!showing) { ReportTrayFailure(); }
    }

    /// <summary>
    /// The shell refused the icon.
    ///
    /// This is the app's whole visible presence, so the alternative to saying it is a process that
    /// is running, holding the keyboard hook, and showing nothing at all - indistinguishable from
    /// a launch that failed. Said once per run: the registration is retried on every status
    /// change, and a message per attempt would be its own fault.
    /// </summary>
    /// <remarks>
    /// A message box, not a log file: the project writes no diagnostics to disk, and a failure the
    /// person cannot see is the same as no failure at all.
    /// </remarks>
    private void ReportTrayFailure()
    {
        if (reportedTrayFailure) { return; }
        reportedTrayFailure = true;

        MessageBox.Show(
            "OpenRamble couldn't put its icon in the notification area, so its menu can't be "
            + "reached. The dictation key still works. Restarting OpenRamble usually fixes this.",
            "OpenRamble",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    /// <summary>
    /// The menu, from <see cref="MenuSections"/>. Which rows exist in which state is policy and is
    /// checked as a table; this only turns each row into a clickable one.
    /// </summary>
    private IReadOnlyList<TrayMenuItem> BuildMenu()
    {
        if (state is null) { return []; }

        var items = new List<TrayMenuItem>();
        IReadOnlyList<IReadOnlyList<MenuRow>> sections = MenuSections.Sections(
            state.DictationStateValue,
            state.IsDictationReady,
            state.RecoveredText is not null,
            state.RecentDictations.Count > 0,
            state.CanCopyRawDictation);

        foreach (IReadOnlyList<MenuRow> section in sections)
        {
            if (items.Count > 0) { items.Add(TrayMenuItem.Separator); }
            foreach (MenuRow row in section) { items.Add(Row(row)); }
        }

        return items;
    }

    private TrayMenuItem Row(MenuRow row)
    {
        AppState current = state!;
        return row switch
        {
            MenuRow.StatusLine => new TrayMenuItem(
                TrayStatus.StatusLine(
                    current.DictationStateValue,
                    current.IsDictationReady,
                    current.IsHandsFreeActive,
                    current.Hotkey.Title,
                    current.RecoveredText is not null),
                null),

            MenuRow.StopAndInsert => new TrayMenuItem("Stop and Insert", current.FinishCurrentDictation),
            MenuRow.CancelDictation => new TrayMenuItem("Cancel Dictation", current.CancelCurrentDictation),
            MenuRow.SetupHints => new TrayMenuItem(
                DictationReadiness.Reason(
                    current.MicrophoneGranted,
                    current.ModelStateValue,
                    current.IsEngineReady,
                    current.HasEngineBeenReady) ?? "Setup needed",
                null),
            MenuRow.FinishSetup => new TrayMenuItem("Finish Setup…", ShowOnboarding),
            MenuRow.InsertLastDictation => new TrayMenuItem("Insert Last Dictation", current.RetryRecoveredText),
            MenuRow.RecentDictations => new TrayMenuItem("Recent Dictations", null)
            {
                Children = [.. current.RecentDictations.Select(entry =>
                    new TrayMenuItem(entry.MenuTitle, () => current.CopyRecentDictation(entry)))],
            },
            MenuRow.CopyLastAsSpoken => new TrayMenuItem("Copy Last as Spoken", current.CopyRawDictation),
            MenuRow.Settings => new TrayMenuItem("Settings…", ShowSettings),
            MenuRow.Quit => new TrayMenuItem("Quit OpenRamble", Shutdown),
            _ => TrayMenuItem.Separator,
        };
    }

    private void ShowSettings()
    {
        if (state is null) { return; }
        if (settings is null || !settings.IsLoaded)
        {
            settings = new SettingsWindow(state);
            settings.Closed += (_, _) => settings = null;
        }

        settings.Show();
        settings.Activate();
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
        catch (Exception error) when (error is InvalidOperationException or XamlParseException)
        {
            MessageBox.Show(
                $"OpenRamble couldn't open its setup window: {error.Message}",
                "OpenRamble",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Bring our own windows back after the Settings app took the focus. There is no main window
    /// to activate, so whatever the person was in the middle of is what comes forward.
    /// </summary>
    private void ActivateOwnWindows() => Dispatcher.BeginInvoke(() =>
    {
        foreach (Window open in Windows)
        {
            if (open is OverlayWindow) { continue; }
            open.Activate();
        }
    });
}

/// <summary>
/// Reading the field we inserted into - the only place the app looks inside another window, and
/// only when the person switched it on.
/// </summary>
/// <remarks>
/// Deferred, deliberately and visibly: doing it properly means driving UI Automation's
/// <c>TextPattern</c> against an arbitrary foreign control, which is its own wave of work. Until
/// then this returns nothing, which makes "learn from my corrections" a switch that changes
/// nothing rather than one that quietly reads windows it cannot read correctly.
/// </remarks>
public sealed class FocusedFieldReader : IFocusedFieldReading
{
    public FocusedFieldHandle? CaptureFocusedField() => null;
}
