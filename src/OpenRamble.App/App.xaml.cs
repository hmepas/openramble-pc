using System.Windows;
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
    /// <summary>
    /// One instance per user session.
    /// </summary>
    /// <remarks>
    /// Two running copies would install two low-level keyboard hooks and fight over one hotkey:
    /// the first to see the press starts a dictation, the second starts another, and both hold the
    /// microphone. <c>Local\</c> scope, not <c>Global\</c>: two people signed into the same machine
    /// each get their own dictation, their own model folder and their own dictionary.
    /// </remarks>
    private const string InstanceMutexName = @"Local\OpenRamble.SingleInstance";

    private Mutex? instance;
    private MessageWindow? window;
    private TrayIcon? tray;
    private SystemEventSource? systemEvents;
    private HotkeyMonitor? hotkeys;
    private OverlayWindow? overlay;
    private WasapiCapture? capture;
    private AppState? state;
    private SettingsWindow? settings;

    protected override void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);

        instance = new Mutex(initiallyOwned: true, InstanceMutexName, out bool first);
        if (!first)
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

        state.OnInputLevel = level => overlay.UpdateInputLevel(level);
        state.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(RefreshTray);
        tray.MenuProvider = BuildMenu;
        RefreshTray();

        if (!state.OnboardingCompleted)
        {
            new OnboardingWindow(state).Show();
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
        tray.Show(
            badge,
            TrayStatus.AccessibilityLabel(
                state.DictationStateValue,
                state.IsDictationReady,
                state.RecoveredText is not null));
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

    private void ShowOnboarding()
    {
        if (state is null) { return; }
        new OnboardingWindow(state).Show();
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
