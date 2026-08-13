using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DictationCore;
using LocalASR;
using Microsoft.Win32;
using OpenRamble.AppCore;

namespace OpenRamble.App;

/// <summary>
/// The settings window: the hotkey, the language and model, the dictionary, and what the app does
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
/// The window renders <see cref="AppState"/> and calls back into it. What every string says is
/// decided by <see cref="ModelStatus"/>, <see cref="PermissionStatus"/> and
/// <see cref="HotkeyAdvice"/>, all of which are checked without a screen.
/// </para>
/// </remarks>
public sealed partial class SettingsWindow : Window
{
    private readonly AppState state;
    private bool updating;

    public SettingsWindow(AppState state)
    {
        this.state = state;
        InitializeComponent();

        HotkeyPicker.ItemsSource = DictationHotkeyInfo.AllCases.Select(hotkey => hotkey.Title).ToArray();
        LanguagePicker.ItemsSource = new[] { RecognitionLanguages.Name(null) }
            .Concat(RecognitionLanguages.Options.Select(option => option.Name))
            .ToArray();

        HotkeyPicker.SelectionChanged += OnHotkeyChanged;
        LanguagePicker.SelectionChanged += OnLanguageChanged;
        SoundsToggle.Click += (_, _) => state.SoundsEnabled = SoundsToggle.IsChecked == true;
        LaunchToggle.Click += (_, _) => state.LaunchAtLogin = LaunchToggle.IsChecked == true;
        LearnToggle.Click += (_, _) => state.LearnFromEdits = LearnToggle.IsChecked == true;

        state.PropertyChanged += OnStateChanged;
        Closed += (_, _) => state.PropertyChanged -= OnStateChanged;

        Render();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs args) =>
        Dispatcher.BeginInvoke(Render);

    private void Render()
    {
        updating = true;
        try
        {
            HotkeyPicker.SelectedIndex = DictationHotkeyInfo.AllCases.ToList().IndexOf(state.Hotkey);
            SoundsToggle.IsChecked = state.SoundsEnabled;
            LaunchToggle.IsChecked = state.LaunchAtLogin;
            LearnToggle.IsChecked = state.LearnFromEdits;
            LanguagePicker.SelectedIndex = state.RecognitionLanguage is string code
                ? RecognitionLanguages.Options.ToList().FindIndex(option => option.Code == code) + 1
                : 0;

            HotkeyWarning.Text = state.HotkeyWarning ?? string.Empty;
            HotkeyWarning.Foreground = StatusBrushes.Attention;
            HotkeyWarning.Visibility = state.HotkeyWarning is null ? Visibility.Collapsed : Visibility.Visible;

            RenderPermission();
            RenderModel();
            RenderDictionary();
        }
        finally
        {
            updating = false;
        }
    }

    private void RenderPermission()
    {
        PermissionStatus permission = PermissionStatus.Microphone(
            state.MicrophoneState,
            state.MicrophoneGranted
                ? "OpenRamble can hear you. It listens only while you hold the dictation key."
                : "Dictation cannot start without it. OpenRamble opens the microphone only while you hold the dictation key.");

        PermissionTitle.Text = $"{permission.Title} — {permission.AccessibilityValue}";
        PermissionDetail.Text = permission.Detail;
        PermissionButton.Content = permission.ActionTitle ?? string.Empty;
        PermissionButton.Visibility = permission.ActionTitle is null ? Visibility.Collapsed : Visibility.Visible;
        System.Windows.Automation.AutomationProperties.SetName(
            PermissionButton, permission.ActionAccessibilityLabel ?? string.Empty);
    }

    private void RenderModel()
    {
        ModelStatus status = ModelStatus.Make(
            state.ModelStateValue,
            state.IsPreparingEngine,
            ModelStatus.Place.Settings,
            state.EnginePreparation,
            state.RemainingDownloadMegabytes == 0
                ? ModelStatus.FullDownloadMegabytes
                : state.RemainingDownloadMegabytes);

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
            System.Windows.Automation.AutomationProperties.SetHelpText(button, status.HintFor(action));
            ModelStatus.Action captured = action;
            button.Click += (_, _) => Perform(captured);
            ModelActions.Children.Add(button);
        }
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
                state.DeleteModel();
                break;
        }
    }

    private void OnHotkeyChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (updating || HotkeyPicker.SelectedIndex < 0) { return; }
        state.Hotkey = DictationHotkeyInfo.AllCases[HotkeyPicker.SelectedIndex];
    }

    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (updating || LanguagePicker.SelectedIndex < 0) { return; }
        state.RecognitionLanguage = LanguagePicker.SelectedIndex == 0
            ? null
            : RecognitionLanguages.Options[LanguagePicker.SelectedIndex - 1].Code;
    }

    private void OnPermissionAction(object sender, RoutedEventArgs args) => state.RequestMicrophone();

    private void OnOpenRecordings(object sender, RoutedEventArgs args) => state.OpenRecordingsFolder();

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
            state.ReportDictionaryFileProblem($"The dictionary file couldn't be read: {error.Message}");
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
            state.ReportDictionaryFileProblem($"The dictionary couldn't be saved: {error.Message}");
        }
    }
}
