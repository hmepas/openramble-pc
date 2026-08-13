using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using OpenRamble.AppCore;

namespace OpenRamble.App;

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
    private OnboardingStep step = OnboardingStep.Welcome;
    private bool skippedTryOut;

    public OnboardingWindow(AppState state)
    {
        this.state = state;
        successesAtStart = state.SuccessfulDictationCount;
        InitializeComponent();

        state.PropertyChanged += OnStateChanged;
        Closed += (_, _) => state.PropertyChanged -= OnStateChanged;

        Render();
    }

    private bool TrialSucceeded => skippedTryOut || state.SuccessfulDictationCount > successesAtStart;

    private void OnStateChanged(object? sender, PropertyChangedEventArgs args) =>
        Dispatcher.BeginInvoke(Render);

    private void Render()
    {
        ProgressText.Text = step.ProgressText;
        System.Windows.Automation.AutomationProperties.SetName(
            ProgressText, step.ProgressAccessibilityLabel);

        WelcomePage.Visibility = step == OnboardingStep.Welcome ? Visibility.Visible : Visibility.Collapsed;
        SetupPage.Visibility = step == OnboardingStep.Setup ? Visibility.Visible : Visibility.Collapsed;
        TryPage.Visibility = step == OnboardingStep.TryIt ? Visibility.Visible : Visibility.Collapsed;

        BackButton.Visibility = step.HasPrevious ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = step == OnboardingStep.TryIt && !TrialSucceeded
            ? Visibility.Visible
            : Visibility.Collapsed;
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
        BlockReason.Visibility = reason is null ? Visibility.Collapsed : Visibility.Visible;

        if (step == OnboardingStep.Setup) { RenderSetup(); }
        if (step == OnboardingStep.TryIt) { RenderTry(); }
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
        PermissionButton.Visibility = permission.ActionTitle is null ? Visibility.Collapsed : Visibility.Visible;

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
        TryInstruction.Text = $"Hold {state.Hotkey.Title}, say a sentence, then let go.";
        bool passed = state.SuccessfulDictationCount > successesAtStart;
        TryResult.Text = passed ? "That is the whole product. You are set up." : string.Empty;
        TryResult.Foreground = StatusBrushes.Success;
        TryResult.Visibility = passed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPermissionAction(object sender, RoutedEventArgs args) => state.RequestMicrophone();

    private void OnSkip(object sender, RoutedEventArgs args)
    {
        skippedTryOut = true;
        Render();
    }

    private void OnBack(object sender, RoutedEventArgs args)
    {
        if (step.Previous is not OnboardingStep previous) { return; }
        step = previous;
        Render();
    }

    private void OnNext(object sender, RoutedEventArgs args)
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
