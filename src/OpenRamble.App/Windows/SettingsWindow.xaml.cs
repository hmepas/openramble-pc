using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Navigation;
using DictationCore;
using Microsoft.Win32;
using OpenRamble.AppCore;
using OpenRamble.AppCore.Audio;

namespace OpenRamble.App;

/// <summary>
/// The settings window: the keys, the model, the history, the dictionary, and what the app does
/// with a person's voice.
/// </summary>
/// <remarks>
/// <para>
/// Plain WPF, no theme package. Every control here is the shell's own, which means it already
/// follows the person's high-contrast theme, text scaling and screen reader, and none of that has
/// to be re-implemented by a styling layer for a window most people open twice.
/// </para>
/// <para>
/// <b>There is no Updates section, and that is a decision, not an omission.</b> This port ships no
/// updater; inventing a settings page for a mechanism that does not exist would promise the person
/// that the app checks for new versions.
/// </para>
/// <para>
/// Every settings row carries the small arrow that puts that one setting back the way it came -
/// per setting rather than one "reset everything" button, because those are different promises.
/// The arrow is only live where the value differs from <see cref="SettingsDefaults"/>, which also
/// answers "what did I change in here?".
/// </para>
/// <para>
/// The window renders <see cref="AppState"/> and calls back into it. What every string says is
/// decided by <see cref="ModelStatus"/>, <see cref="PermissionStatus"/> and
/// <see cref="HotkeyAdvice"/>, all of which are checked without a screen.
/// </para>
/// </remarks>
public sealed partial class SettingsWindow : Window
{
    private static readonly int[] HistoryLimitChoices = [5, 10, 20, 50];

    private readonly AppState state;
    private readonly IShortcutKeymap keymap = LiveWindowsShortcutKeymap.Instance;
    private readonly WavPlayback playback = new();
    private Guid? playing;
    private bool updating;

    // General - built in code so each row shares one layout; see Row().
    private readonly ComboBox hotkeyPicker = new() { Width = 220 };
    private readonly Button hotkeyRevert;
    private readonly ShortcutRecorder copyRecorder;
    private readonly Button copyRevert;
    private readonly CheckBox launchToggle = new();
    private readonly Button launchRevert;
    private readonly CheckBox spaceToggle = new();
    private readonly Button spaceRevert;
    private readonly CheckBox soundsToggle = new();
    private readonly Button soundsRevert;
    private readonly ComboBox devicePicker = new() { Width = 260 };
    private readonly Button deviceRevert;
    private readonly CheckBox silenceToggle = new();
    private readonly Button silenceRevert;
    private readonly ComboBox placementPicker = new() { Width = 140 };
    private readonly Button placementRevert;

    // About
    private readonly ComboBox themePicker = new() { Width = 140 };
    private readonly Button themeRevert;
    private readonly CheckBox logsToggle = new();
    private readonly Button logsRevert;

    /// <summary>The microphones offered by the picker, in the order it shows them; index 0 is the default.</summary>
    private IReadOnlyList<AudioInputDevice> devices = [];

    public SettingsWindow(AppState state)
    {
        this.state = state;
        InitializeComponent();

        VersionText.Text = $"Version {typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "—"}";

        // MARK: - Shortcut

        hotkeyPicker.ItemsSource = DictationHotkeyInfo.AllCases.Select(hotkey => hotkey.Title).ToArray();
        hotkeyPicker.SelectionChanged += OnHotkeyChanged;
        ShortcutRows.Children.Add(Row(
            "Dictation key",
            hotkeyPicker,
            "The key you hold down while dictating",
            out hotkeyRevert,
            () => state.Hotkey = SettingsDefaults.Hotkey));

        copyRecorder = new ShortcutRecorder(keymap);
        copyRecorder.ShortcutChanged += shortcut => state.LastTakeShortcut = shortcut;
        copyRecorder.Changed += RenderCopyProblem;
        ShortcutRows.Children.Add(Row(
            "Paste last take",
            copyRecorder,
            "Press this shortcut to paste your newest dictation into the app in front",
            out copyRevert,
            () => state.LastTakeShortcut = state.DefaultLastTakeShortcut));

        // MARK: - Behavior

        launchToggle.Click += (_, _) => state.LaunchAtLogin = launchToggle.IsChecked == true;
        BehaviorRows.Children.Add(Row(
            "Launch at login",
            launchToggle,
            "Starts OpenRamble automatically when you sign in",
            out launchRevert,
            () => state.LaunchAtLogin = SettingsDefaults.LaunchAtLogin));

        spaceToggle.Click += (_, _) => state.AppendsTrailingSpace = spaceToggle.IsChecked == true;
        BehaviorRows.Children.Add(Row(
            "Add a space after each dictation",
            spaceToggle,
            "For dictating in runs, so the next phrase does not arrive welded to the last word.",
            out spaceRevert,
            () => state.AppendsTrailingSpace = SettingsDefaults.AppendsTrailingSpace));

        soundsToggle.Click += (_, _) => state.SoundsEnabled = soundsToggle.IsChecked == true;
        BehaviorRows.Children.Add(Row(
            "Play a sound when something needs you",
            soundsToggle,
            "A quiet tone when the text didn't reach the field or nothing was recognized. A dictation that works stays silent.",
            out soundsRevert,
            () => state.SoundsEnabled = SettingsDefaults.SoundsEnabled));

        devicePicker.SelectionChanged += OnDeviceChanged;
        BehaviorRows.Children.Add(Row(
            "Microphone",
            devicePicker,
            "Which input to record through. The system default follows whatever Windows is using.",
            out deviceRevert,
            () => state.InputDeviceUid = SettingsDefaults.InputDeviceUid));

        silenceToggle.Click += (_, _) => state.StopsOnSilence = silenceToggle.IsChecked == true;
        BehaviorRows.Children.Add(Row(
            "Finish hands-free dictation on silence",
            silenceToggle,
            "Only in hands-free mode. While you hold the key, a pause is never treated as the end.",
            out silenceRevert,
            () => state.StopsOnSilence = SettingsDefaults.StopsOnSilence));

        placementPicker.ItemsSource = DictationOverlayPlacements.AllCases.Select(placement => placement.Title).ToArray();
        placementPicker.SelectionChanged += (_, _) =>
        {
            if (updating || placementPicker.SelectedIndex < 0) { return; }
            state.OverlayPlacement = DictationOverlayPlacements.AllCases[placementPicker.SelectedIndex];
        };
        BehaviorRows.Children.Add(Row(
            "Dictation panel",
            placementPicker,
            "Places dictation feedback at the top or bottom of the active screen",
            out placementRevert,
            () => state.OverlayPlacement = SettingsDefaults.OverlayPlacement));

        // MARK: - History

        HistoryLimitPicker.SelectionChanged += (_, _) =>
        {
            if (updating || HistoryLimitPicker.SelectedItem is not int limit) { return; }
            state.HistoryLimit = limit;
        };

        // MARK: - Dictionary

        LearnToggle.Click += (_, _) => state.LearnFromEdits = LearnToggle.IsChecked == true;

        // MARK: - About

        themePicker.ItemsSource = AppAppearances.AllCases.Select(appearance => appearance.Title).ToArray();
        themePicker.SelectionChanged += (_, _) =>
        {
            if (updating || themePicker.SelectedIndex < 0) { return; }
            state.Appearance = AppAppearances.AllCases[themePicker.SelectedIndex];
        };
        AppearanceRows.Children.Add(Row(
            "Theme",
            themePicker,
            "Follow the system, or keep this app light or dark on its own",
            out themeRevert,
            () => state.Appearance = SettingsDefaults.Appearance));

        logsToggle.Click += (_, _) => state.DetailedLogging = logsToggle.IsChecked == true;
        PrivacyRows.Children.Add(Row(
            "Keep local diagnostics",
            logsToggle,
            "Keeps technical events for up to 7 days, limited to 5 MB. No speech or personal content. Turning this off clears the journal.",
            out logsRevert,
            () => state.DetailedLogging = SettingsDefaults.DetailedLogging));

        state.PropertyChanged += OnStateChanged;
        Closed += (_, _) =>
        {
            state.PropertyChanged -= OnStateChanged;
            playback.Dispose();
        };

        Render();
    }

    /// <summary>
    /// One settings row: a name, its control, and the arrow that undoes it.
    /// </summary>
    /// <remarks>
    /// The hint goes to the tooltip and the accessibility help text, as the original puts it in
    /// the accessibility hint: a screen reader reads it on the control, a pointer finds it on
    /// hover, and the row itself stays one line.
    /// </remarks>
    private static Grid Row(string title, FrameworkElement control, string hint, out Button revert, Action reset)
    {
        var row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        row.Children.Add(label);

        control.VerticalAlignment = VerticalAlignment.Center;
        control.HorizontalAlignment = HorizontalAlignment.Left;
        control.ToolTip = hint;
        AutomationProperties.SetName(control, title);
        AutomationProperties.SetHelpText(control, hint);
        Grid.SetColumn(control, 1);
        row.Children.Add(control);

        var arrow = new Button { Style = (Style)Application.Current.Resources["RevertButton"] };
        arrow.Click += (_, _) => reset();
        Grid.SetColumn(arrow, 2);
        row.Children.Add(arrow);
        revert = arrow;

        return row;
    }

    /// <summary>
    /// Kept in the layout rather than removed when inactive: a control that appears and
    /// disappears makes every row jump as settings change.
    /// </summary>
    private static void ShowRevert(Button revert, bool changed)
    {
        revert.Visibility = changed ? Visibility.Visible : Visibility.Hidden;
        revert.IsEnabled = changed;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs args) =>
        Dispatcher.BeginInvoke(Render);

    private void Render()
    {
        updating = true;
        try
        {
            hotkeyPicker.SelectedIndex = DictationHotkeyInfo.AllCases.ToList().IndexOf(state.Hotkey);
            ShowRevert(hotkeyRevert, state.Hotkey != SettingsDefaults.Hotkey);
            HotkeyWarning.Text = state.HotkeyWarning ?? string.Empty;
            HotkeyWarning.Foreground = StatusBrushes.Attention;
            HotkeyWarning.Visibility = state.HotkeyWarning is null ? Visibility.Collapsed : Visibility.Visible;

            if (copyRecorder.Shortcut != state.LastTakeShortcut) { copyRecorder.Shortcut = state.LastTakeShortcut; }
            ShowRevert(copyRevert, state.LastTakeShortcut != state.DefaultLastTakeShortcut);

            launchToggle.IsChecked = state.LaunchAtLogin;
            ShowRevert(launchRevert, state.LaunchAtLogin != SettingsDefaults.LaunchAtLogin);
            spaceToggle.IsChecked = state.AppendsTrailingSpace;
            ShowRevert(spaceRevert, state.AppendsTrailingSpace != SettingsDefaults.AppendsTrailingSpace);
            soundsToggle.IsChecked = state.SoundsEnabled;
            ShowRevert(soundsRevert, state.SoundsEnabled != SettingsDefaults.SoundsEnabled);
            silenceToggle.IsChecked = state.StopsOnSilence;
            ShowRevert(silenceRevert, state.StopsOnSilence != SettingsDefaults.StopsOnSilence);

            RenderDevices();

            placementPicker.SelectedIndex = DictationOverlayPlacements.AllCases.ToList().IndexOf(state.OverlayPlacement);
            ShowRevert(placementRevert, state.OverlayPlacement != SettingsDefaults.OverlayPlacement);

            LearnToggle.IsChecked = state.LearnFromEdits;

            themePicker.SelectedIndex = AppAppearances.AllCases.ToList().IndexOf(state.Appearance);
            ShowRevert(themeRevert, state.Appearance != SettingsDefaults.Appearance);
            logsToggle.IsChecked = state.DetailedLogging;
            ShowRevert(logsRevert, state.DetailedLogging != SettingsDefaults.DetailedLogging);

            RenderPermission();
            RenderModel();
            RenderHistory();
            RenderDictionary();
        }
        finally
        {
            updating = false;
        }
    }

    private void RenderCopyProblem()
    {
        string? problem = copyRecorder.Problem;
        LastTakeShortcutProblem.Text = problem ?? string.Empty;
        LastTakeShortcutProblem.Foreground = StatusBrushes.Attention;
        LastTakeShortcutProblem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// The system default first, then every microphone the machine can hear through. A chosen
    /// device that has gone away is still listed - by its UID, as the only name left - so the
    /// picker shows what is set, and the notice under it says why dictation is not using it.
    /// </summary>
    private void RenderDevices()
    {
        IReadOnlyList<AudioInputDevice> available = state.AvailableInputDevices;
        string? chosen = state.InputDeviceUid;
        var listed = new List<AudioInputDevice>(available);
        if (chosen is not null && !available.Any(device => device.Uid == chosen))
        {
            listed.Add(new AudioInputDevice(chosen, "Not connected"));
        }

        devices = listed;
        var names = new List<string> { "System default" };
        names.AddRange(listed.Select(device => device.Name));
        devicePicker.ItemsSource = names;
        devicePicker.SelectedIndex = chosen is null ? 0 : 1 + listed.FindIndex(device => device.Uid == chosen);
        ShowRevert(deviceRevert, chosen != SettingsDefaults.InputDeviceUid);

        // Said out loud rather than swapped silently: someone who chose a headset and quietly
        // got the laptop lid would only find out from the transcript.
        string? notice = state.InputDeviceNotice;
        InputDeviceNotice.Text = notice ?? string.Empty;
        InputDeviceNotice.Foreground = StatusBrushes.Attention;
        InputDeviceNotice.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnDeviceChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (updating || devicePicker.SelectedIndex < 0) { return; }
        int index = devicePicker.SelectedIndex;
        state.InputDeviceUid = index == 0 ? null : devices[index - 1].Uid;
    }

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
        PermissionButton.Visibility = permission.ActionTitle is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(PermissionButton, permission.ActionAccessibilityLabel ?? string.Empty);
    }

    private void RenderModel()
    {
        // The card reads three facts and never guesses: are the files usable, is preparation
        // actually running, and how far along it is - "Step N of 3" while it runs.
        ModelStatus status = ModelStatus.Make(
            state.ModelStateValue,
            ModelStatus.Place.Settings,
            state.EnginePreparation,
            state.RemainingDownloadMegabytes == 0
                ? state.FullModelDownloadMegabytes
                : state.RemainingDownloadMegabytes,
            state.IsEngineReady,
            state.IsPreparingEngine);

        ModelTitle.Text = status.Title;
        ModelDetail.Text = status.Detail ?? string.Empty;
        ModelDetail.Visibility = status.Detail is null ? Visibility.Collapsed : Visibility.Visible;

        ModelProgress.Visibility = status.Progress is null ? Visibility.Collapsed : Visibility.Visible;
        ModelProgress.Value = status.Progress ?? 0;
        ModelProgressLabel.Text = status.ProgressLabel ?? string.Empty;
        ModelProgressLabel.Visibility = status.ProgressLabel is null ? Visibility.Collapsed : Visibility.Visible;

        ModelActions.Children.Clear();
        foreach (ModelStatus.Action action in status.Actions)
        {
            var button = new Button
            {
                Content = status.TitleFor(action),
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = status.HintFor(action),
            };
            AutomationProperties.SetHelpText(button, status.HintFor(action));
            ModelStatus.Action captured = action;
            button.Click += (_, _) => Perform(captured);
            ModelActions.Children.Add(button);
        }
    }

    private void RenderHistory()
    {
        IReadOnlyList<HistoryEntry> history = state.History;

        var limits = new List<int>(HistoryLimitChoices);
        if (!limits.Contains(state.HistoryLimit))
        {
            limits.Add(state.HistoryLimit);
            limits.Sort();
        }

        HistoryLimitPicker.ItemsSource = limits;
        HistoryLimitPicker.SelectedItem = state.HistoryLimit;

        HistoryEmpty.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowRecordingsButton.IsEnabled = history.Count > 0;
        DeleteAllButton.IsEnabled = history.Count > 0;

        if (playing is { } id && !history.Any(entry => entry.Id == id)) { StopPlayback(); }

        HistoryList.Children.Clear();
        foreach (HistoryEntry entry in history)
        {
            HistoryList.Children.Add(HistoryRow(entry));
        }
    }

    /// <summary>
    /// One kept dictation: when, the text, and what can be done with it. The star means "keep":
    /// a starred take does not count against the limit and is not pushed out by newer ones.
    /// </summary>
    private FrameworkElement HistoryRow(HistoryEntry entry)
    {
        string? audio = state.HistoryAudioPath(entry);
        bool isPlaying = playing == entry.Id;

        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };

        var header = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right);

        var star = RowButton(
            entry.IsKept ? "★" : "☆",
            entry.IsKept ? "Stop keeping this one" : "Keep this one",
            entry.IsKept ? "Kept. Stop keeping" : "Keep this dictation",
            () => state.SetHistoryEntryKept(!entry.IsKept, entry));
        if (entry.IsKept) { star.Foreground = StatusBrushes.Attention; }
        actions.Children.Add(star);

        if (audio is not null)
        {
            var again = RowButton(
                "Recognize again",
                "Recognize this recording again",
                "Recognize this recording again",
                () => state.RetranscribeHistoryEntry(entry));
            again.IsEnabled = !state.IsRetranscribing && state.IsEngineReady;
            actions.Children.Add(again);
        }

        // A take with no words has nothing to copy: the button is hidden rather than collapsed, so
        // Delete stays in its place on every row.
        bool hasWords = entry.Text.Length > 0;
        var copy = RowButton("Copy", "Copy", "Copy this dictation", () => state.CopyHistoryEntry(entry));
        if (!hasWords) { copy.Visibility = Visibility.Hidden; }
        actions.Children.Add(copy);
        actions.Children.Add(RowButton("Delete", "Delete", "Delete this dictation", () =>
        {
            if (playing == entry.Id) { StopPlayback(); }
            state.DeleteHistoryEntry(entry);
        }));
        header.Children.Add(actions);

        header.Children.Add(new TextBlock
        {
            Text = entry.Date.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture),
            Style = (Style)Application.Current.Resources["Secondary"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(header);

        panel.Children.Add(hasWords
            ? new TextBox
            {
                Text = entry.Text,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["WindowForegroundBrush"],
                Margin = new Thickness(0, 4, 0, 4),
            }
            : new TextBlock
            {
                // Kept for its audio: play it, or recognize it again.
                Text = "Nothing recognized",
                Style = (Style)Application.Current.Resources["Secondary"],
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 4, 0, 4),
            });

        if (audio is not null)
        {
            var play = RowButton(
                isPlaying ? "■ Stop" : "▶ Play",
                isPlaying ? "Stop playback" : "Play this recording",
                isPlaying ? "Stop playback" : "Play this recording",
                () => TogglePlayback(entry, audio));
            play.Margin = new Thickness(0);
            panel.Children.Add(play);
        }
        else
        {
            // Said rather than hidden: a row without a Play button and no explanation reads as a bug.
            panel.Children.Add(new TextBlock
            {
                Text = "Recording no longer on disk",
                Style = (Style)Application.Current.Resources["Secondary"],
                FontSize = 11.5,
            });
        }

        return new Border
        {
            BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["PanelBorderBrush"],
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = panel,
        };
    }

    private static Button RowButton(string content, string tooltip, string accessibleName, Action action)
    {
        var button = new Button
        {
            Content = content,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = tooltip,
        };
        AutomationProperties.SetName(button, accessibleName);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>One take playing at a time: a second Play stops the first rather than layering two.</summary>
    private void TogglePlayback(HistoryEntry entry, string audio)
    {
        if (playing == entry.Id)
        {
            StopPlayback();
            return;
        }

        try
        {
            playback.Play(audio);
            playing = entry.Id;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            playing = null;
            DiagnosticLog.EmitException("history.play", error);
        }

        RenderHistory();
    }

    private void StopPlayback()
    {
        playback.Stop();
        playing = null;
    }

    private void RenderDictionary()
    {
        ReplacementList.ItemsSource = state.Replacements;
        ReplacementList.IsEnabled = state.IsDictionaryEditable;
        DictionaryProblem.Text = state.DictionaryProblem?.Message ?? string.Empty;
        DictionaryProblem.Foreground = StatusBrushes.Attention;
        DictionaryProblem.Visibility = state.DictionaryProblem is null ? Visibility.Collapsed : Visibility.Visible;
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
                // Delete sits next to ordinary buttons and used to work on the first click: a
                // miss cost the whole model and a new download. Ask, and name what it will cost.
                MessageBoxResult answer = MessageBox.Show(
                    this,
                    $"Dictation stops working until you download the model again — that's another "
                    + $"{state.FullModelDownloadMegabytes} MB over the network.",
                    "Delete the recognition model?",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning);
                if (answer == MessageBoxResult.OK) { state.DeleteModel(); }
                break;
        }
    }

    private void OnHotkeyChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (updating || hotkeyPicker.SelectedIndex < 0) { return; }
        state.Hotkey = DictationHotkeyInfo.AllCases[hotkeyPicker.SelectedIndex];
    }

    private void OnPermissionAction(object sender, RoutedEventArgs args) => state.RequestMicrophone();

    private void OnOpenRecordings(object sender, RoutedEventArgs args) => state.OpenRecordingsFolder();

    private async void OnSaveErrorReport(object sender, RoutedEventArgs args)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save Error Report",
            FileName = "OpenRamble-Error-Report.zip",
            Filter = "ZIP archive (*.zip)|*.zip",
        };
        if (dialog.ShowDialog(this) != true) { return; }

        SaveReportButton.IsEnabled = false;
        SaveReportButton.Content = "Saving Report…";
        try
        {
            await state.SaveErrorReport(dialog.FileName);
        }
        finally
        {
            SaveReportButton.Content = "Save Error Report…";
            SaveReportButton.IsEnabled = true;
        }
    }

    private void OnRevealHistoryAudio(object sender, RoutedEventArgs args) => state.RevealHistoryAudio();

    private void OnClearHistory(object sender, RoutedEventArgs args)
    {
        MessageBoxResult answer = MessageBox.Show(
            this,
            "The transcripts and their recordings are removed from this PC.",
            "Delete all dictation history?",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) { return; }
        StopPlayback();
        state.ClearHistory();
    }

    private void OnOpenLink(object sender, RequestNavigateEventArgs args)
    {
        // A link is the one thing here that leaves the machine, and it does so in the person's
        // own browser: the app itself never opens a connection for it.
        try
        {
            Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            DiagnosticLog.EmitException("about.link", error);
        }

        args.Handled = true;
    }

    private void OnAddReplacement(object sender, RoutedEventArgs args)
    {
        state.AddReplacement(SpokenBox.Text, WrittenBox.Text);
        SpokenBox.Clear();
        WrittenBox.Clear();
        RenderDictionary();
    }

    private void OnRemoveReplacement(object sender, RoutedEventArgs args)
    {
        if (ReplacementList.SelectedItem is not DictionaryReplacement selected) { return; }
        int index = state.Replacements.ToList().IndexOf(selected);
        if (index < 0) { return; }
        state.RemoveReplacements([index]);
        RenderDictionary();
    }

    private void OnImportDictionary(object sender, RoutedEventArgs args)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import dictionary",
            Filter = "OpenRamble dictionary (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) { return; }

        try
        {
            state.ImportDictionary(File.ReadAllBytes(dialog.FileName));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            state.ReportDictionaryFileProblem("The dictionary file couldn't be read", error);
        }

        RenderDictionary();
    }

    private void OnExportDictionary(object sender, RoutedEventArgs args)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export dictionary",
            FileName = "openramble-dictionary.json",
            Filter = "OpenRamble dictionary (*.json)|*.json",
        };
        if (dialog.ShowDialog(this) != true) { return; }

        try
        {
            File.WriteAllBytes(dialog.FileName, state.ExportedDictionary());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            state.ReportDictionaryFileProblem("The dictionary couldn't be saved", error);
        }
    }
}
