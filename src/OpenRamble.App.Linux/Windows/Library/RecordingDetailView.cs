using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DictationCore;
using DictationCore.Audio;
using OpenRamble.App.Linux.Controls;
using OpenRamble.AppCore;

namespace OpenRamble.App.Linux;

/// <summary>
/// One recording in the Library (upstream RecordingDetail.swift, RecordingTransport.swift,
/// TranscriptView.swift): its title (double-click to rename), what kind it is, the transcript
/// with clickable timestamps and the paragraph being heard highlighted, and a transport whose
/// slider sits on the recording's waveform. The recording in progress shows here too
/// (upstream LiveRecordingDetail, CaptureHealthStrip, TranscriptStatusLine): its transcript as it
/// grows, what is wrong with either side, how far behind the words are - and no player.
/// </summary>
/// <remarks>
/// <para>
/// Plays through the Library's one <see cref="RecordingPlayer"/>, owned by the window.
/// <see cref="Show"/> picks the recording (another one pauses what was playing);
/// <see cref="Detach"/> - also run when the view leaves the window - pauses and stops listening.
/// </para>
/// <para>
/// The player has no position events: while it plays, or the slider is dragged, a timer reads
/// <see cref="RecordingPlayer.Position"/> ten times a second. State and player events arrive on
/// background threads and are posted to the UI thread; anything that arrives for a recording no
/// longer shown is dropped.
/// </para>
/// </remarks>
public sealed class RecordingDetailView : UserControl
{
    private static readonly IBrush AttentionWash = new SolidColorBrush(Color.FromArgb(0x1F, 0xF7, 0x6B, 0x15));

    /// <summary>Lucide "share" (ISC): export.</summary>
    private static readonly Geometry ShareIcon = Geometry.Parse("M4 12v8a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-8 M16 6l-4-4-4 4 M12 2v13");

    /// <summary>How far the back and forward buttons skip.</summary>
    private const double SkipSeconds = 15;

    /// <summary>How far behind the live words may be before the line is worth saying (upstream TranscriptStatusPolicy).</summary>
    private const double BacklogVisibleAfter = 3;

    private readonly AppState state;
    private readonly RecordingPlayer player;
    private readonly TransportScrub scrub = new();
    private readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private readonly TextBlock title = new()
    {
        Classes = { "display24" },
        TextWrapping = TextWrapping.Wrap,
        MaxLines = 2,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private readonly TextBox titleEditor = new()
    {
        FontFamily = LibraryLook.Serif,
        FontSize = 24,
        IsVisible = false,
    };

    private readonly TextBlock details = new() { Classes = { "detail" } };
    private readonly TextBlock note = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border noteStrip = new() { Classes = { "content", "attention" }, Padding = new Thickness(16), Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel transcript = new() { MaxWidth = 748, Margin = new Thickness(36, 8), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ScrollViewer scroller = new();
    private readonly Button jumpToLatest = new()
    {
        Classes = { "plain" },
        Content = "Jump to Latest",
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(0, 0, 0, 8),
        IsVisible = false,
    };

    private readonly TextBlock placeholderTitle = new() { Classes = { "display24" }, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock placeholderDetail = new()
    {
        Classes = { "body" },
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        MaxWidth = 360,
    };

    private readonly StackPanel placeholder = new() { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock importLine = new() { Classes = { "detail" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button retryButton = new() { Classes = { "plain" }, Content = "Retry", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly DockPanel statusLine = new() { Margin = new Thickness(36, 4) };
    private readonly StackPanel healthStrip = new() { Spacing = 8, MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
    private readonly MenuItem exportTranscript = new() { Header = "Export Transcript…" };
    private readonly MenuItem exportAudio = new() { Header = "Export Audio…" };
    private readonly Button exportButton = new() { Classes = { "icon" }, Content = new LucideIcon { Data = ShareIcon }, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0) };
    private readonly Grid transportBar = new() { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*,Auto"), Margin = new Thickness(36, 10) };
    private readonly Button playButton = new() { Classes = { "icon" }, Width = 40, Height = 40, Content = LibraryLook.Icon("IconPlay", 18) };
    private readonly Button backButton = LibraryLook.IconButton("IconRotateCcw", "Back 15 seconds");
    private readonly Button forwardButton = LibraryLook.IconButton("IconRotateCw", "Forward 15 seconds");
    private readonly TextBlock positionText = new() { Classes = { "clock" }, FontSize = 12, MinWidth = 48, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock durationText = new() { Classes = { "clock" }, FontSize = 12, MinWidth = 48, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider slider = new() { Minimum = 0, Maximum = 0.01, VerticalAlignment = VerticalAlignment.Center };
    private readonly PeakWaveform waveform = new() { Height = 32, VerticalAlignment = VerticalAlignment.Center };

    private MeetingRecordingMetadata? recording;
    private IReadOnlyList<MeetingUtterance>? renderedTranscript;
    private readonly Dictionary<Guid, Border> rows = [];
    private Guid? highlighted;
    private TranscriptFollowState follow = new();
    private bool following;
    private bool audioMissing;

    /// <summary>Play was pressed while the audio was still opening: it starts once it is open.</summary>
    private bool playWhenLoaded;
    private string? audioPath;
    private readonly TitleRenameSession rename = new();
    private bool attached;
    private bool updatingSlider;

    /// <summary>The shown recording was the one in progress when last drawn: its end is noticed once.</summary>
    private bool wasLive;

    public RecordingDetailView(AppState state, RecordingPlayer player)
    {
        this.state = state;
        this.player = player;
        Focusable = true;

        // Header: title, or its editor while renaming; then what it is.
        ToolTip.SetTip(title, "Double-click to rename");
        titleEditor.KeyDown += OnEditorKey;
        titleEditor.GotFocus += (_, _) => rename.EditorFocused();
        titleEditor.LostFocus += (_, _) => Dispatcher.UIThread.Post(OnEditorLeft, DispatcherPriority.Background);
        AutomationProperties.SetName(titleEditor, "Recording name");
        exportTranscript.Click += (_, _) =>
        {
            if (recording is { } shown) { _ = RecordingExport.Transcript(this, state, shown); }
        };
        exportAudio.Click += (_, _) =>
        {
            if (recording is { } shown) { _ = RecordingExport.Audio(this, state, shown); }
        };
        exportButton.Flyout = new MenuFlyout { Items = { exportTranscript, exportAudio } };
        AutomationProperties.SetName(exportButton, "Export");
        ToolTip.SetTip(exportButton, "Export transcript or audio");
        // The whole title line takes the double-click, not only the glyphs of the title.
        var naming = new Panel { Background = Brushes.Transparent };
        naming.DoubleTapped += (_, args) =>
        {
            if (args.Source is Visual source && titleEditor.IsVisualAncestorOf(source)) { return; }
            BeginRename();
        };
        naming.Children.Add(title);
        naming.Children.Add(titleEditor);
        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        titleRow.Children.Add(naming);
        Grid.SetColumn(exportButton, 1);
        titleRow.Children.Add(exportButton);
        var header = new StackPanel { Spacing = 4, Margin = new Thickness(44, 28, 44, 12) };
        header.Children.Add(titleRow);
        header.Children.Add(details);
        LucideIcon alert = LibraryLook.Icon("IconTriangleAlert", 15);
        alert.Foreground = StatusBrushes.Attention;
        alert.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(alert, Dock.Left);
        noteStrip.Child = new DockPanel { Children = { alert, note } };
        header.Children.Add(noteStrip);
        header.Children.Add(healthStrip);

        // The transcript, with its placeholder and the way back to the latest words over it.
        scroller.Content = transcript;
        scroller.ScrollChanged += OnScrolled;
        jumpToLatest.Click += (_, _) =>
        {
            follow.ReturnToLatest();
            jumpToLatest.IsVisible = false;
            scroller.ScrollToEnd();
        };
        placeholderDetail.HorizontalAlignment = HorizontalAlignment.Center;
        placeholderDetail.TextAlignment = TextAlignment.Center;
        placeholder.Children.Add(placeholderTitle);
        placeholder.Children.Add(placeholderDetail);
        var body = new Panel();
        body.Children.Add(scroller);
        body.Children.Add(placeholder);
        body.Children.Add(jumpToLatest);

        // The transport: play/pause, where, the slider on its waveform, how long.
        playButton.Click += (_, _) =>
        {
            if (!IsAudioLoaded)
            {
                playWhenLoaded = true;
                return;
            }

            player.Toggle();
            UpdatePlayhead();
        };
        slider.AddHandler(PointerPressedEvent, OnSliderPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        slider.AddHandler(PointerReleasedEvent, (_, _) => EndScrub(), RoutingStrategies.Bubble, handledEventsToo: true);
        slider.AddHandler(PointerCaptureLostEvent, (_, _) => EndScrub(), RoutingStrategies.Bubble, handledEventsToo: true);
        slider.ValueChanged += OnSliderMoved;
        AutomationProperties.SetName(slider, "Position");
        var track = new Panel();
        track.Children.Add(waveform);
        track.Children.Add(slider);
        backButton.Click += (_, _) => Skip(-SkipSeconds);
        forwardButton.Click += (_, _) => Skip(SkipSeconds);
        Grid transport = transportBar;
        transport.Children.Add(backButton);
        Grid.SetColumn(playButton, 1);
        transport.Children.Add(playButton);
        Grid.SetColumn(forwardButton, 2);
        transport.Children.Add(forwardButton);
        positionText.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(positionText, 3);
        transport.Children.Add(positionText);
        track.Margin = new Thickness(12, 0);
        Grid.SetColumn(track, 4);
        transport.Children.Add(track);
        Grid.SetColumn(durationText, 5);
        transport.Children.Add(durationText);

        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        DockPanel.SetDock(transport, Dock.Bottom);
        layout.Children.Add(transport);
        retryButton.Click += (_, _) => state.ResumeTranscription();
        DockPanel.SetDock(retryButton, Dock.Right);
        statusLine.Children.Add(retryButton);
        statusLine.Children.Add(importLine);
        DockPanel.SetDock(statusLine, Dock.Bottom);
        layout.Children.Add(statusLine);
        layout.Children.Add(body);
        Content = layout;

        ticker.Tick += (_, _) => UpdatePlayhead();
        KeyDown += OnKey;
    }

    /// <summary>
    /// Show this recording. Another one than before pauses what was playing and loads this one;
    /// the same one (renamed, transcribed further) only redraws.
    /// </summary>
    public void Show(MeetingRecordingMetadata shown)
    {
        bool another = recording?.Id != shown.Id;
        recording = shown;
        if (another)
        {
            player.Pause();
            scrub.Cancel();
            playWhenLoaded = false;
            rename.End();
            follow = new TranscriptFollowState();
            renderedTranscript = null;
            highlighted = null;
            waveform.Channels = [];
            waveform.Progress = 0;
            wasLive = state.LiveRecordingId == shown.Id;
            LoadPeaks(shown.Id);
        }

        LoadAudio(shown.Id);
        Render();
    }

    /// <summary>Start renaming, as a double-click on the title does (the window's Return and context menu).</summary>
    public void BeginRename()
    {
        if (recording is null || IsLive || !rename.Begin()) { return; }
        titleEditor.Text = recording.Title ?? string.Empty;
        titleEditor.Watermark = RecordingsPlaceholder.DefaultTitle(recording.StartedAt);
        RenderHeader();
        // The caret moves once the editor is laid out and whatever opened it (a double-click, a
        // closing menu, the list's Return) has finished with the focus.
        Dispatcher.UIThread.Post(() =>
        {
            if (!rename.IsOpen) { return; }
            titleEditor.Focus();
            titleEditor.SelectAll();
        }, DispatcherPriority.Background);
    }

    /// <summary>Leaving the recording: playback pauses, the view stops listening.</summary>
    public void Detach()
    {
        player.Pause();
        scrub.Cancel();
        ticker.Stop();
        if (!attached) { return; }
        attached = false;
        state.PropertyChanged -= OnStateChanged;
        state.TranscriptChanged -= OnTranscriptChanged;
        player.Ended -= OnEnded;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (attached) { return; }
        attached = true;
        state.PropertyChanged += OnStateChanged;
        state.TranscriptChanged += OnTranscriptChanged;
        player.Ended += OnEnded;
        Render();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Detach();
    }

    // MARK: - Events from elsewhere

    private void OnStateChanged(object? sender, PropertyChangedEventArgs args) => Dispatcher.UIThread.Post(() =>
    {
        // The meters live in the window's record bar; ten redraws a second here would be waste.
        if (recording is null || args.PropertyName == nameof(AppState.LiveLevels)) { return; }
        // Renamed, or transcribed further: the metadata as listed now. Gone: the window decides.
        if (state.Recordings.FirstOrDefault(listed => listed.Id == recording.Id) is { } current)
        {
            recording = current;
        }

        RefreshAudio();
        Render();
    });

    private void OnTranscriptChanged(Guid id) => Dispatcher.UIThread.Post(() =>
    {
        if (recording?.Id != id) { return; }
        RefreshAudio();
        Render();
    });

    /// <summary>
    /// A finished import swaps its WAV for the compact archive: load the file that is there now.
    /// What is playing carries on from its open file and is swapped once it stops.
    /// </summary>
    private void RefreshAudio()
    {
        if (recording is not { } current || state.RecordingAudioPath(current.Id) == audioPath) { return; }
        if (player.LoadedId == current.Id && player.IsPlaying) { return; }
        LoadAudio(current.Id);
    }

    private void OnEnded(object? sender, Guid id) => Dispatcher.UIThread.Post(() =>
    {
        if (recording?.Id == id) { UpdatePlayhead(); }
    });

    // MARK: - Loading

    private void LoadAudio(Guid id)
    {
        string? path = state.RecordingAudioPath(id);
        audioPath = path;
        audioMissing = path is null;
        if (path is null) { return; }
        // Opening an m4a probes it with ffprobe: off the UI thread.
        _ = Task.Run(() => player.Load(id, path)).ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (recording?.Id != id) { return; }
                if (playWhenLoaded && IsAudioLoaded) { player.Play(); }
                playWhenLoaded = false;
                Render();
            }),
            TaskScheduler.Default);
    }

    private void LoadPeaks(Guid id)
    {
        if (state.RecordingPeaksPath(id) is not { } path) { return; }
        _ = Task.Run(() =>
        {
            try
            {
                return PeakFile.Read(path).Channels;
            }
            catch (PeakFile.Failure)
            {
                return [];
            }
        }).ContinueWith(
            read => Dispatcher.UIThread.Post(() =>
            {
                if (recording?.Id == id) { waveform.Channels = read.Result; }
            }),
            TaskScheduler.Default);
    }

    // MARK: - Rendering

    private bool IsAudioLoaded => recording is { } current && player.LoadedId == current.Id;

    /// <summary>The shown recording is the one in progress: no player, no export, no rename yet.</summary>
    private bool IsLive => recording is { } current && state.LiveRecordingId == current.Id;

    private void Render()
    {
        if (recording is null) { return; }
        bool live = IsLive;
        // Just filed: the waveform is there to read now (the audio follows through RefreshAudio).
        if (wasLive && !live) { LoadPeaks(recording.Id); }
        wasLive = live;
        transportBar.IsVisible = !live;
        RenderHeader();
        RenderTranscript();
        UpdatePlayhead();
    }

    private void RenderHeader()
    {
        if (recording is null) { return; }
        bool live = IsLive;
        title.Text = recording.Title ?? RecordingsPlaceholder.DefaultTitle(recording.StartedAt);
        title.IsVisible = !rename.IsOpen;
        titleEditor.IsVisible = rename.IsOpen;
        DateTimeOffset started = recording.StartedAt.ToLocalTime();
        string length = !live
            ? RecordingTime.Brief(TimeSpan.FromSeconds(recording.Duration))
            : (state.RecordingState == MeetingRecordingState.Paused ? "Paused — " : "Recording — ") + RecordingTime.Clock(state.LiveDuration);
        details.Text = string.Join(
            " · ",
            RecordingsPlaceholder.Kind(LibraryRecording.KindOf(recording)),
            started.ToString("D", CultureInfo.CurrentCulture) + ", " + started.ToString("t", CultureInfo.CurrentCulture),
            length);
        string? warning = live ? null : RecordingsPlaceholder.EndNote(recording.EndReason) ?? RecordingsPlaceholder.DegradedNote(recording);
        note.Text = warning;
        noteStrip.IsVisible = warning is not null;

        exportButton.IsVisible = !live;
        exportTranscript.IsEnabled = state.Transcript(recording.Id).Count > 0;
        exportAudio.IsEnabled = audioPath is not null;
        RenderHealth(live);
    }

    /// <summary>
    /// While recording, one card per side that is not arriving as it should (upstream
    /// CaptureHealthStrip): orange when the recording is missing that side, grey when it is only
    /// something this computer cannot do.
    /// </summary>
    private void RenderHealth(bool live)
    {
        healthStrip.Children.Clear();
        if (live)
        {
            MicrophoneHealth microphone = state.LiveMicrophoneHealth;
            CaptureHealth others = state.LiveCaptureHealth;
            if (microphone.Title is { } microphoneTitle)
            {
                healthStrip.Children.Add(HealthCard(microphoneTitle, microphone.Detail, microphone.MarksRecordingDegraded));
            }

            if (others.Title is { } othersTitle)
            {
                healthStrip.Children.Add(HealthCard(othersTitle, others.Detail, others.MarksRecordingDegraded));
            }
        }

        healthStrip.IsVisible = healthStrip.Children.Count > 0;
    }

    private static Border HealthCard(string heading, string? detail, bool degraded)
    {
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Classes = { "setupTitle" }, Text = heading, TextWrapping = TextWrapping.Wrap });
        if (detail is not null)
        {
            text.Children.Add(new TextBlock { Classes = { "detail" }, Text = detail });
        }

        var glyph = new StateGlyph
        {
            Kind = degraded ? StateGlyphKind.Attention : StateGlyphKind.Info,
            GlyphSize = 15,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 10, 0),
        };
        DockPanel.SetDock(glyph, Dock.Left);
        var card = new Border
        {
            Classes = { "content" },
            Child = new DockPanel { Children = { glyph, text } },
            Padding = new Thickness(14, 10),
            CornerRadius = new CornerRadius(14),
        };
        if (degraded) { card.Background = AttentionWash; }
        AutomationProperties.SetName(card, detail is null ? heading : heading + ". " + detail);
        return card;
    }

    /// <summary>
    /// One honest line under a live transcript about how far behind it is, and nothing when it
    /// is not worth saying (upstream TranscriptStatusLine). Orange only for a stopped
    /// transcription: the audio is safe either way.
    /// </summary>
    private (string Text, bool Paused)? TranscriptStatus(Guid id)
    {
        if (!IsLive && state.TranscribingRecordingId != id) { return null; }
        if (state.IsTranscriptionPaused) { return ("Transcription paused.", true); }
        if (!state.IsEngineReady) { return ("Waiting for the speech model.", false); }
        if (state.DictationStateValue != DictationState.Idle) { return ("Transcription paused while you dictate.", false); }
        double backlog = state.TranscriptBacklogSeconds;
        return backlog >= BacklogVisibleAfter
            ? ($"Transcribing — about {Math.Round(backlog).ToString(CultureInfo.CurrentCulture)} seconds behind.", false)
            : null;
    }

    private void RenderTranscript()
    {
        if (recording is not { } current) { return; }
        IReadOnlyList<MeetingUtterance> utterances = state.Transcript(current.Id);
        RecordingImportActivity activity = state.ImportActivity;
        bool live = IsLive;
        following = activity.Transcribing == current.Id || live || state.TranscribingRecordingId == current.Id;

        bool isDictating = state.DictationStateValue != DictationState.Idle;
        string? line = activity.Transcribing == current.Id || utterances.Count > 0
            ? RecordingsPlaceholder.ImportLine(current.Id, activity, isDictating)
            : null;
        (string Text, bool Paused)? status = line is null ? TranscriptStatus(current.Id) : null;
        importLine.Text = line ?? status?.Text;
        if (status is { Paused: true }) { importLine.Foreground = StatusBrushes.Attention; }
        else { importLine.ClearValue(TextBlock.ForegroundProperty); }
        retryButton.IsVisible = status is { Paused: true };
        statusLine.IsVisible = importLine.Text is not null;

        if (utterances.Count == 0)
        {
            RecordingsPlaceholder shown = live
                ? RecordingsPlaceholder.Listening
                : audioMissing || (!IsAudioLoaded && player.FailedToLoad)
                ? RecordingsPlaceholder.AudioMissing
                : RecordingsPlaceholder.Importing(current.Id, activity)
                    ?? RecordingsPlaceholder.Transcript(current.TranscriptionState);
            placeholderTitle.Text = shown.Title;
            placeholderDetail.Text = shown.Detail;
            placeholder.IsVisible = true;
            scroller.IsVisible = false;
            jumpToLatest.IsVisible = false;
            transcript.Children.Clear();
            rows.Clear();
            renderedTranscript = utterances;
            return;
        }

        placeholder.IsVisible = false;
        scroller.IsVisible = true;
        jumpToLatest.IsVisible = following && !follow.FollowsLatest;
        if (ReferenceEquals(utterances, renderedTranscript)) { return; }
        renderedTranscript = utterances;

        transcript.Children.Clear();
        rows.Clear();
        highlighted = null;
        // Who speaks in an imported file is not something the app knows: no headings there.
        bool speakers = !current.IsImported;
        foreach (TranscriptSection section in TranscriptSection.Make(utterances))
        {
            if (speakers) { transcript.Children.Add(SpeakerHeading(section.Channel)); }
            foreach (MeetingUtterance utterance in section.Utterances)
            {
                Border row = Paragraph(utterance, speakers);
                rows[utterance.Id] = row;
                transcript.Children.Add(row);
            }
        }

        if (following && follow.FollowsLatest)
        {
            // After the new paragraphs are laid out.
            Dispatcher.UIThread.Post(scroller.ScrollToEnd, DispatcherPriority.Background);
        }
    }

    private static Control SpeakerHeading(MeetingChannel channel)
    {
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 16, 8, 4) };
        var mark = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (channel == MeetingChannel.Microphone) { mark.Background = StatusBrushes.Processing; }
        else { ThemeTokens.Bind(mark, Border.BackgroundProperty, ThemeTokens.Label3); }
        heading.Children.Add(mark);
        heading.Children.Add(new TextBlock
        {
            Classes = { "small" },
            Text = MeetingTranscriptFormatter.Name(channel),
            FontWeight = FontWeight.Medium,
        });
        return heading;
    }

    private Border Paragraph(MeetingUtterance utterance, bool speakers)
    {
        var stamp = new Button
        {
            Classes = { "quiet" },
            Content = new TextBlock { Classes = { "small", "tnum" }, Text = RecordingTime.Clock(TimeSpan.FromSeconds(utterance.Start)) },
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(6, 0),
            MinHeight = 22,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 0, 0),
        };
        ToolTip.SetTip(stamp, "Play from " + RecordingTime.Clock(TimeSpan.FromSeconds(utterance.Start)));
        AutomationProperties.SetName(stamp, "Play from " + RecordingTime.Spoken(TimeSpan.FromSeconds(utterance.Start)));
        stamp.Click += (_, _) => PlayFrom(utterance.Start);

        Control text = utterance.IsFailed
            ? new TextBlock
            {
                Text = "Couldn't transcribe this part",
                FontFamily = LibraryLook.Serif,
                FontStyle = FontStyle.Italic,
                Foreground = StatusBrushes.Attention,
                FontSize = 19,
            }
            : Words(utterance.Text);
        string said = utterance.IsFailed ? "Couldn't transcribe this part" : utterance.Text;
        AutomationProperties.SetName(text, speakers ? MeetingTranscriptFormatter.Name(utterance.Channel) + ": " + said : said);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("56,*") };
        grid.Children.Add(stamp);
        text.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return new Border
        {
            Child = grid,
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(10),
        };
    }

    /// <summary>A paragraph of what was said: the serif, 19 on 28, selectable.</summary>
    private static SelectableTextBlock Words(string text)
    {
        var words = new SelectableTextBlock
        {
            Text = text,
            FontFamily = LibraryLook.Serif,
            FontSize = 19,
            LineHeight = 28,
            TextWrapping = TextWrapping.Wrap,
        };
        ThemeTokens.Bind(words, TextBlock.ForegroundProperty, ThemeTokens.Label);
        return words;
    }

    /// <summary>The playhead, ten times a second while something moves: slider, clocks, waveform, paragraph.</summary>
    private void UpdatePlayhead()
    {
        bool loaded = IsAudioLoaded;
        bool playing = loaded && player.IsPlaying;
        double duration = loaded ? player.Duration.TotalSeconds : recording?.Duration ?? 0;
        double position = scrub.Displayed(loaded ? player.Position.TotalSeconds : 0);

        updatingSlider = true;
        try
        {
            slider.Maximum = Math.Max(duration, 0.01);
            slider.Value = Math.Clamp(position, 0, slider.Maximum);
        }
        finally
        {
            updatingSlider = false;
        }

        slider.IsEnabled = loaded && duration > 0;
        backButton.IsEnabled = slider.IsEnabled;
        forwardButton.IsEnabled = slider.IsEnabled;
        // Enabled while the audio is still opening (ffprobe), so the control is there at once;
        // a press meanwhile plays once it is open.
        bool failed = !loaded && (audioMissing || player.FailedToLoad);
        playButton.IsEnabled = loaded ? duration > 0 : !failed;
        ToolTip.SetTip(playButton, failed ? "Couldn't open this recording's audio" : null);
        ((LucideIcon)playButton.Content!).Data = LibraryLook.Geometry(playing ? "IconPause" : "IconPlay");
        AutomationProperties.SetName(playButton, playing ? "Pause" : "Play");
        positionText.Text = RecordingTime.Clock(TimeSpan.FromSeconds(position));
        durationText.Text = RecordingTime.Clock(TimeSpan.FromSeconds(duration));
        waveform.Progress = duration > 0 ? position / duration : 0;
        Highlight(playing || position > 0 ? TranscriptPlayback.Current(renderedTranscript ?? [], position) : null);

        bool moving = playing || scrub.IsScrubbing;
        if (moving && !ticker.IsEnabled) { ticker.Start(); }
        else if (!moving && ticker.IsEnabled) { ticker.Stop(); }
    }

    private void Highlight(Guid? current)
    {
        if (current == highlighted) { return; }
        if (highlighted is { } previous && rows.TryGetValue(previous, out Border? old)) { old.Background = null; }
        if (current is { } next && rows.TryGetValue(next, out Border? row)) { row.Background = ThemeTokens.BrushOf(row, "Fill"); }
        highlighted = current;
    }

    // MARK: - Playing

    private void PlayFrom(double seconds)
    {
        if (!IsAudioLoaded) { return; }
        player.Seek(TimeSpan.FromSeconds(seconds));
        player.Play();
        UpdatePlayhead();
    }

    private void Skip(double seconds)
    {
        if (!IsAudioLoaded) { return; }
        double end = player.Duration.TotalSeconds;
        player.Seek(TimeSpan.FromSeconds(Math.Clamp(player.Position.TotalSeconds + seconds, 0, end)));
        UpdatePlayhead();
    }

    private void OnSliderPressed(object? sender, PointerPressedEventArgs args)
    {
        if (!IsAudioLoaded) { return; }
        if (scrub.Begin(player.IsPlaying, slider.Value)) { player.Pause(); }
        UpdatePlayhead();
    }

    private void OnSliderMoved(object? sender, RangeBaseValueChangedEventArgs args)
    {
        if (updatingSlider || !IsAudioLoaded) { return; }
        if (scrub.IsScrubbing)
        {
            scrub.Move(args.NewValue);
            UpdatePlayhead();
            return;
        }

        // The keyboard: a step is a seek.
        player.Seek(TimeSpan.FromSeconds(args.NewValue));
        UpdatePlayhead();
    }

    private void EndScrub()
    {
        if (scrub.End() is not { } end) { return; }
        player.Seek(TimeSpan.FromSeconds(end.SeekTo));
        if (end.Resume) { player.Play(); }
        UpdatePlayhead();
    }

    private void OnKey(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Space || rename.IsOpen || args.Source is TextBox || !IsAudioLoaded) { return; }
        player.Toggle();
        UpdatePlayhead();
        args.Handled = true;
    }

    // MARK: - Following the latest words

    private void OnScrolled(object? sender, ScrollChangedEventArgs args)
    {
        // Only the person moves the offset by hand; new words grow the extent, not the offset.
        if (!following || args.OffsetDelta.Y == 0) { return; }
        follow.UserScrolled(scroller.Extent.Height - scroller.Offset.Y - scroller.Viewport.Height);
        jumpToLatest.IsVisible = !follow.FollowsLatest;
    }

    // MARK: - Renaming

    private void OnEditorKey(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter)
        {
            CommitRename();
            args.Handled = true;
        }
        else if (args.Key == Key.Escape)
        {
            if (rename.End())
            {
                RenderHeader();
                Focus();
            }

            args.Handled = true;
        }
    }

    /// <summary>
    /// Settled after the editor lost the caret: gone elsewhere in this window commits; gone to a
    /// popup, another window or nowhere leaves the rename open.
    /// </summary>
    private void OnEditorLeft()
    {
        if (titleEditor.IsFocused) { return; }
        IInputElement? now = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        bool elsewhereInWindow = now is Visual visual
            && visual.GetVisualRoot() == this.GetVisualRoot()
            && !titleEditor.IsVisualAncestorOf(visual);
        if (rename.FocusLeft(elsewhereInWindow)) { Save(); }
    }

    private void CommitRename()
    {
        if (!rename.End()) { return; }
        Save();
        Focus();
    }

    private void Save()
    {
        if (recording is null) { return; }
        state.RenameRecording(recording.Id, titleEditor.Text);
        RenderHeader();
    }
}
