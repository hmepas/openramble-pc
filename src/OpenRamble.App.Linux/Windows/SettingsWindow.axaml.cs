using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DictationCore;
using OpenRamble.AppCore;
using OpenRamble.AppCore.Audio;

namespace OpenRamble.App.Linux;

/// <summary>
/// The settings window: four pages - General, Setup, Dictionary, About - behind a segmented
/// switcher at the top, as upstream's window (0.37+). The dictation history is listed in the
/// Library; its limit and Delete All stay under General.
/// </summary>
/// <remarks>
/// <para>
/// Plain Avalonia, no styling layer beyond the Fluent theme the application sets. The Windows
/// window takes every control from the shell so that high contrast, text scaling and the screen
/// reader come for free; nothing on this platform hands out controls, so what is left is to keep
/// the markup ordinary and the automation names on it.
/// </para>
/// <para>
/// <b>Every setting has its own revert arrow</b> (see <c>RevertToDefault.swift</c>): a person who
/// wants the dictation key back does not want their dictionary emptied, and a single button that
/// does both is a button nobody dares press. The arrow is live only where the value differs
/// from <see cref="SettingsDefaults"/>, which also answers "what did I change in here?".
/// </para>
/// <para>
/// <b>There is no Updates section, and that is a decision, not an omission.</b> This port ships no
/// updater; inventing a settings page for a mechanism that does not exist would promise the person
/// that the app checks for new versions.
/// </para>
/// <para>
/// The window renders <see cref="AppState"/> and calls back into it. What every string says is
/// decided by <see cref="ModelStatus"/>, <see cref="PermissionStatus"/>,
/// <see cref="HotkeyAdvice"/> and <see cref="AudioInputDevicePolicy"/>, all of which are checked
/// without a screen.
/// </para>
/// </remarks>
public sealed partial class SettingsWindow : Window, IDictationSink
{
    /// <summary>
    /// Dictation over the settings window goes into whichever text box has the caret - a
    /// dictionary phrase, most likely. A single-line box takes no line break, and a window with no
    /// text box focused takes nothing: the field is the only thing here that can hold words.
    /// </summary>
    public bool TryInsert(string text)
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not TextBox box) { return false; }
        if (text == "\n" && !box.AcceptsReturn) { return true; }

        DictationSinkTextBox.Insert(box, text);
        return true;
    }

    /// <summary>The choices the macOS picker offers, in its order.</summary>
    private static readonly int[] HistoryLimits = [5, 10, 20, 50];

    private static readonly Uri SourceUrl = new("https://github.com/mikwiseman/openramble");

    private readonly AppState state;

    /// <summary>
    /// The microphones as they were listed when the window opened or came back to the front.
    /// </summary>
    /// <remarks>
    /// Read then, not on every render: listing them is a subprocess on this platform, and the
    /// window re-renders on every property change, including the once-a-second preparation
    /// tick. Coming back to the window is the moment a person has just plugged something in.
    /// </remarks>
    private IReadOnlyList<AudioInputDevice> devices = [];

    /// <summary>
    /// Guards the controls against reacting to what the render just set.
    /// </summary>
    /// <remarks>
    /// It has to cover the toggles too, which is the one behavioural difference from the WPF
    /// window. There <c>Click</c> fires for a person and never for code; here the only event a
    /// toggle raises is <c>IsCheckedChanged</c>, which cannot tell the two apart - and without the
    /// guard every render would write every setting back to disk.
    /// </remarks>
    private bool updating;

    public SettingsWindow(AppState state)
    {
        this.state = state;
        InitializeComponent();
        Icon = TrayIconArt.WindowMark();

        HotkeyPicker.ItemsSource = DictationHotkeyInfo.AllCases.Select(hotkey => hotkey.Title).ToArray();
        PlacementPicker.ItemsSource = DictationOverlayPlacements.AllCases.Select(placement => placement.Title).ToArray();
        ThemePicker.ItemsSource = AppAppearances.AllCases.Select(appearance => appearance.Title).ToArray();
        HistoryLimitPicker.ItemsSource = HistoryLimits.Select(count => $"Last {count}").ToArray();
        LastTakeShortcutRecorder.Keymap = LinuxShortcutKeymap.Instance;
        RecordingShortcutRecorder.Keymap = LinuxShortcutKeymap.Instance;
        VersionText.Text = Version();

        foreach (RadioButton tab in new[] { GeneralTab, SetupTab, DictionaryTab, AboutTab })
        {
            tab.IsCheckedChanged += (_, _) => ShowPage();
        }
        GeneralTab.IsChecked = true;
        ShowPage();

        // The cap is the key's face; pressing it opens the same list the picker under it holds.
        HotkeyCap.PointerPressed += (_, _) => HotkeyPicker.IsDropDownOpen = true;
        ModelRow.ActionClick += (_, _) => { if (modelAction is { } action) { Perform(action); } };
        UpgradeRow.ActionClick += (_, _) => { if (upgradeAction is { } action) { Perform(action); } };
        MicrophoneRow.ActionClick += OnPermissionAction;

        HotkeyPicker.SelectionChanged += (_, _) => Pick(HotkeyPicker, DictationHotkeyInfo.AllCases, hotkey => state.Hotkey = hotkey);
        PlacementPicker.SelectionChanged += (_, _) => Pick(PlacementPicker, DictationOverlayPlacements.AllCases, placement => state.OverlayPlacement = placement);
        ThemePicker.SelectionChanged += (_, _) => Pick(ThemePicker, AppAppearances.AllCases, appearance => state.Appearance = appearance);
        HistoryLimitPicker.SelectionChanged += (_, _) => Pick(HistoryLimitPicker, HistoryLimits, limit => state.HistoryLimit = limit);
        DevicePicker.SelectionChanged += OnDeviceChanged;

        LastTakeShortcutRecorder.ShortcutChanged += shortcut => state.LastTakeShortcut = shortcut;
        LastTakeShortcutRecorder.ProblemChanged += _ => RenderLastTakeProblem();
        RecordingShortcutRecorder.ShortcutChanged += shortcut => state.RecordingShortcut = shortcut;
        RecordingShortcutRecorder.ProblemChanged += _ => RenderRecordingProblem();

        Toggle(LaunchToggle, on => state.LaunchAtLogin = on);
        Toggle(TrailingSpaceToggle, on => state.AppendsTrailingSpace = on);
        Toggle(SoundsToggle, on => state.SoundsEnabled = on);
        Toggle(SilenceToggle, on => state.StopsOnSilence = on);
        Toggle(LearnToggle, on => state.LearnFromEdits = on);
        Toggle(LoggingToggle, on => state.DetailedLogging = on);

        state.PropertyChanged += OnStateChanged;
        Activated += (_, _) =>
        {
            devices = state.AvailableInputDevices;
            Render();
        };
        Closed += (_, _) =>
        {
            state.PropertyChanged -= OnStateChanged;
        };

        // Permissions are granted in system settings, and returned here a person should see a
        // fresh state, and not what was before leaving.
        state.RefreshPermissions();
        devices = state.AvailableInputDevices;
        Render();
    }

    /// <summary>
    /// Version and build number. The first thing asked in any bug report, and the only place it
    /// can be read: a tray application has no About box of its own.
    /// </summary>
    private static string Version()
    {
        Assembly assembly = typeof(SettingsWindow).Assembly;
        string informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "—";
        // The informational version carries the commit after a '+'; the person wants the number.
        int plus = informational.IndexOf('+', StringComparison.Ordinal);
        return "Version " + (plus > 0 ? informational[..plus] : informational);
    }

    private void Toggle(ToggleSwitch toggle, Action<bool> apply) =>
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (!updating) { apply(toggle.IsChecked == true); }
        };

    private void Pick<T>(ComboBox picker, IReadOnlyList<T> cases, Action<T> apply)
    {
        if (updating || picker.SelectedIndex < 0 || picker.SelectedIndex >= cases.Count) { return; }
        apply(cases[picker.SelectedIndex]);
    }

    /// <summary>
    /// Select a page by its control-socket name (<c>general</c>, <c>setup</c>, <c>dictionary</c>,
    /// <c>about</c>); an unknown name leaves the page as it is.
    /// </summary>
    public void SelectPage(string page)
    {
        RadioButton? tab = page switch
        {
            "general" => GeneralTab,
            "setup" => SetupTab,
            "dictionary" => DictionaryTab,
            "about" => AboutTab,
            _ => null,
        };
        if (tab is not null) { tab.IsChecked = true; }
    }

    /// <summary>One page at a time: the one whose segment is selected.</summary>
    private void ShowPage()
    {
        GeneralPage.IsVisible = GeneralTab.IsChecked == true;
        SetupPage.IsVisible = SetupTab.IsChecked == true;
        DictionaryPage.IsVisible = DictionaryTab.IsChecked == true;
        AboutPage.IsVisible = AboutTab.IsChecked == true;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs args) =>
        Dispatcher.UIThread.Post(Render);

    private void Render()
    {
        updating = true;
        try
        {
            RenderGeneral();
            RenderPermission();
            RenderModel();
            RenderHistory();
            RenderDictionary();
            RenderAbout();
        }
        finally
        {
            updating = false;
        }
    }

    // MARK: - General

    /// <summary>A refused press wins; otherwise what a key remapper does to the chosen chord.</summary>
    private void RenderLastTakeProblem()
    {
        string? problem = LastTakeShortcutRecorder.Problem ?? state.LastTakeShortcutWarning;
        LastTakeShortcutProblem.Text = problem ?? string.Empty;
        LastTakeShortcutProblem.Foreground = StatusBrushes.Attention;
        LastTakeShortcutProblem.IsVisible = problem is not null;
    }

    private void RenderRecordingProblem()
    {
        string? problem = RecordingShortcutRecorder.Problem ?? state.RecordingShortcutWarning;
        RecordingShortcutProblem.Text = problem ?? string.Empty;
        RecordingShortcutProblem.Foreground = StatusBrushes.Attention;
        RecordingShortcutProblem.IsVisible = problem is not null;
    }

    private void RenderGeneral()
    {
        HotkeyPicker.SelectedIndex = DictationHotkeyInfo.AllCases.ToList().IndexOf(state.Hotkey);
        Changed(RevertHotkey, state.Hotkey != SettingsDefaults.Hotkey);
        HotkeyCap.Text = state.Hotkey.Title;
        // The raw key edge, the same one the first-launch try-out shows: the cap answers the hand.
        HotkeyCap.IsPressed = state.IsHotkeyHeld;
        HotkeyWarning.Text = state.HotkeyWarning ?? string.Empty;
        HotkeyWarning.Foreground = StatusBrushes.Attention;
        HotkeyWarning.IsVisible = state.HotkeyWarning is not null;
        HotkeyHint.IsVisible = state.HotkeyWarning is null;

        LastTakeShortcutRecorder.Shortcut = state.LastTakeShortcut;
        Changed(RevertLastTakeShortcut, state.LastTakeShortcut != state.DefaultLastTakeShortcut);
        RenderLastTakeProblem();

        RecordingShortcutRecorder.Shortcut = state.RecordingShortcut;
        Changed(RevertRecordingShortcut, state.RecordingShortcut != state.DefaultRecordingShortcut);
        RenderRecordingProblem();

        LaunchToggle.IsChecked = state.LaunchAtLogin;
        Changed(RevertLaunch, state.LaunchAtLogin != SettingsDefaults.LaunchAtLogin);
        TrailingSpaceToggle.IsChecked = state.AppendsTrailingSpace;
        Changed(RevertTrailingSpace, state.AppendsTrailingSpace != SettingsDefaults.AppendsTrailingSpace);
        SoundsToggle.IsChecked = state.SoundsEnabled;
        Changed(RevertSounds, state.SoundsEnabled != SettingsDefaults.SoundsEnabled);
        SilenceToggle.IsChecked = state.StopsOnSilence;
        Changed(RevertSilence, state.StopsOnSilence != SettingsDefaults.StopsOnSilence);

        RenderDevices();
        RenderCommandLineTool();

        PlacementPicker.SelectedIndex = DictationOverlayPlacements.AllCases.ToList().IndexOf(state.OverlayPlacement);
        Changed(RevertPlacement, state.OverlayPlacement != SettingsDefaults.OverlayPlacement);
    }

    /// <summary>
    /// Upstream's <c>CommandLineToolSettings</c>: install, reinstall or remove the
    /// <c>~/.local/bin/openramble</c> link. The status is read from disk on every render, and a
    /// failure from the last button press stays until the next one.
    /// </summary>
    private void RenderCommandLineTool()
    {
        string? link = CommandLineToolInstaller.InstalledLink();
        CliInstallButton.Content = link is null ? "Install command-line tool" : "Reinstall command-line tool";
        CliUninstallButton.IsVisible = link is not null;
        CliUsage.IsVisible = link is not null;
        CliPathHint.IsVisible = link is not null && !CommandLineToolInstaller.LinkFolderOnPath();
        if (commandLineToolFailure is not null)
        {
            CliStatus.Text = commandLineToolFailure;
            CliStatus.Foreground = StatusBrushes.Attention;
            CliStatus.IsVisible = true;
        }
        else
        {
            CliStatus.Text = link is null ? string.Empty : $"Installed at {link}";
            CliStatus.ClearValue(TextBlock.ForegroundProperty);
            CliStatus.IsVisible = link is not null;
        }
    }

    private string? commandLineToolFailure;

    private void OnInstallCommandLineTool(object? sender, RoutedEventArgs args) =>
        ChangeCommandLineTool(() => CommandLineToolInstaller.Install());

    private void OnUninstallCommandLineTool(object? sender, RoutedEventArgs args) =>
        ChangeCommandLineTool(() => CommandLineToolInstaller.Uninstall());

    private void ChangeCommandLineTool(Action change)
    {
        try
        {
            change();
            commandLineToolFailure = null;
        }
        catch (Exception error) when (error is CommandLineToolInstaller.Failure or IOException or UnauthorizedAccessException)
        {
            commandLineToolFailure = error.Message;
        }

        updating = true;
        try
        {
            RenderCommandLineTool();
        }
        finally
        {
            updating = false;
        }
    }

    /// <summary>
    /// "System default" first, then whatever the desktop can hear through right now. A chosen
    /// microphone that is not on the list stays selected by name, so the notice under the
    /// picker has something to point at.
    /// </summary>
    private void RenderDevices()
    {
        var names = new List<string>(devices.Count + 2) { "System default" };
        names.AddRange(devices.Select(device => device.Name));
        int selected = 0;
        string? chosen = state.InputDeviceUid;
        if (chosen is not null)
        {
            int index = IndexOfDevice(chosen);
            if (index >= 0)
            {
                selected = index + 1;
            }
            else
            {
                names.Add("Chosen microphone (not connected)");
                selected = names.Count - 1;
            }
        }

        DevicePicker.ItemsSource = names;
        DevicePicker.SelectedIndex = selected;
        Changed(RevertDevice, chosen != SettingsDefaults.InputDeviceUid);

        // The same sentence AppState would give, computed against the list already read rather
        // than through a second enumeration.
        string? notice = AudioInputDevicePolicy.Notice(chosen, devices);
        DeviceNotice.Text = notice ?? string.Empty;
        DeviceNotice.Foreground = StatusBrushes.Attention;
        DeviceNotice.IsVisible = notice is not null;
    }

    private int IndexOfDevice(string uid)
    {
        for (int index = 0; index < devices.Count; index++)
        {
            if (string.Equals(devices[index].Uid, uid, StringComparison.Ordinal)) { return index; }
        }

        return -1;
    }

    private void OnDeviceChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (updating) { return; }
        int index = DevicePicker.SelectedIndex;
        if (index == 0) { state.InputDeviceUid = null; }
        else if (index >= 1 && index <= devices.Count) { state.InputDeviceUid = devices[index - 1].Uid; }
        // The "not connected" row keeps the stored choice; picking it again changes nothing.
    }

    /// <summary>
    /// Kept in the layout rather than removed when inactive: a control that appears and
    /// disappears makes every row jump as settings change.
    /// </summary>
    private static void Changed(Button revert, bool isChanged)
    {
        revert.IsEnabled = isChanged;
        revert.Opacity = isChanged ? 1 : 0;
        AutomationProperties.SetAccessibilityView(
            revert,
            isChanged ? AccessibilityView.Content : AccessibilityView.Raw);
    }

    private void OnRevertHotkey(object? sender, RoutedEventArgs args) => state.Hotkey = SettingsDefaults.Hotkey;

    private void OnRevertLastTakeShortcut(object? sender, RoutedEventArgs args) => state.LastTakeShortcut = state.DefaultLastTakeShortcut;

    private void OnRevertRecordingShortcut(object? sender, RoutedEventArgs args) => state.RecordingShortcut = state.DefaultRecordingShortcut;

    private void OnRevertLaunch(object? sender, RoutedEventArgs args) => state.LaunchAtLogin = SettingsDefaults.LaunchAtLogin;


    private void OnRevertTrailingSpace(object? sender, RoutedEventArgs args) => state.AppendsTrailingSpace = SettingsDefaults.AppendsTrailingSpace;

    private void OnRevertSounds(object? sender, RoutedEventArgs args) => state.SoundsEnabled = SettingsDefaults.SoundsEnabled;

    private void OnRevertDevice(object? sender, RoutedEventArgs args) => state.InputDeviceUid = SettingsDefaults.InputDeviceUid;

    private void OnRevertSilence(object? sender, RoutedEventArgs args) => state.StopsOnSilence = SettingsDefaults.StopsOnSilence;

    private void OnRevertPlacement(object? sender, RoutedEventArgs args) => state.OverlayPlacement = SettingsDefaults.OverlayPlacement;


    private void OnRevertTheme(object? sender, RoutedEventArgs args) => state.Appearance = SettingsDefaults.Appearance;

    private void OnRevertLogging(object? sender, RoutedEventArgs args) => state.DetailedLogging = SettingsDefaults.DetailedLogging;

    private void RenderPermission()
    {
        PermissionStatus permission = PermissionStatus.Microphone(
            state.MicrophoneState,
            state.MicrophoneGranted
                ? "OpenRamble can hear you. It listens only while you hold the dictation key."
                : "Dictation cannot start without it. OpenRamble opens the microphone only while you hold the dictation key.",
            state.MicrophoneRemedy);

        MicrophoneRow.Title = permission.Title;
        MicrophoneRow.Detail = permission.Detail;
        MicrophoneRow.Glyph = permission.Granted
            ? StateGlyphKind.Pasted
            : state.MicrophoneState == MicrophoneConsentState.Denied ? StateGlyphKind.Silent : StateGlyphKind.Attention;
        MicrophoneRow.ActionText = permission.ActionTitle;
        MicrophoneRow.IsPrimaryAction = true;
        AutomationProperties.SetName(MicrophoneRow, $"{permission.Title} — {permission.AccessibilityValue}");
        AutomationProperties.SetName(MicrophoneRow.ActionButton, permission.ActionAccessibilityLabel ?? string.Empty);

        // Whether the key reaches OpenRamble at all: the input device, a compositor keybind or
        // the control socket. A hook that never installed has no symptom of its own.
        string? failure = state.HotkeyTrackingFailure;
        KeyTrackingRow.Glyph = failure is null ? StateGlyphKind.Pasted : StateGlyphKind.Attention;
        KeyTrackingRow.Detail = failure ?? $"OpenRamble notices {state.Hotkey.Title} and types the finished text at your cursor.";
    }

    // MARK: - Recognition

    private ModelStatus.Action? modelAction;

    private ModelStatus.Action? upgradeAction;

    private void RenderModel()
    {
        ModelStatus status = ModelStatus.Make(
            state.ModelStateValue,
            ModelStatus.Place.Settings,
            state.EnginePreparation,
            state.RemainingDownloadMegabytes == 0
                ? ModelStatus.FullDownloadMegabytes
                : state.RemainingDownloadMegabytes,
            state.IsEngineReady,
            state.IsPreparingEngine);
        modelAction = RenderRow(status, ModelRow);

        // The newer model is offered beside the one that keeps dictating, never instead of it.
        UpgradeRow.IsVisible = state.UpgradeState is not null;
        UpgradeDivider.IsVisible = state.UpgradeState is not null;
        upgradeAction = state.UpgradeState is { } upgrade
            ? RenderRow(ModelStatus.Upgrade(upgrade, state.RemainingDownloadMegabytes), UpgradeRow)
            : null;
    }

    /// <summary>
    /// A model card as one setup row. Every status carries at most one action; the step
    /// forward (download, retry, repair) is ink, cancelling or deleting stays quiet.
    /// </summary>
    private static ModelStatus.Action? RenderRow(ModelStatus status, SetupRow row)
    {
        ModelStatus.Action? action = status.Actions.Count > 0 ? status.Actions[0] : null;
        row.Title = status.Title;
        row.Detail = (status.Detail, status.ProgressLabel) switch
        {
            (null, null) => null,
            ({ } detail, null) => detail,
            (null, { } label) => label,
            ({ } detail, { } label) => detail + "\n" + label,
        };
        row.Progress = status.Progress;
        row.Glyph = status.ToneValue switch
        {
            ModelStatus.Tone.Success => StateGlyphKind.Pasted,
            ModelStatus.Tone.Failure => StateGlyphKind.Attention,
            _ => action == ModelStatus.Action.Install ? StateGlyphKind.Silent : StateGlyphKind.Working,
        };
        row.ActionText = action is { } shown ? status.TitleFor(shown) : null;
        row.IsPrimaryAction = action is ModelStatus.Action.Install or ModelStatus.Action.Retry or ModelStatus.Action.Repair;
        string? hint = action is { } hinted ? status.HintFor(hinted) : null;
        ToolTip.SetTip(row.ActionButton, hint);
        AutomationProperties.SetHelpText(row.ActionButton, hint);
        AutomationProperties.SetName(row, status.Announcement);
        return action;
    }

    private void Perform(ModelStatus.Action action)
    {
        switch (action)
        {
            case ModelStatus.Action.Install:
            case ModelStatus.Action.Retry:
            case ModelStatus.Action.Repair:
                state.InstallModel();
                break;

            case ModelStatus.Action.Cancel:
                state.CancelModelInstall();
                break;

            case ModelStatus.Action.Delete:
                // Delete is next to regular buttons and used to work on the first click: a
                // miss cost the whole model and a new download. There is nothing to cancel
                // this, so it asks.
                _ = ConfirmDeleteModel();
                break;
        }
    }

    private async Task ConfirmDeleteModel()
    {
        bool confirmed = await Confirm.Ask(
            this,
            "Delete the recognition model?",
            "Dictation stops working until you download the model again — that's another "
            + state.FullModelDownloadMegabytes.ToString(CultureInfo.InvariantCulture)
            + " MB over the network.",
            "Delete Model").ConfigureAwait(true);
        if (confirmed) { state.DeleteModel(); }
    }

    private void OnPermissionAction(object? sender, RoutedEventArgs args) => state.RequestMicrophone();

    private void OnOpenRecordings(object? sender, RoutedEventArgs args) => state.OpenRecordingsFolder();

    // MARK: - Dictation History

    /// <summary>
    /// The limit only: the dictations themselves are listed in the Library (upstream 0.37).
    /// </summary>
    private void RenderHistory()
    {
        IReadOnlyList<HistoryEntry> history = state.History;
        HistoryLimitPicker.SelectedIndex = Array.IndexOf(HistoryLimits, state.HistoryLimit);
        ShowHistoryButton.IsEnabled = history.Count > 0;
        ClearHistoryButton.IsEnabled = history.Count > 0;
    }

    private void OnRevealHistory(object? sender, RoutedEventArgs args) => state.RevealHistoryAudio();

    private void OnClearHistory(object? sender, RoutedEventArgs args) => _ = ConfirmClearHistory();

    private async Task ConfirmClearHistory()
    {
        bool confirmed = await Confirm.Ask(
            this,
            "Delete all dictation history?",
            "The transcripts and their recordings are removed from this PC.",
            "Delete All").ConfigureAwait(true);
        if (!confirmed) { return; }
        state.ClearHistory();
    }

    // MARK: - Dictionary

    private void RenderDictionary()
    {
        ReplacementList.ItemsSource = state.Replacements;
        ReplacementList.IsEnabled = state.IsDictionaryEditable;
        ReplacementList.IsVisible = state.Replacements.Count > 0;
        DictionaryEmpty.IsVisible = state.Replacements.Count == 0;
        ExportButton.IsEnabled = state.Replacements.Count > 0;
        DictionaryProblem.Text = state.DictionaryProblem?.Message ?? string.Empty;
        DictionaryProblem.Foreground = StatusBrushes.Attention;
        DictionaryProblem.IsVisible = state.DictionaryProblem is not null;
        LearnToggle.IsChecked = state.LearnFromEdits;
    }

    private void OnAddReplacement(object? sender, RoutedEventArgs args)
    {
        state.AddReplacement(SpokenBox.Text ?? string.Empty, WrittenBox.Text ?? string.Empty);
        SpokenBox.Text = string.Empty;
        WrittenBox.Text = string.Empty;
        RenderDictionary();
    }

    private void OnRemoveReplacement(object? sender, RoutedEventArgs args)
    {
        if (ReplacementList.SelectedItem is not DictionaryReplacement selected) { return; }
        int index = state.Replacements.ToList().IndexOf(selected);
        if (index < 0) { return; }
        state.RemoveReplacements([index]);
        RenderDictionary();
    }

    /// <summary>
    /// The file pickers are asynchronous here where WPF's are blocking, so both handlers hand off
    /// and return. The dialog is the desktop's own - a portal on a sandboxed session, a window of
    /// Avalonia's making otherwise - and the file is read through the picker's stream rather than
    /// through a path, because under the portal there may not be a path we are allowed to open.
    /// </summary>
    private void OnImportDictionary(object? sender, RoutedEventArgs args) => _ = ImportDictionary();

    private void OnExportDictionary(object? sender, RoutedEventArgs args) => _ = ExportDictionary();

    private async Task ImportDictionary()
    {
        IReadOnlyList<IStorageFile> chosen = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Import dictionary",
                AllowMultiple = false,
                FileTypeFilter = [DictionaryFiles, FilePickerFileTypes.All],
            }).ConfigureAwait(true);
        if (chosen.Count == 0) { return; }

        try
        {
            using var buffer = new MemoryStream();
            await using (Stream file = await chosen[0].OpenReadAsync().ConfigureAwait(true))
            {
                await file.CopyToAsync(buffer).ConfigureAwait(true);
            }

            state.ImportDictionary(buffer.ToArray());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            state.ReportDictionaryFileProblem("The dictionary file couldn't be read", error);
        }

        RenderDictionary();
    }

    private async Task ExportDictionary()
    {
        IStorageFile? chosen = await StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Export dictionary",
                SuggestedFileName = "openramble-dictionary.json",
                DefaultExtension = "json",
                FileTypeChoices = [DictionaryFiles],
            }).ConfigureAwait(true);
        if (chosen is null) { return; }

        try
        {
            await using Stream file = await chosen.OpenWriteAsync().ConfigureAwait(true);
            await file.WriteAsync(state.ExportedDictionary()).ConfigureAwait(true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            state.ReportDictionaryFileProblem("The dictionary couldn't be saved", error);
        }
    }

    private static FilePickerFileType DictionaryFiles => new("OpenRamble dictionary")
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"],
    };

    // MARK: - About

    private void RenderAbout()
    {
        ThemePicker.SelectedIndex = AppAppearances.AllCases.ToList().IndexOf(state.Appearance);
        Changed(RevertTheme, state.Appearance != SettingsDefaults.Appearance);
        LoggingToggle.IsChecked = state.DetailedLogging;
        Changed(RevertLogging, state.DetailedLogging != SettingsDefaults.DetailedLogging);
    }

    private void OnSaveErrorReport(object? sender, RoutedEventArgs args) => _ = SaveErrorReport();

    /// <summary>
    /// One ZIP the person saves and shares themselves: local technical events, versions and any
    /// crash records. Nothing is sent.
    /// </summary>
    private async Task SaveErrorReport()
    {
        IStorageFile? chosen = await StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Save Error Report",
                SuggestedFileName = "OpenRamble-Error-Report.zip",
                DefaultExtension = "zip",
                FileTypeChoices = [new FilePickerFileType("ZIP archive") { Patterns = ["*.zip"], MimeTypes = ["application/zip"] }],
            }).ConfigureAwait(true);
        if (chosen is null) { return; }
        if (chosen.TryGetLocalPath() is not { } path)
        {
            Notice.Show("The report can only be saved to a folder on this PC.");
            return;
        }

        SaveReportButton.IsEnabled = false;
        SaveReportButton.Content = "Saving Report…";
        try
        {
            await state.SaveErrorReport(path).ConfigureAwait(true);
        }
        finally
        {
            SaveReportButton.Content = "Save Error Report…";
            SaveReportButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// The one link in this window. An app whose whole claim is that speech never leaves the
    /// machine should be readable by anyone who doubts it.
    /// </summary>
    private void OnOpenSource(object? sender, RoutedEventArgs args) =>
        DesktopTools.Start("xdg-open", SourceUrl.ToString());
}
