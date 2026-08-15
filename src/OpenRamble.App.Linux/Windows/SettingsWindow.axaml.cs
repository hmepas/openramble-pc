using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DictationCore;
using OpenRamble.AppCore;

namespace OpenRamble.App.Linux;

/// <summary>
/// The settings window: the hotkey, the language and model, the dictionary, and what the app does
/// with a person's voice.
/// </summary>
/// <remarks>
/// <para>
/// Plain Avalonia, no styling layer beyond the Fluent theme the application sets. The Windows
/// window takes every control from the shell so that high contrast, text scaling and the screen
/// reader come for free; nothing on this platform hands out controls, so what is left is to keep
/// the markup ordinary and the automation names on it.
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

    /// <summary>
    /// Guards the controls against reacting to what the render just set.
    /// </summary>
    /// <remarks>
    /// It has to cover the check boxes too, which is the one behavioural difference from the WPF
    /// window. There <c>Click</c> fires for a person and never for code; here the only event a
    /// toggle raises is <c>IsCheckedChanged</c>, which cannot tell the two apart - and without the
    /// guard every render would write all three settings back to disk.
    /// </remarks>
    private bool updating;

    public SettingsWindow(AppState state)
    {
        this.state = state;
        InitializeComponent();
        Icon = TrayIconArt.WindowMark();

        HotkeyPicker.ItemsSource = DictationHotkeyInfo.AllCases.Select(hotkey => hotkey.Title).ToArray();
        LanguagePicker.ItemsSource = new[] { RecognitionLanguages.Name(null) }
            .Concat(RecognitionLanguages.Options.Select(option => option.Name))
            .ToArray();

        HotkeyPicker.SelectionChanged += OnHotkeyChanged;
        LanguagePicker.SelectionChanged += OnLanguageChanged;
        SoundsToggle.IsCheckedChanged += (_, _) =>
        {
            if (!updating) { state.SoundsEnabled = SoundsToggle.IsChecked == true; }
        };
        LaunchToggle.IsCheckedChanged += (_, _) =>
        {
            if (!updating) { state.LaunchAtLogin = LaunchToggle.IsChecked == true; }
        };
        LearnToggle.IsCheckedChanged += (_, _) =>
        {
            if (!updating) { state.LearnFromEdits = LearnToggle.IsChecked == true; }
        };

        state.PropertyChanged += OnStateChanged;
        Closed += (_, _) => state.PropertyChanged -= OnStateChanged;

        Render();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs args) =>
        Dispatcher.UIThread.Post(Render);

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
            HotkeyWarning.IsVisible = state.HotkeyWarning is not null;

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
                : "Dictation cannot start without it. OpenRamble opens the microphone only while you hold the dictation key.",
            state.MicrophoneRemedy);

        PermissionTitle.Text = $"{permission.Title} — {permission.AccessibilityValue}";
        PermissionDetail.Text = permission.Detail;
        PermissionButton.Content = permission.ActionTitle ?? string.Empty;
        PermissionButton.IsVisible = permission.ActionTitle is not null;
        AutomationProperties.SetName(PermissionButton, permission.ActionAccessibilityLabel ?? string.Empty);
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
        ModelDetail.IsVisible = status.Detail is not null;

        ModelProgress.IsVisible = status.Progress is not null;
        ModelProgress.Value = status.Progress ?? 0;
        ModelProgressLabel.Text = status.ProgressLabel ?? string.Empty;
        ModelProgressLabel.IsVisible = status.ProgressLabel is not null;

        ModelActions.Children.Clear();
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
            ModelActions.Children.Add(button);
        }
    }

    private void RenderDictionary()
    {
        ReplacementList.ItemsSource = state.Replacements;
        ReplacementList.IsEnabled = state.IsDictionaryEditable;
        DictionaryProblem.Text = state.DictionaryProblem?.Message ?? string.Empty;
        DictionaryProblem.Foreground = StatusBrushes.Attention;
        DictionaryProblem.IsVisible = state.DictionaryProblem is not null;
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

    private void OnPermissionAction(object? sender, RoutedEventArgs args) => state.RequestMicrophone();

    private void OnOpenRecordings(object? sender, RoutedEventArgs args) => state.OpenRecordingsFolder();

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
            state.ReportDictionaryFileProblem($"The dictionary file couldn't be read: {error.Message}");
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
            state.ReportDictionaryFileProblem($"The dictionary couldn't be saved: {error.Message}");
        }
    }

    private static FilePickerFileType DictionaryFiles => new("OpenRamble dictionary")
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"],
    };
}
