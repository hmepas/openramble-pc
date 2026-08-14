using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DictationCore;
using OpenRamble.AppCore;

namespace OpenRamble.App;

/// <summary>
/// The dictation panel: the app's only feedback channel while a person is speaking.
/// </summary>
/// <remarks>
/// <para>
/// <b>It must never take the focus.</b> The text is about to be inserted into whatever window the
/// person was typing in, and a panel that steals focus would insert it into itself. Three things
/// enforce that together, and all three are needed: <c>ShowActivated="False"</c> (WPF's own
/// activation on show), <c>WS_EX_NOACTIVATE</c> (the shell's - without it a click on the panel
/// still activates it), and <c>WS_EX_TOOLWINDOW</c> (keeps it out of Alt+Tab, where selecting it
/// would activate it anyway).
/// </para>
/// <para>
/// It carries no <c>Icon</c>, and setting one would change nothing: with <c>WindowStyle="None"</c>
/// there is no title bar, <c>ShowInTaskbar="False"</c> takes the taskbar button away, and
/// <c>WS_EX_TOOLWINDOW</c> the Alt+Tab entry - no surface is left that would draw it.
/// </para>
/// <para>
/// What is written on it is <see cref="OverlayContent"/>'s decision, and when it appears and
/// hides is <see cref="OverlayModel"/>'s - both are checked without a screen. This file draws
/// and nothing else.
/// </para>
/// </remarks>
public sealed partial class OverlayWindow : Window, IOverlayPresenting, IAccessibilityAnnouncing
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTransparent = 0x00000020;

    private readonly OverlayModel model;
    private readonly DispatcherTimer redraw;
    private readonly Rectangle[] bars = new Rectangle[OverlayModel.WaveformSampleCount];

    public OverlayWindow()
    {
        InitializeComponent();

        model = new OverlayModel(this);
        model.OnVisibilityChange = visible => Dispatcher.BeginInvoke(() => SetVisible(visible));

        for (int index = 0; index < bars.Length; index++)
        {
            var bar = new Rectangle { Width = 3, RadiusX = 1.5, RadiusY = 1.5 };
            bars[index] = bar;
            Canvas.SetLeft(bar, index * 4);
            Waveform.Children.Add(bar);
        }

        // Half a second: the panel shows whole seconds, so anything faster redraws the same
        // number. The timer runs only while a dictation is running.
        redraw = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        redraw.Tick += (_, _) => Render();

        SourceInitialized += (_, _) => ApplyNonActivatingStyles();
    }

    public Task Present(DictationState state, TimeSpan elapsed)
    {
        Dispatcher.BeginInvoke(() =>
        {
            model.Show(state, elapsed);
            Render();
            UpdateTicking();
        });
        return Task.CompletedTask;
    }

    public Task Dismiss()
    {
        Dispatcher.BeginInvoke(() =>
        {
            model.Hide();
            UpdateTicking();
        });
        return Task.CompletedTask;
    }

    public Task PresentNotice(DictationNotice notice)
    {
        Dispatcher.BeginInvoke(() =>
        {
            model.ShowNotice(notice);
            Render();
            UpdateTicking();
        });
        return Task.CompletedTask;
    }

    /// <summary>Live microphone level for the waveform.</summary>
    public void UpdateInputLevel(float level) => model.UpdateInputLevel(level);

    /// <summary>
    /// What the recognition engine is doing, for the panel to say while it waits on it.
    /// </summary>
    /// <remarks>
    /// Redraws immediately rather than waiting for the next <see cref="Present"/>: between the key
    /// release and the result there is no further state change and the redraw clock is stopped, so
    /// without this the seconds on a reload would never move.
    /// </remarks>
    public void UpdateEnginePreparation(EnginePreparationState preparation) =>
        Dispatcher.BeginInvoke(() =>
        {
            model.UpdateEnginePreparation(preparation);
            Render();
        });

    /// <summary>
    /// Say it out loud.
    /// </summary>
    /// <remarks>
    /// A UI Automation notification, the counterpart of macOS's announcement request: it reaches
    /// Narrator and every screen reader that listens for the event, and it does not require the
    /// panel to have focus - which it must never have.
    /// </remarks>
    public void Announce(string message, bool urgent)
    {
        Dispatcher.BeginInvoke(() =>
        {
            AutomationPeer? peer = UIElementAutomationPeer.CreatePeerForElement(Panel);
            peer?.RaiseNotificationEvent(
                AutomationNotificationKind.ActionCompleted,
                urgent
                    ? AutomationNotificationProcessing.ImportantMostRecent
                    : AutomationNotificationProcessing.MostRecent,
                message,
                "OpenRamble");
        });
    }

    private void SetVisible(bool visible)
    {
        if (!visible)
        {
            Hide();
            return;
        }

        PlaceAboveTaskbar();
        Show();
        // And again, because the first placement of the session was a guess. Until the window has
        // a source it measures as nothing at all, so ActualWidth and ActualHeight are zero and the
        // very first press - the one on the try-out page, and the one that decides whether a
        // person believes the product works - put the panel off-centre and half under the taskbar.
        PlaceAboveTaskbar();
    }

    private void UpdateTicking()
    {
        if (model.IsTicking && !redraw.IsEnabled) { redraw.Start(); }
        else if (!model.IsTicking && redraw.IsEnabled) { redraw.Stop(); }
    }

    private void Render()
    {
        OverlayContent content = model.Content;
        TitleText.Text = content.Title;
        SubtitleText.Text = content.Subtitle ?? string.Empty;
        SubtitleText.Visibility = content.Subtitle is null ? Visibility.Collapsed : Visibility.Visible;
        ToneDot.Fill = StatusBrushes.For(content.Tone.ColorRole);
        AutomationProperties.SetName(Panel, content.AccessibilityLabel);

        bool recording = model.State == DictationState.Listening && model.Notice is null;
        Waveform.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        if (recording) { RenderWaveform(); }
    }

    private void RenderWaveform()
    {
        IReadOnlyList<float> samples = model.WaveformSamples;
        double maximum = Waveform.Height;
        for (int index = 0; index < bars.Length; index++)
        {
            double height = RecordingWaveformLayout.BarHeight(samples[index], maximum);
            Rectangle bar = bars[index];
            bar.Height = height;
            bar.Fill = StatusBrushes.Recording;
            bar.Opacity = RecordingWaveformLayout.BarOpacity(index, bars.Length);
            Canvas.SetTop(bar, (maximum - height) / 2);
        }
    }

    /// <summary>
    /// Bottom centre of the work area, clear of the taskbar.
    ///
    /// Not near the cursor and not near the caret: the person is looking at what they are
    /// dictating into, and a panel that follows the caret would cover the very words it is about
    /// to insert.
    /// </summary>
    private void PlaceAboveTaskbar()
    {
        Rect work = SystemParameters.WorkArea;
        Width = double.NaN;
        UpdateLayout();
        Left = work.Left + ((work.Width - ActualWidth) / 2);
        Top = work.Bottom - ActualHeight - 48;
    }

    private void ApplyNonActivatingStyles()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        int style = GetWindowLongW(handle, GwlExStyle);
        // WS_EX_TRANSPARENT as well: the panel is a status readout with nothing to click, and a
        // click that lands on it instead of the document underneath is a lost keystroke.
        _ = SetWindowLongW(handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow | WsExTransparent);
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLongW(nint window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static partial int SetWindowLongW(nint window, int index, int value);
}
