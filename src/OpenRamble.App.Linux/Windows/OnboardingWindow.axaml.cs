using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using DictationCore;
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
public sealed partial class OnboardingWindow : Window
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

        TryHotkeyPicker.ItemsSource = DictationHotkeyInfo.AllCases.Select(hotkey => hotkey.Title).ToArray();
        TryHotkeyPicker.SelectionChanged += OnHotkeyChanged;

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
        ProgressText.Text = step.ProgressText;
        AutomationProperties.SetName(ProgressText, step.ProgressAccessibilityLabel);

        WelcomePage.IsVisible = step == OnboardingStep.Welcome;
        SetupPage.IsVisible = step == OnboardingStep.Setup;
        TryPage.IsVisible = step == OnboardingStep.TryIt;

        BackButton.IsVisible = step.HasPrevious;
        SkipButton.IsVisible = step == OnboardingStep.TryIt && !TrialSucceeded;
        NextButton.Content = step.NextButtonTitle;

        string? reason = OnboardingGate.BlockReason(
            step,
            state.MicrophoneGranted,
            state.ModelStateValue,
            state.IsEngineReady || state.HasEngineBeenReady,
            TrialSucceeded);

        NextButton.IsEnabled = reason is null;
        BlockReason.Text = reason ?? string.Empty;
        BlockReason.Foreground = StatusBrushes.Attention;
        BlockReason.IsVisible = reason is not null;

        if (step == OnboardingStep.Setup) { RenderSetup(); }
        if (step == OnboardingStep.TryIt) { RenderTry(); }
        else { levelClock.Stop(); }
    }

    private void RenderSetup()
    {
        PermissionStatus permission = PermissionStatus.Microphone(
            state.MicrophoneState,
            state.MicrophoneGranted
                ? "Granted. OpenRamble opens the microphone only while you hold the dictation key."
                : "OpenRamble needs it to hear you. It opens the microphone only while you hold the dictation key.");

        PermissionDetail.Text = permission.Detail;
        PermissionButton.Content = permission.ActionTitle ?? string.Empty;
        PermissionButton.IsVisible = permission.ActionTitle is not null;

        ModelStatus status = ModelStatus.Make(
            state.ModelStateValue,
            state.IsPreparingEngine,
            ModelStatus.Place.Onboarding,
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
            ModelStatus.Action captured = action;
            button.Click += (_, _) =>
            {
                if (captured == ModelStatus.Action.Cancel) { state.CancelModelInstall(); }
                else { state.InstallModel(); }
            };
            ModelActions.Children.Add(button);
        }
    }

    private void RenderTry()
    {
        OnboardingTryOut tryOut = OnboardingTryOut.Make(
            state.Hotkey,
            state.IsHotkeyHeld,
            state.HotkeyPressCount > pressesAtStart,
            state.DictationStateValue,
            state.SuccessfulDictationCount > successesAtStart,
            state.IsDictationButtonHeld,
            state.HotkeyTrackingFailure);

        updating = true;
        try
        {
            TryHotkeyPicker.SelectedIndex = DictationHotkeyInfo.AllCases.ToList().IndexOf(state.Hotkey);
        }
        finally
        {
            updating = false;
        }

        // Changing the key mid-dictation would leave the running session with no key to stop it.
        TryHotkeyPicker.IsEnabled = state.DictationStateValue == DictationState.Idle;
        TryHotkeyWarning.Text = state.HotkeyWarning ?? string.Empty;
        TryHotkeyWarning.Foreground = StatusBrushes.Attention;
        TryHotkeyWarning.IsVisible = state.HotkeyWarning is not null;

        TryInstruction.Text = tryOut.Instruction;
        TryKeyName.Text = state.Hotkey.Title;

        // Neutral until something is seen: a badge that is already coloured says the key arrived
        // before it did.
        IBrush tone = tryOut.Tone is null ? StatusBrushes.Neutral : StatusBrushes.For(tryOut.Tone);
        TryKeyBadge.BorderBrush = tone;
        TryKeyDot.Fill = tone;
        TryStatus.Text = tryOut.Status;
        TryStatus.Foreground = tryOut.Tone is null ? Brushes.Gray : tone;

        TryLevel.IsVisible = tryOut.ShowsInputLevel;
        if (tryOut.ShowsInputLevel) { levelClock.Start(); }
        else
        {
            levelClock.Stop();
            TryLevel.Value = 0;
        }
    }

    private void OnHotkeyChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (updating) { return; }
        int index = TryHotkeyPicker.SelectedIndex;
        if (index < 0 || index >= DictationHotkeyInfo.AllCases.Count) { return; }

        // One property for both pickers: it persists the choice and re-arms the live monitor, so
        // the next hold on this very page uses the key that was just picked.
        state.Hotkey = DictationHotkeyInfo.AllCases[index];
        Render();
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
