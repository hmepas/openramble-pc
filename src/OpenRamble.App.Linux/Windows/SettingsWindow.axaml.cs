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
/// The settings window: five panes - General, Recognition, Dictation History, Dictionary,
/// About - the same five the macOS window shows behind its sidebar.
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
    private readonly WavPlayback playback = new();

    /// <summary>
    /// The microphones as they were listed when the window opened or came back to the front.
    /// </summary>
    /// <remarks>
    /// Read then, not on every render: listing them is a subprocess on this platform, and the
    /// window re-renders on every property change, including the once-a-second preparation
    /// tick. Coming back to the window is the moment a person has just plugged something in.
    /// </remarks>
    private IReadOnlyList<AudioInputDevice> devices = [];

    /// <summary>The take being played, so its row can offer Stop rather than Play.</summary>
    private Guid? playingEntry;

    /// <summary>
    /// What the history rows were last built from. Fifty rows with five buttons each are not
    /// rebuilt for a property change that touched none of them.
    /// </summary>
    private IReadOnlyList<HistoryEntry>? renderedHistory;
    private bool renderedHistoryCanRetranscribe;

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
        CopyShortcutRecorder.Keymap = LinuxShortcutKeymap.Instance;
        VersionText.Text = Version();

        HotkeyPicker.SelectionChanged += (_, _) => Pick(HotkeyPicker, DictationHotkeyInfo.AllCases, hotkey => state.Hotkey = hotkey);
        PlacementPicker.SelectionChanged += (_, _) => Pick(PlacementPicker, DictationOverlayPlacements.AllCases, placement => state.OverlayPlacement = placement);
        ThemePicker.SelectionChanged += (_, _) => Pick(ThemePicker, AppAppearances.AllCases, appearance => state.Appearance = appearance);
        HistoryLimitPicker.SelectionChanged += (_, _) => Pick(HistoryLimitPicker, HistoryLimits, limit => state.HistoryLimit = limit);
        DevicePicker.SelectionChanged += OnDeviceChanged;

        CopyShortcutRecorder.ShortcutChanged += shortcut => state.CopyShortcut = shortcut;
        CopyShortcutRecorder.ProblemChanged += problem =>
        {
            CopyShortcutProblem.Text = problem ?? string.Empty;
            CopyShortcutProblem.Foreground = StatusBrushes.Attention;
            CopyShortcutProblem.IsVisible = problem is not null;
        };

        Toggle(LaunchToggle, on => state.LaunchAtLogin = on);
        Toggle(ClipboardToggle, on => state.CopiesToClipboard = on);
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
            playback.Dispose();
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

    private void RenderGeneral()
    {
        HotkeyPicker.SelectedIndex = DictationHotkeyInfo.AllCases.ToList().IndexOf(state.Hotkey);
        Changed(RevertHotkey, state.Hotkey != SettingsDefaults.Hotkey);
        HotkeyWarning.Text = state.HotkeyWarning ?? string.Empty;
        HotkeyWarning.Foreground = StatusBrushes.Attention;
        HotkeyWarning.IsVisible = state.HotkeyWarning is not null;

        CopyShortcutRecorder.Shortcut = state.CopyShortcut;
        Changed(RevertCopyShortcut, state.CopyShortcut != SettingsDefaults.CopyShortcut);

        LaunchToggle.IsChecked = state.LaunchAtLogin;
        Changed(RevertLaunch, state.LaunchAtLogin != SettingsDefaults.LaunchAtLogin);
        ClipboardToggle.IsChecked = state.CopiesToClipboard;
        Changed(RevertClipboard, state.CopiesToClipboard != SettingsDefaults.CopiesToClipboard);
        TrailingSpaceToggle.IsChecked = state.AppendsTrailingSpace;
        Changed(RevertTrailingSpace, state.AppendsTrailingSpace != SettingsDefaults.AppendsTrailingSpace);
        SoundsToggle.IsChecked = state.SoundsEnabled;
        Changed(RevertSounds, state.SoundsEnabled != SettingsDefaults.SoundsEnabled);
        SilenceToggle.IsChecked = state.StopsOnSilence;
        Changed(RevertSilence, state.StopsOnSilence != SettingsDefaults.StopsOnSilence);

        RenderDevices();

        PlacementPicker.SelectedIndex = DictationOverlayPlacements.AllCases.ToList().IndexOf(state.OverlayPlacement);
        Changed(RevertPlacement, state.OverlayPlacement != SettingsDefaults.OverlayPlacement);
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

    private void OnRevertCopyShortcut(object? sender, RoutedEventArgs args) => state.CopyShortcut = SettingsDefaults.CopyShortcut;

    private void OnRevertLaunch(object? sender, RoutedEventArgs args) => state.LaunchAtLogin = SettingsDefaults.LaunchAtLogin;

    private void OnRevertClipboard(object? sender, RoutedEventArgs args) => state.CopiesToClipboard = SettingsDefaults.CopiesToClipboard;

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

        PermissionTitle.Text = $"{permission.Title} — {permission.AccessibilityValue}";
        PermissionDetail.Text = permission.Detail;
        PermissionButton.Content = permission.ActionTitle ?? string.Empty;
        PermissionButton.IsVisible = permission.ActionTitle is not null;
        AutomationProperties.SetName(PermissionButton, permission.ActionAccessibilityLabel ?? string.Empty);
    }

    // MARK: - Recognition

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
        RenderCard(status, ModelTitle, ModelDetail, ModelProgress, ModelProgressLabel, ModelActions);

        // The newer model is offered beside the one that keeps dictating, never instead of it.
        UpgradeCard.IsVisible = state.UpgradeState is not null;
        if (state.UpgradeState is { } upgrade)
        {
            RenderCard(
                ModelStatus.Upgrade(upgrade, state.RemainingDownloadMegabytes),
                UpgradeTitle,
                UpgradeDetail,
                UpgradeProgress,
                UpgradeProgressLabel,
                UpgradeActions);
        }
    }

    private void RenderCard(
        ModelStatus status,
        TextBlock title,
        TextBlock detail,
        ProgressBar progress,
        TextBlock progressLabel,
        StackPanel actions)
    {
        title.Text = status.Title;
        detail.Text = status.Detail ?? string.Empty;
        detail.IsVisible = status.Detail is not null;

        progress.IsVisible = status.Progress is not null;
        progress.Value = status.Progress ?? 0;
        progressLabel.Text = status.ProgressLabel ?? string.Empty;
        progressLabel.IsVisible = status.ProgressLabel is not null;

        actions.Children.Clear();
        foreach (ModelStatus.Action action in status.Actions)
        {
            var button = new Button
            {
                Content = status.TitleFor(action),
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 8, 0),
            };
            ToolTip.SetTip(button, status.HintFor(action));
            AutomationProperties.SetHelpText(button, status.HintFor(action));
            ModelStatus.Action captured = action;
            button.Click += (_, _) => Perform(captured);
            actions.Children.Add(button);
        }
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
    /// Rows built by hand rather than through a template: each carries five buttons bound to
    /// one entry, and a template with five commands is more plumbing than fifty rows.
    /// </summary>
    private void RenderHistory(bool force = false)
    {
        IReadOnlyList<HistoryEntry> history = state.History;
        HistoryLimitPicker.SelectedIndex = Array.IndexOf(HistoryLimits, state.HistoryLimit);
        ShowHistoryButton.IsEnabled = history.Count > 0;
        ClearHistoryButton.IsEnabled = history.Count > 0;
        HistoryEmpty.IsVisible = history.Count == 0;
        HistoryScroller.IsVisible = history.Count > 0;

        bool canRetranscribe = !state.IsRetranscribing && state.IsEngineReady;
        if (!force && ReferenceEquals(history, renderedHistory) && canRetranscribe == renderedHistoryCanRetranscribe)
        {
            return;
        }

        renderedHistory = history;
        renderedHistoryCanRetranscribe = canRetranscribe;

        if (playingEntry is { } playing && (!playback.IsPlaying || history.All(entry => entry.Id != playing)))
        {
            playingEntry = null;
        }

        HistoryList.Children.Clear();
        foreach (HistoryEntry entry in history)
        {
            HistoryList.Children.Add(HistoryRow(entry));
            HistoryList.Children.Add(new Separator());
        }
    }

    private Control HistoryRow(HistoryEntry entry)
    {
        string? audio = state.HistoryAudioPath(entry);
        bool isPlaying = playingEntry == entry.Id && playback.IsPlaying;

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto") };
        header.Children.Add(new TextBlock
        {
            Text = entry.Date.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
        });

        Button keep = RowButton(
            entry.IsKept ? "★" : "☆",
            entry.IsKept ? "Kept. Stop keeping" : "Keep this dictation",
            () => state.SetHistoryEntryKept(!entry.IsKept, entry));
        keep.Foreground = entry.IsKept ? StatusBrushes.Attention : Brushes.Gray;
        Grid.SetColumn(keep, 1);
        header.Children.Add(keep);

        if (audio is not null)
        {
            // Worth having after editing the dictionary: the words the model heard do not
            // change, but what they become does.
            Button again = RowButton("↻", "Recognise this recording again", () => state.RetranscribeHistoryEntry(entry));
            again.IsEnabled = !state.IsRetranscribing && state.IsEngineReady;
            Grid.SetColumn(again, 2);
            header.Children.Add(again);
        }

        // A take with no words has nothing to copy: the button is hidden rather than removed, so
        // the star, the re-run and Delete stay in their columns on every row.
        bool hasWords = entry.Text.Length > 0;
        Button copy = RowButton("Copy", "Copy this dictation", () => state.CopyHistoryEntry(entry));
        if (!hasWords)
        {
            copy.Opacity = 0;
            copy.IsEnabled = false;
            copy.IsHitTestVisible = false;
            copy.Focusable = false;
        }

        Grid.SetColumn(copy, 3);
        header.Children.Add(copy);

        Button delete = RowButton("Delete", "Delete this dictation", () =>
        {
            if (playingEntry == entry.Id) { StopPlayback(); }
            state.DeleteHistoryEntry(entry);
        });
        Grid.SetColumn(delete, 4);
        header.Children.Add(delete);

        var row = new StackPanel { Margin = new Thickness(4, 8) };
        row.Children.Add(header);
        row.Children.Add(hasWords
            ? new SelectableTextBlock
            {
                Text = entry.Text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 6),
            }
            : new TextBlock
            {
                // Kept for its audio: play it, or recognise it again.
                Text = "Nothing recognized",
                Foreground = Brushes.Gray,
                FontStyle = FontStyle.Italic,
                Margin = new Thickness(0, 6, 0, 6),
            });

        if (audio is not null)
        {
            Button play = RowButton(
                isPlaying ? "■ Stop" : "▶ Play",
                isPlaying ? "Stop playback" : "Play this recording",
                () => TogglePlayback(entry, audio));
            play.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(play);
        }
        else
        {
            // Said rather than hidden: a row without a Play button and no explanation reads
            // as a bug.
            row.Children.Add(new TextBlock
            {
                Text = "Recording no longer on disk",
                Foreground = Brushes.Gray,
                FontSize = 12,
            });
        }

        return row;
    }

    private static Button RowButton(string content, string accessibilityLabel, Action action)
    {
        var button = new Button
        {
            Content = content,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(6, 0, 0, 0),
        };
        ToolTip.SetTip(button, accessibilityLabel);
        AutomationProperties.SetName(button, accessibilityLabel);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>One take playing at a time: a second Play stops the first rather than layering two.</summary>
    private void TogglePlayback(HistoryEntry entry, string audio)
    {
        if (playingEntry == entry.Id && playback.IsPlaying)
        {
            StopPlayback();
            return;
        }

        playingEntry = playback.Play(audio) ? entry.Id : null;
        RenderHistory(force: true);
    }

    private void StopPlayback()
    {
        playback.Stop();
        playingEntry = null;
        RenderHistory(force: true);
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
        StopPlayback();
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
