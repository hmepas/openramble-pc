using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
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
/// the document underneath. The panel sits well clear of where people type, so the exposure is a
/// mis-aimed click - but it is a real difference from the other platform and not a detail that
/// will be noticed later. Waiting words add buttons, which are not focusable; still, a window
/// manager that activates on click hands the panel the keyboard, and the action's paste then
/// goes to whatever has focus after that click. Escape on the panel works only in that case.
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
    IEngineWaitPresenting,
    IWaitingWordsPresenting
{
    /// <summary>Panel widths per kind (upstream <c>OverlayContent.panelWidth</c>): a take never re-centres.</summary>
    private const double TakeWidth = 244;
    private const double SentenceWidth = 320;
    private const double WaitingWidth = 384;

    private readonly OverlayModel model;
    private readonly DispatcherTimer redraw;
    private readonly DispatcherTimer meter;

    /// <summary>
    /// Half a second into a state that may linger (starting the microphone, writing it down):
    /// nine takes in ten are done by then and never show a title (upstream <c>titleDelay</c>).
    /// </summary>
    private readonly DispatcherTimer linger;
    private DictationState? lingerState;
    private bool hasLingered;

    /// <summary>The take is hands-free: the red lock stands where the red disc was.</summary>
    private bool isHandsFree;

    /// <summary>The newest microphone peak, written from the audio thread and read by the meter clock.</summary>
    private volatile float latestLevel;

    private DictationOverlayPlacement placement = SettingsDefaults.OverlayPlacement;

    /// <summary>
    /// Device pixels already taken at the panel's edge (the recording pill while it is up). Read
    /// at every placement; <see cref="Replace"/> re-reads it when the pill comes or goes.
    /// </summary>
    public Func<double> EdgeReserve { get; set; } = () => 0;

    /// <summary>Place again if shown - the room at the edge changed.</summary>
    public void Replace() { if (IsVisible) { Place(); } }

    public OverlayWindow()
    {
        InitializeComponent();

        model = new OverlayModel(this);
        model.OnVisibilityChange = visible => Dispatcher.UIThread.Post(() => SetVisible(visible));

        // A click copies rather than pastes: on a click-to-focus window manager the click gives
        // this panel the keyboard, and a retried Ctrl+V would land here. The key still pastes.
        ActionButton.Click += (_, _) => OnWaitingCopy?.Invoke();
        LaterButton.Click += (_, _) => OnWaitingLater?.Invoke();
        CopiedLaterButton.Click += (_, _) => OnWaitingLater?.Invoke();
        // Escape reaches the panel only when the window manager gave it the keyboard - after a
        // click on it, say. The global Escape is the cancel shortcut's, not this.
        AddHandler(KeyDownEvent, OnPanelKeyDown, RoutingStrategies.Tunnel);
        // Waiting words grow the panel; it is placed again so it stays centred and on screen.
        PropertyChanged += (_, change) =>
        {
            if (change.Property == ClientSizeProperty && IsVisible) { Place(); }
        };
        // A bar that appears, moves or goes away moves the work area under an open panel.
        WorkArea.Changed += () => Dispatcher.UIThread.Post(() => { if (IsVisible) { Place(); } });

        linger = new DispatcherTimer(
            TimeSpan.FromMilliseconds(500),
            DispatcherPriority.Background,
            (_, _) =>
            {
                linger!.Stop();
                hasLingered = true;
                Render();
            });
        linger.Stop();

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

    public Action? OnWaitingAction { get; set; }

    public Action? OnWaitingLater { get; set; }

    /// <summary>The action button's click: copy the waiting words. Wired by the app.</summary>
    public Action? OnWaitingCopy { get; set; }

    public void SetWaiting(WaitingWords? waiting, DictationHotkey key) =>
        Dispatcher.UIThread.Post(() =>
        {
            model.SetWaiting(waiting, key);
            Render();
        });

    public void ShowOutcome(TakeOutcome outcome) =>
        Dispatcher.UIThread.Post(() =>
        {
            model.ShowOutcome(outcome);
            Render();
        });

    public void SetKeyDown(bool down) =>
        Dispatcher.UIThread.Post(() =>
        {
            model.SetKeyDown(down);
            Render();
        });

    /// <summary>The take went hands-free, or came back from it. Wired by the app from its state.</summary>
    public void SetHandsFree(bool active) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (isHandsFree == active) { return; }
            isHandsFree = active;
            Render();
        });

    private void OnPanelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || model.Waiting is null) { return; }
        e.Handled = true;
        OnWaitingLater?.Invoke();
    }

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
        if (Waveform.IsVisible) { Waveform.Samples = model.WaveformSamples; }
    }

    private void Render()
    {
        OverlayContent content = model.Content;
        AutomationProperties.SetName(Panel, content.AccessibilityLabel);
        UpdateLinger();

        // Waiting words first, as the content decides; then a sentence (a notice, an outcome);
        // otherwise the take's one shape from key-down to text.
        bool sentence = content.Waiting is null && (content.Outcome is not null || model.Notice is not null);
        bool take = content.Waiting is null && !sentence;
        TakeLine.IsVisible = take;
        SentenceLine.IsVisible = sentence;
        Panel.Width = content.Waiting is not null ? WaitingWidth : sentence ? SentenceWidth : TakeWidth;

        if (take) { RenderTake(content); }
        if (sentence)
        {
            SentenceGlyph.Kind = SentenceGlyphKind(content);
            SentenceText.Text = content.Title;
        }
        RenderWaiting(content);
    }

    /// <summary>A state that may linger starts its half-second again whenever the state changes.</summary>
    private void UpdateLinger()
    {
        DictationState state = model.State;
        if (lingerState == state) { return; }
        lingerState = state;
        hasLingered = false;
        linger.Stop();
        if (state is DictationState.Preparing or DictationState.Transcribing) { linger.Start(); }
    }

    /// <summary>
    /// Glyph, meter, clock (0.41: no "Listening"). Red only while the microphone is live; after the
    /// release the meter and clock freeze in gray, and a state that outlasts half a second is said
    /// in quiet gray text in their place, at the same size.
    /// </summary>
    private void RenderTake(OverlayContent content)
    {
        DictationState state = model.State;
        bool listening = state == DictationState.Listening;
        // Waiting on the model is news at once: the subtitle is set only for that wait.
        bool waitingOnEngine = state == DictationState.Transcribing && content.Subtitle is not null;
        bool showsTitle = waitingOnEngine || (hasLingered && state is DictationState.Preparing or DictationState.Transcribing);

        TakeGlyph.Kind = listening
            ? (isHandsFree ? StateGlyphKind.HandsFree : StateGlyphKind.Listening)
            : showsTitle && state == DictationState.Transcribing ? StateGlyphKind.Working : StateGlyphKind.Silent;

        LingerText.IsVisible = showsTitle;
        LingerText.Text = showsTitle ? content.Title : string.Empty;
        Waveform.IsVisible = !showsTitle;
        ClockText.IsVisible = !showsTitle;
        if (showsTitle) { return; }

        Waveform.Foreground = listening ? StatusBrushes.Recording : null;
        Waveform.Samples = model.WaveformSamples;
        ClockText.Text = RecordingTime.Clock(model.Elapsed);
    }

    private static StateGlyphKind SentenceGlyphKind(OverlayContent content) => content.Outcome switch
    {
        TakeOutcome.Pasted => StateGlyphKind.Pasted,
        TakeOutcome.Kept => StateGlyphKind.Kept,
        _ => content.Tone == OverlayTone.Info ? StateGlyphKind.Info : StateGlyphKind.Attention,
    };

    private void RenderWaiting(OverlayContent content)
    {
        WaitingWords? waiting = content.Waiting;
        WaitingPanel.IsVisible = waiting is not null;
        if (waiting is null) { return; }

        string key = model.Key.Title;
        if (WordsText.Text != waiting.Words)
        {
            WordsText.Text = waiting.Words;
            WordsScroll.Offset = default;
        }

        NoteText.Text = waiting.Note ?? string.Empty;
        NoteText.IsVisible = waiting.Note is not null;
        ReasonText.Text = waiting.Reason(key);
        ReasonGlyph.Kind = waiting.IsCopied ? StateGlyphKind.Copied : StateGlyphKind.Waiting;
        // Copied words have nothing left to press but the way out, so it shares the reason's line.
        ActionRow.IsVisible = !waiting.IsCopied;
        CopiedLaterButton.IsVisible = waiting.IsCopied;
        ActionKeyCap.Text = key;
        ActionKeyCap.IsPressed = model.IsKeyDown;
        ActionVerbText.Text = waiting.ActionVerb;
        AutomationProperties.SetName(ActionButton, "Copy");
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
    /// The work area is the compositor's where it says (see <see cref="WorkArea"/>), and it is in
    /// physical pixels and the window's size is in device-independent ones, so
    /// the scaling has to be applied by hand - the one piece of arithmetic WPF did for us. On a
    /// desktop that refuses to place a window where it is asked, this is a no-op and the panel
    /// lands wherever the compositor puts it.
    /// </remarks>
    private void Place()
    {
        Screen? screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) { return; }

        PixelRect work = WorkArea.For(screen, Screens.ScreenCount);
        double scale = screen.Scaling;
        var size = new OverlaySize(
            Math.Round(Bounds.Width * scale),
            Math.Round(Bounds.Height * scale));

        OverlayPoint origin = OverlayPlacementPolicy.Origin(
            placement,
            ShadowFrame(work, Panel.Margin, scale),
            size,
            EdgeReserve());
        Position = new PixelPoint((int)Math.Round(origin.X), (int)Math.Round(origin.Y));
    }

    /// <summary>
    /// The work area grown by the window's transparent shadow margin, so that the glass - not the
    /// window around it - lands where the placement policy puts things.
    /// </summary>
    internal static OverlayFrame ShadowFrame(PixelRect work, Thickness margin, double scale) =>
        new(
            work.X - (margin.Left * scale),
            work.Y - (margin.Top * scale),
            work.Width + ((margin.Left + margin.Right) * scale),
            work.Height + ((margin.Top + margin.Bottom) * scale));
}
