using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DictationCore;
using LocalASR;
using OpenRamble.AppCore;

namespace OpenRamble.App.Linux;

/// <summary>
/// First launch: the only path a new person has to a working product.
/// </summary>
/// <remarks>
/// Three steps, and which of them lets the person through is <see cref="OnboardingGate"/>'s
/// decision - checked as a table, because the failure mode here is a dead "Continue" button with
/// no explanation, and for a blind person that is simply an unavailable button and the end of the
/// installation. This window never decides; it shows the reason the gate gives.
/// </remarks>
public sealed partial class OnboardingWindow : Window, IDictationSink
{
    private readonly AppState state;
    private readonly int successesAtStart;
    private readonly int pressesAtStart;

    /// <summary>
    /// Drives the microphone meter on the try-out page.
    ///
    /// A clock rather than a callback per frame: the peaks arrive from the audio thread hundreds
    /// of times a second, and marshalling each one onto the dispatcher would put the interface's
    /// throughput in the hands of the sample rate.
    /// </summary>
    private readonly DispatcherTimer levelClock;

    /// <summary>Latest microphone peak, written from the audio thread and read by the clock.</summary>
    private volatile float inputLevel;

    private OnboardingStep step = OnboardingStep.Welcome;
    private bool skippedTryOut;

    /// <summary>Guards the picker against reacting to the selection the render just set.</summary>
    private bool updating;

    public OnboardingWindow(AppState state)
    {
        this.state = state;
        successesAtStart = state.SuccessfulDictationCount;
        pressesAtStart = state.HotkeyPressCount;
        InitializeComponent();
        Icon = TrayIconArt.WindowMark();

        string[] hotkeyTitles = DictationHotkeyInfo.AllCases.Select(hotkey => hotkey.Title).ToArray();
        foreach (ComboBox picker in new[] { WelcomeHotkeyPicker, TryHotkeyPicker })
        {
            picker.ItemsSource = hotkeyTitles;
            picker.SelectionChanged += OnHotkeyChanged;
        }

        ModelRow.ActionClick += OnModelAction;
        MicrophoneRow.ActionClick += OnPermissionAction;

        // The hold has to survive the pointer leaving the button, so the button captures the
        // pointer and the release is taken from the capture rather than from the click: a release
        // that lands outside would otherwise never arrive, and the microphone would stay open with
        // nothing left to close it.
        TryHoldButton.AddHandler(PointerPressedEvent, OnHoldPressed, RoutingStrategies.Tunnel);
        TryHoldButton.AddHandler(PointerReleasedEvent, OnHoldReleased, RoutingStrategies.Tunnel);
        TryHoldButton.PointerCaptureLost += (_, _) => state.ReleaseDictationButton();
        // The fourth way a hold can end without a release: the window goes to the back. An
        // Alt+Tab or another application taking the foreground while the button is down leaves no
        // event on the button at all - and a hold that never ends is a microphone that never
        // closes. Ending it is idempotent, so this costs nothing when the release does arrive.
        Deactivated += (_, _) => state.ReleaseDictationButton();

        levelClock = new DispatcherTimer(
            TimeSpan.FromMilliseconds(80),
            DispatcherPriority.Background,
            (_, _) => TryLevel.Value = RecordingWaveformLayout.BarHeight(inputLevel, TryLevel.Maximum));
        levelClock.Stop();

        state.PropertyChanged += OnStateChanged;
        Closed += (_, _) =>
        {
            state.PropertyChanged -= OnStateChanged;
            // Closing the window mid-hold must not leave the microphone open: the release will be
            // delivered to a window that no longer exists.
            state.ReleaseDictationButton();
            levelClock.Stop();
        };

        Render();
    }

    /// <summary>Live microphone level for the try-out meter. Called from the audio thread.</summary>
    public void UpdateInputLevel(float level) => inputLevel = level;

    private bool TrialSucceeded => skippedTryOut || state.SuccessfulDictationCount > successesAtStart;

    private void OnStateChanged(object? sender, PropertyChangedEventArgs args) =>
        Dispatcher.UIThread.Post(Render);

    private void Render()
    {
        for (int index = 0; index < ProgressDots.Children.Count; index++)
        {
            ProgressDots.Children[index].Classes.Set("current", index == (int)step);
        }
        AutomationProperties.SetName(ProgressDots, step.ProgressAccessibilityLabel);
        ToolTip.SetTip(ProgressDots, step.ProgressText);

        WelcomePage.IsVisible = step == OnboardingStep.Welcome;
        SetupPage.IsVisible = step == OnboardingStep.Setup;
        TryPage.IsVisible = step == OnboardingStep.TryIt;

        BackButton.IsVisible = step.HasPrevious;
        SkipButton.IsVisible = step == OnboardingStep.TryIt && !TrialSucceeded;
        NextButton.Content = step.ContinueTitle(state.ModelStateValue, ModelCard.DownloadMegabytes);

        string? reason = OnboardingGate.BlockReason(
            step,
            new OnboardingConditions(state, TrialSucceeded));

        NextButton.IsEnabled = reason is null;
        BlockReason.Text = reason ?? string.Empty;

        RenderHotkeyPickers();
        if (step == OnboardingStep.Welcome) { RenderWelcome(); }
        if (step == OnboardingStep.Setup) { RenderSetup(); }
        if (step == OnboardingStep.TryIt) { RenderTry(); }
        else { levelClock.Stop(); }
    }

    /// <summary>The key on the welcome page answers the real one before anything is granted.</summary>
    private void RenderWelcome()
    {
        WelcomeKey.Text = state.Hotkey.Title;
        WelcomeKey.IsPressed = state.IsHotkeyHeld;
        WelcomeKeyLine.Text = state.HotkeyPressCount > pressesAtStart
            ? "That is your dictation key."
            : $"Press {state.Hotkey.Title} to find it.";
    }

    private void RenderSetup()
    {
        ModelStatus status = ModelCard;
        ModelStatus.Action? modelAction = status.Actions.Count > 0 ? status.Actions[0] : null;

        ModelRow.Glyph = status.ToneValue switch
        {
            ModelStatus.Tone.Success => StateGlyphKind.Pasted,
            ModelStatus.Tone.Failure => StateGlyphKind.Attention,
            _ => status.Progress is not null || state.IsPreparingEngine
                || state.ModelStateValue is ModelState.Downloading or ModelState.Verifying or ModelState.Deleting
                ? StateGlyphKind.Working
                : StateGlyphKind.Silent,
        };
        ModelRow.Title = status.Title;
        ModelRow.Detail = status.ProgressLabel is not string label ? status.Detail
            : status.Detail is null ? label
            : $"{status.Detail}\n{label}";
        ModelRow.Progress = status.Progress;
        ModelRow.ActionText = modelAction switch
        {
            null => null,
            ModelStatus.Action.Install => SetupRow.DownloadLabel(status.DownloadMegabytes),
            ModelStatus.Action other => status.TitleFor(other),
        };
        ModelRow.IsPrimaryAction = modelAction is not null and not ModelStatus.Action.Cancel;
        ToolTip.SetTip(ModelRow.ActionButton, modelAction is ModelStatus.Action hinted ? status.HintFor(hinted) : null);
        AutomationProperties.SetHelpText(ModelRow.ActionButton, modelAction is ModelStatus.Action spoken ? status.HintFor(spoken) : null);

        PermissionStatus permission = PermissionStatus.Microphone(
            state.MicrophoneState,
            state.MicrophoneGranted
                ? "Granted. OpenRamble opens the microphone only while you hold the dictation key."
                : "OpenRamble needs it to hear you. It opens the microphone only while you hold the dictation key.",
            state.MicrophoneRemedy);

        MicrophoneRow.Glyph = permission.Granted ? StateGlyphKind.Pasted
            : state.MicrophoneState == MicrophoneConsentState.Denied ? StateGlyphKind.Silent
            : StateGlyphKind.Attention;
        MicrophoneRow.Detail = permission.Detail;
        MicrophoneRow.ActionText = permission.ActionTitle;
        // Ink marks the next thing to do: the model's button when it has one, otherwise this one.
        MicrophoneRow.IsPrimaryAction = !ModelRow.IsPrimaryAction;
        AutomationProperties.SetName(MicrophoneRow.ActionButton, permission.ActionAccessibilityLabel);

        // The Linux input path: the key arrives through /dev/input (the input group) or a
        // compositor keybind feeding the control socket. Only an edge proves either.
        string key = state.Hotkey.Title;
        (KeyRow.Glyph, KeyRow.Detail) =
            state.HotkeyTrackingFailure is string failure ? (StateGlyphKind.Attention, failure)
            : state.HotkeyWarning is string warning ? (StateGlyphKind.Attention, warning)
            : state.HotkeyPressCount > pressesAtStart ? (StateGlyphKind.Pasted, $"OpenRamble hears {key}.")
            : (StateGlyphKind.Silent,
                $"Press {key} to check that it reaches OpenRamble — through the input group, keyd or a compositor keybind.");

        bool typingReady = TypingToolsInstalled;
        TypingRow.Glyph = typingReady ? StateGlyphKind.Pasted : StateGlyphKind.Attention;
        TypingRow.Detail = typingReady
            ? "wl-copy and a typing tool are installed, so the words land at your cursor."
            : "Install wl-clipboard and wtype (or ydotool) so the words can land at your cursor.";
    }

    private bool? typingToolsInstalled;

    /// <summary>Asked once: the tools do not come and go while the window is open.</summary>
    private bool TypingToolsInstalled => typingToolsInstalled ??=
        DesktopTools.IsInstalled("wl-copy")
        && (DesktopTools.IsInstalled("wtype") || DesktopTools.IsInstalled("ydotool"));

    /// <summary>
    /// What the app has to say about the model, written once for both steps that show it. The
    /// setup step draws the whole card; the try-out step draws its one-line form while there is
    /// something to say.
    /// </summary>
    private ModelStatus ModelCard => ModelStatus.Make(
        state.ModelStateValue,
        ModelStatus.Place.Onboarding,
        state.EnginePreparation,
        state.RemainingDownloadMegabytes == 0
            ? ModelStatus.FullDownloadMegabytes
            : state.RemainingDownloadMegabytes,
        state.IsEngineReady,
        state.IsPreparingEngine);

    private void RenderTry()
    {
        ModelStatus card = ModelCard;
        TryModelLine.Text = card.SetupLine ?? string.Empty;
        TryModelLine.IsVisible = card.SetupLine is not null;
        AutomationProperties.SetName(TryModelLine, card.Announcement);

        OnboardingTryOut tryOut = OnboardingTryOut.Make(
            state.Hotkey,
            state.IsHotkeyHeld,
            state.HotkeyPressCount > pressesAtStart,
            state.DictationStateValue,
            state.SuccessfulDictationCount > successesAtStart,
            state.IsDictationButtonHeld,
            state.HotkeyTrackingFailure);

        TryHotkeyWarning.Text = state.HotkeyWarning ?? string.Empty;
        TryHotkeyWarning.Foreground = StatusBrushes.Attention;
        TryHotkeyWarning.IsVisible = state.HotkeyWarning is not null;

        TryTitle.Text = $"Hold {state.Hotkey.Title} and say something.";
        TryInstruction.Text = tryOut.Instruction;
        TryKey.Text = state.Hotkey.Title;
        TryKey.IsPressed = state.IsHotkeyHeld || state.IsDictationButtonHeld;
        TryNote.Classes.Set("success", state.SuccessfulDictationCount > successesAtStart);

        // Neutral until something is seen: a line that is already coloured says the key arrived
        // before it did.
        TryStatus.Text = tryOut.Status;
        if (tryOut.Tone is null) { TryStatus.ClearValue(TextBlock.ForegroundProperty); }
        else { TryStatus.Foreground = StatusBrushes.For(tryOut.Tone); }

        TryLevel.IsVisible = tryOut.ShowsInputLevel;
        if (tryOut.ShowsInputLevel) { levelClock.Start(); }
        else
        {
            levelClock.Stop();
            TryLevel.Value = 0;
        }
    }

    /// <summary>Both pickers show the one stored key.</summary>
    private void RenderHotkeyPickers()
    {
        int selected = DictationHotkeyInfo.AllCases.ToList().IndexOf(state.Hotkey);
        updating = true;
        try
        {
            WelcomeHotkeyPicker.SelectedIndex = selected;
            TryHotkeyPicker.SelectedIndex = selected;
        }
        finally
        {
            updating = false;
        }

        // Changing the key mid-dictation would leave the running session with no key to stop it.
        bool idle = state.DictationStateValue == DictationState.Idle;
        WelcomeHotkeyPicker.IsEnabled = idle;
        TryHotkeyPicker.IsEnabled = idle;
    }

    private void OnHotkeyChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (updating || sender is not ComboBox picker) { return; }
        int index = picker.SelectedIndex;
        if (index < 0 || index >= DictationHotkeyInfo.AllCases.Count) { return; }

        // One property for both pickers: it persists the choice and re-arms the live monitor, so
        // the next hold on this very page uses the key that was just picked.
        state.Hotkey = DictationHotkeyInfo.AllCases[index];
        Render();
    }

    /// <summary>
    /// Dictation on the try-out page lands in the try-out box - whichever key or button started
    /// it. On any other page there is no field for it, and the words go where they would have
    /// gone anyway.
    /// </summary>
    public bool TryInsert(string text)
    {
        if (step != OnboardingStep.TryIt) { return false; }

        DictationSinkTextBox.Insert(TryBox, text);
        return true;
    }

    private void OnHoldPressed(object? sender, PointerPressedEventArgs args)
    {
        if (!args.GetCurrentPoint(TryHoldButton).Properties.IsLeftButtonPressed) { return; }

        // The words go where the caret already was, and on this page that is the try-out box.
        // A hold that starts with focus somewhere else would insert into somewhere else.
        TryBox.Focus();
        args.Pointer.Capture(TryHoldButton);
        state.PressDictationButton();
    }

    private void OnHoldReleased(object? sender, PointerReleasedEventArgs args)
    {
        args.Pointer.Capture(null);
        state.ReleaseDictationButton();
    }

    private void OnPermissionAction(object? sender, RoutedEventArgs args) => state.RequestMicrophone();

    private void OnModelAction(object? sender, RoutedEventArgs args)
    {
        IReadOnlyList<ModelStatus.Action> actions = ModelCard.Actions;
        if (actions.Count > 0 && actions[0] == ModelStatus.Action.Cancel) { state.CancelModelInstall(); }
        else { state.InstallModel(); }
    }

    private void OnSkip(object? sender, RoutedEventArgs args)
    {
        skippedTryOut = true;
        Render();
    }

    private void OnBack(object? sender, RoutedEventArgs args)
    {
        if (step.Previous is not OnboardingStep previous) { return; }
        step = previous;
        Render();
    }

    private void OnNext(object? sender, RoutedEventArgs args)
    {
        if (step.Next is OnboardingStep next)
        {
            // The one download starts here and runs while the rest is granted.
            if (step.DownloadsOnContinue(state.ModelStateValue)) { state.InstallModel(); }
            step = next;
            if (step == OnboardingStep.TryIt) { TryBox.Focus(); }
            Render();
            return;
        }

        // Finishing is what makes the app allowed to talk about a missing permission at launch:
        // before that, the setup window is already saying it.
        state.OnboardingCompleted = true;
        Close();
    }
}
