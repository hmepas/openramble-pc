using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Platform;
using Avalonia.Threading;
using DictationCore;
using OpenRamble.AppCore;

namespace OpenRamble.App.Linux;

/// <summary>
/// The dictation panel: the app's only feedback channel while a person is speaking.
/// </summary>
/// <remarks>
/// <para>
/// <b>It must never take the focus.</b> The text is about to be inserted into whatever window the
/// person was typing in, and a panel that steals focus would insert it into itself.
/// <c>ShowActivated="False"</c> and <c>Focusable="False"</c> are what this platform offers for
/// that, and they are weaker than the Windows trio: there is no <c>WS_EX_NOACTIVATE</c> to stop a
/// click from activating the window and no <c>WS_EX_TRANSPARENT</c> to let the click through to
/// the document underneath. The panel is small, has nothing to click and sits well clear of where
/// people type, so the exposure is a mis-aimed click - but it is a real difference from the other
/// platform and not a detail that will be noticed later.
/// </para>
/// <para>
/// <b>It is a plain top-level window, not a layer-shell surface.</b> A dictation panel wants
/// <c>zwlr_layer_shell_v1</c>: an overlay layer above every application, positioned by anchor
/// rather than by coordinate, never focusable by construction. That is a wave of its own and is
/// deliberately deferred. What this means in practice today: under a Wayland session Avalonia
/// draws through XWayland, where <c>Topmost</c> and an absolute position work as they do on X11 -
/// and on a compositor that refuses either, the panel is a window in the ordinary stack, and the
/// top/bottom choice in Settings is a request the compositor may decline.
/// </para>
/// <para>
/// What is written on it is <see cref="OverlayContent"/>'s decision, and when it appears and hides
/// is <see cref="OverlayModel"/>'s - both are checked without a screen. Where it sits is
/// <see cref="OverlayPlacementPolicy"/>'s. This file draws and nothing else.
/// </para>
/// </remarks>
public sealed partial class OverlayWindow : Window,
    IOverlayPresenting,
    IAccessibilityAnnouncing,
    IOverlayPlacementConfiguring,
    IEngineWaitPresenting
{
    private readonly OverlayModel model;
    private readonly DispatcherTimer redraw;
    private readonly DispatcherTimer meter;
    private readonly Rectangle[] bars = new Rectangle[OverlayModel.WaveformSampleCount];

    /// <summary>The newest microphone peak, written from the audio thread and read by the meter clock.</summary>
    private volatile float latestLevel;

    private DictationOverlayPlacement placement = SettingsDefaults.OverlayPlacement;

    public OverlayWindow()
    {
        InitializeComponent();

        model = new OverlayModel(this);
        model.OnVisibilityChange = visible => Dispatcher.UIThread.Post(() => SetVisible(visible));

        for (int index = 0; index < bars.Length; index++)
        {
            var bar = new Rectangle { Width = 3, RadiusX = 1.5, RadiusY = 1.5 };
            bars[index] = bar;
            Canvas.SetLeft(bar, index * 4);
            Waveform.Children.Add(bar);
        }

        // Half a second: the panel shows whole seconds, so anything faster redraws the same
        // number. The timer runs only while a dictation is running.
        redraw = new DispatcherTimer(
            TimeSpan.FromMilliseconds(500),
            DispatcherPriority.Background,
            (_, _) => Render());
        redraw.Stop();

        // The level meter draws as fast as the screen can show it and no faster. Peaks arrive
        // from the audio thread tens of times a second; each used to be one hop onto the UI
        // thread, competing with the dictation it was drawn for. Now the newest peak waits here
        // and the clock takes it - dropped frames cost nothing visible, because a display cannot
        // show more than it is given, and the newest value always wins (upstream 0.23.0).
        meter = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16),
            DispatcherPriority.Render,
            (_, _) => TickMeter());
        meter.Stop();
    }

    /// <summary>Where on the screen the panel sits. Applied on the next show, or now if it is up.</summary>
    public DictationOverlayPlacement Placement
    {
        set
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (placement == value) { return; }
                placement = value;
                if (IsVisible) { Place(); }
            });
        }
    }

    public Task Present(DictationState state, TimeSpan elapsed)
    {
        Dispatcher.UIThread.Post(() =>
        {
            model.Show(state, elapsed);
            Render();
            UpdateTicking();
        });
        return Task.CompletedTask;
    }

    public Task Dismiss()
    {
        Dispatcher.UIThread.Post(() =>
        {
            model.Hide();
            UpdateTicking();
        });
        return Task.CompletedTask;
    }

    public Task PresentNotice(DictationNotice notice)
    {
        Dispatcher.UIThread.Post(() =>
        {
            model.ShowNotice(notice);
            Render();
            UpdateTicking();
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// The take is finished and the model is not resident yet: say "Waking the model…" rather
    /// than claiming to transcribe half a second of audio for fifteen seconds.
    /// </summary>
    public void SetWaitingForEngine(bool waiting) =>
        Dispatcher.UIThread.Post(() =>
        {
            model.SetWaitingForEngine(waiting);
            Render();
        });

    /// <summary>Live microphone level for the waveform. Called from the audio thread; kept, not drawn.</summary>
    public void UpdateInputLevel(float level) => latestLevel = level;

    /// <summary>
    /// What the recognition engine is doing, for the panel to say while it waits on it.
    /// </summary>
    /// <remarks>
    /// Redraws immediately rather than waiting for the next <see cref="Present"/>: between the key
    /// release and the result there is no further state change and the redraw clock is stopped, so
    /// without this the seconds on a reload would never move.
    /// </remarks>
    public void UpdateEnginePreparation(EnginePreparationState preparation) =>
        Dispatcher.UIThread.Post(() =>
        {
            model.UpdateEnginePreparation(preparation);
            Render();
        });

    /// <summary>
    /// Say it out loud - or rather, do not, and record that we did not.
    /// </summary>
    /// <remarks>
    /// The Windows panel raises a UI Automation notification, which reaches Narrator without the
    /// panel ever having focus. Avalonia's automation peers carry no notification event on this
    /// platform: AT-SPI has <c>Announce</c>, Avalonia does not expose it, and inventing an
    /// announcement by moving focus is precisely what this window must never do. So the message
    /// goes to the log, where it at least proves what a screen reader was owed. The panel itself
    /// still says every one of these in writing - this is the spoken copy of it that is missing.
    /// </remarks>
    public void Announce(string message, bool urgent) =>
        DiagnosticLog.Emit("announce", ("urgent", urgent), ("spoken", false), ("message", message));

    private void SetVisible(bool visible)
    {
        if (!visible)
        {
            Hide();
            meter.Stop();
            return;
        }

        Show();
        // Placed after the show, not before it: until the window has been laid out its size is
        // nothing at all, and the very first press - the one on the try-out page, and the one that
        // decides whether a person believes the product works - would put the panel off-centre.
        Place();
    }

    private void UpdateTicking()
    {
        if (model.IsTicking && !redraw.IsEnabled) { redraw.Start(); }
        else if (!model.IsTicking && redraw.IsEnabled) { redraw.Stop(); }

        bool recording = model.State == DictationState.Listening && model.Notice is null && model.IsVisible;
        if (recording && !meter.IsEnabled) { meter.Start(); }
        else if (!recording && meter.IsEnabled) { meter.Stop(); }
    }

    private void TickMeter()
    {
        model.UpdateInputLevel(latestLevel);
        if (Waveform.IsVisible) { RenderWaveform(); }
    }

    private void Render()
    {
        OverlayContent content = model.Content;
        TitleText.Text = content.Title;
        SubtitleText.Text = content.Subtitle ?? string.Empty;
        SubtitleText.IsVisible = content.Subtitle is not null;
        ToneDot.Fill = StatusBrushes.For(content.Tone.ColorRole);
        AutomationProperties.SetName(Panel, content.AccessibilityLabel);

        bool recording = model.State == DictationState.Listening && model.Notice is null;
        Waveform.IsVisible = recording;
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
    /// Top or bottom centre of the work area, clear of whatever panel lives at that edge.
    ///
    /// Not near the cursor and not near the caret: the person is looking at what they are
    /// dictating into, and a panel that followed the caret would cover the very words it is about
    /// to insert. Which edge is the person's choice in Settings; the geometry is
    /// <see cref="OverlayPlacementPolicy"/>'s.
    /// </summary>
    /// <remarks>
    /// The work area is in physical pixels and the window's size is in device-independent ones, so
    /// the scaling has to be applied by hand - the one piece of arithmetic WPF did for us. On a
    /// desktop that refuses to place a window where it is asked, this is a no-op and the panel
    /// lands wherever the compositor puts it.
    /// </remarks>
    private void Place()
    {
        Screen? screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) { return; }

        PixelRect work = screen.WorkingArea;
        double scale = screen.Scaling;
        var size = new OverlaySize(
            Math.Round(Bounds.Width * scale),
            Math.Round(Bounds.Height * scale));

        OverlayPoint origin = OverlayPlacementPolicy.Origin(
            placement,
            new OverlayFrame(work.X, work.Y, work.Width, work.Height),
            size);
        Position = new PixelPoint((int)Math.Round(origin.X), (int)Math.Round(origin.Y));
    }
}
