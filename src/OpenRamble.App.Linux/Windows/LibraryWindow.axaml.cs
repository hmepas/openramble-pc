using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DictationCore;
using OpenRamble.AppCore;

namespace OpenRamble.App.Linux;

/// <summary>
/// The Library: every dictation and recording in one list by day, the selected one beside it
/// (upstream RecordingsWindow.swift and RecordingsList.swift).
/// </summary>
/// <remarks>
/// <para>
/// Rows are built by hand, as the Settings history rows were: day headers are rows that cannot be
/// selected, and each row carries its own context menu. The list is rebuilt only when what it is
/// built from changes - the history, the recordings, the scope or the search; an import's
/// progress rewrites the one status line it touches.
/// </para>
/// <para>
/// The detail pane is whatever <see cref="CreateDetail"/> makes for the selection; the window owns
/// the selection rules and the one player the panes share, and nothing else of the detail.
/// </para>
/// </remarks>
public sealed partial class LibraryWindow : Window, IDictationSink
{
    private const string SerifFamily = "Georgia, Noto Serif, DejaVu Serif, Liberation Serif, serif";

    /// <summary>What the file picker offers (upstream: any audio or movie type).</summary>
    private static readonly string[] ImportPatterns =
    [
        "*.m4a", "*.mp3", "*.wav", "*.aac", "*.flac", "*.ogg", "*.oga", "*.opus", "*.wma", "*.aif", "*.aiff", "*.caf", "*.amr", "*.webm",
        "*.mp4", "*.m4v", "*.mov", "*.mkv", "*.avi", "*.wmv", "*.mpg", "*.mpeg", "*.3gp",
    ];

    private static readonly LibraryScope[] Scopes = [LibraryScope.All, LibraryScope.Dictations, LibraryScope.Recordings];

    private readonly AppState state;

    /// <summary>The one player every detail pane plays through; released when the window closes.</summary>
    private readonly RecordingPlayer player = new();

    /// <summary>The items the list shows, in its order; headers excluded.</summary>
    private List<LibraryItem> listed = [];

    /// <summary>Each recording row's status line, rewritten in place as an import advances.</summary>
    private readonly Dictionary<Guid, TextBlock> statusLines = [];

    /// <summary>What the list was last built from, so a property change that touched none of it is free.</summary>
    private (IReadOnlyList<HistoryEntry> History, IReadOnlyList<MeetingRecordingMetadata> Recordings, LibraryScope Scope, string Query)? rendered;

    /// <summary>The ids selected, kept across rebuilds of the list.</summary>
    private HashSet<Guid> selection = [];

    /// <summary>The item the detail pane was made for: a new pane only when this changes.</summary>
    private (Guid Id, bool IsRecording)? detailFor;

    /// <summary>Guards the list's SelectionChanged against what a rebuild does to it.</summary>
    private bool updating;

    public LibraryWindow(AppState state)
    {
        this.state = state;
        InitializeComponent();
        Icon = TrayIconArt.WindowMark();

        ScopePicker.ItemsSource = new[] { "All", "Dictations", "Recordings" };
        ScopePicker.SelectedIndex = 0;
        ScopePicker.SelectionChanged += (_, _) => RenderList();
        SearchBox.TextChanged += (_, _) => RenderList();
        ItemList.SelectionChanged += OnListSelectionChanged;
        ItemList.KeyDown += OnListKeyDown;

        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropOverlay.IsVisible = false);
        AddHandler(DragDrop.DropEvent, OnDrop);

        state.PropertyChanged += OnStateChanged;
        state.TranscriptChanged += OnTranscriptChanged;
        Closed += (_, _) =>
        {
            state.PropertyChanged -= OnStateChanged;
            state.TranscriptChanged -= OnTranscriptChanged;
            DetachDetail();
            player.Dispose();
        };

        state.ReloadRecordings();
        RenderList();
        // What is happening now before what merely came last: the file whose words are arriving.
        Guid? first = state.ImportActivity.Transcribing is { } transcribing && listed.Any(item => item.Id == transcribing)
            ? transcribing
            : listed.FirstOrDefault()?.Id;
        if (first is { } id) { Select([id]); }
        RenderToolbar();
    }

    /// <summary>Dictation over the Library goes into the search box when it has the caret.</summary>
    public bool TryInsert(string text)
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not TextBox box) { return false; }
        if (text == "\n" && !box.AcceptsReturn) { return true; }

        DictationSinkTextBox.Insert(box, text);
        return true;
    }

    // MARK: - Detail seam

    /// <summary>
    /// The detail pane for one selected item, or the window's placeholder when there is none (no
    /// selection, several, or nothing listed).
    /// </summary>
    /// <remarks>
    /// Made again only when the selected item changes, not when its data does: a pane follows its
    /// item through <see cref="AppState"/> itself, so a player keeps playing through a rename.
    /// </remarks>
    private Control CreateDetail(LibraryItem? selected) => selected switch
    {
        LibraryItem.Dictation dictation => Shown(new DictationDetailView(state, player), view => view.Show(dictation.Entry)),
        LibraryItem.Recording recording when Metadata(recording.Value) is { } metadata =>
            Shown(new RecordingDetailView(state, player), view => view.Show(metadata)),
        _ => PlaceholderView(Placeholder()),
    };

    private static T Shown<T>(T view, Action<T> show)
    {
        show(view);
        return view;
    }

    private MeetingRecordingMetadata? Metadata(ILibraryRecording recording) =>
        recording is LibraryRecording { Metadata: { } metadata }
            ? metadata
            : state.Recordings.FirstOrDefault(stored => stored.Id == recording.Id);

    /// <summary>The pane being replaced lets go of the player and of the state's events.</summary>
    private void DetachDetail()
    {
        switch (Detail.Content)
        {
            case DictationDetailView dictation:
                dictation.Detach();
                break;
            case RecordingDetailView recording:
                recording.Detach();
                break;
        }
    }

    private RecordingsPlaceholder Placeholder()
    {
        if (state.History.Count == 0 && state.Recordings.Count == 0) { return RecordingsPlaceholder.EmptyLibrary; }
        return listed.Count == 0 ? RecordingsPlaceholder.NothingFound : RecordingsPlaceholder.NothingSelected;
    }

    private static Control PlaceholderView(RecordingsPlaceholder placeholder) => new StackPanel
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        MaxWidth = 360,
        Spacing = 6,
        Children =
        {
            new TextBlock
            {
                Text = placeholder.Title,
                FontSize = 17,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
            new TextBlock
            {
                Text = placeholder.Detail,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
            },
        },
    };

    // MARK: - Live updates

    private void OnStateChanged(object? sender, PropertyChangedEventArgs args) =>
        Dispatcher.UIThread.Post(() =>
        {
            switch (args.PropertyName)
            {
                case nameof(AppState.LastFinishedRecordingId):
                    // The file just brought in is what the person came to see.
                    state.ReloadRecordings();
                    RenderList();
                    if (state.LastFinishedRecordingId is { } id && listed.Any(item => item.Id == id)) { Select([id]); }
                    break;
                case nameof(AppState.ImportActivity):
                case nameof(AppState.DictationStateValue):
                    RenderStatusLines();
                    break;
                default:
                    RenderList();
                    break;
            }

            RenderToolbar();
        });

    /// <summary>A transcript grew: the search reads transcripts, and Copy depends on there being words.</summary>
    private void OnTranscriptChanged(Guid id) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (SearchBox.Text?.Trim().Length > 0) { RenderList(force: true); }
            RenderToolbar();
        });

    // MARK: - List

    private LibraryScope Scope => ScopePicker.SelectedIndex is >= 0 and < 3 ? Scopes[ScopePicker.SelectedIndex] : LibraryScope.All;

    private void RenderList(bool force = false)
    {
        IReadOnlyList<HistoryEntry> history = state.History;
        IReadOnlyList<MeetingRecordingMetadata> recordings = state.Recordings;
        string query = SearchBox.Text ?? string.Empty;
        var inputs = (history, recordings, Scope, query);
        if (!force && rendered is { } last
            && ReferenceEquals(last.History, history)
            && ReferenceEquals(last.Recordings, recordings)
            && last.Scope == inputs.Scope
            && last.Query == query)
        {
            return;
        }

        rendered = inputs;
        IReadOnlyList<LibraryDay> days = state.LibraryDays(Scope, query);
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);

        updating = true;
        try
        {
            var rows = new List<ListBoxItem>();
            listed = [];
            statusLines.Clear();
            foreach (LibraryDay day in days)
            {
                rows.Add(DayRow(day.Title(today)));
                foreach (LibraryItem item in day.Items)
                {
                    listed.Add(item);
                    rows.Add(item switch
                    {
                        LibraryItem.Dictation dictation => DictationRow(dictation),
                        LibraryItem.Recording recording => RecordingRow(recording, day.NeedsSeconds(recording.Value)),
                        _ => throw new InvalidOperationException(),
                    });
                }
            }

            ItemList.ItemsSource = rows;
            // What is selected has to be something the list still shows; nothing left - the first.
            var available = listed.Select(item => item.Id).ToHashSet();
            selection.IntersectWith(available);
            if (selection.Count == 0 && listed.Count > 0) { selection = [listed[0].Id]; }
            ApplySelectionToList();
        }
        finally
        {
            updating = false;
        }

        RenderStatusLines();
        RenderDetail();
        RenderToolbar();
    }

    private static ListBoxItem DayRow(string title) => new()
    {
        Classes = { "day" },
        IsEnabled = false,
        Focusable = false,
        Content = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = Brushes.Gray },
    };

    private ListBoxItem DictationRow(LibraryItem.Dictation item)
    {
        HistoryEntry entry = item.Entry;
        bool hasWords = entry.Text.Length > 0;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        var words = new TextBlock
        {
            Text = hasWords ? FirstWords(entry.Text) : "Nothing recognized",
            FontFamily = new FontFamily(SerifFamily),
            FontSize = 14,
            FontStyle = hasWords ? FontStyle.Normal : FontStyle.Italic,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Only the wordless row is grey. Assigning null instead would not mean "inherit": it would
        // set no brush at all, and the words would be drawn invisible.
        if (!hasWords)
        {
            words.Foreground = Brushes.Gray;
        }
        grid.Children.Add(words);
        var time = new TextBlock
        {
            Classes = { "secondary" },
            Text = entry.Date.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(time, 1);
        grid.Children.Add(time);
        if (entry.IsKept)
        {
            var star = new TextBlock
            {
                Text = "★",
                Foreground = StatusBrushes.Attention,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(star, "Kept");
            Grid.SetColumn(star, 2);
            grid.Children.Add(star);
        }

        var copy = new MenuItem { Header = "Copy", IsEnabled = hasWords };
        copy.Click += (_, _) => state.CopyHistoryEntry(entry);
        var keep = new MenuItem { Header = entry.IsKept ? "Stop keeping this one" : "Keep this one" };
        keep.Click += (_, _) => state.SetHistoryEntryKept(!entry.IsKept, entry);
        var delete = new MenuItem { Header = "Delete" };
        delete.Click += (_, _) => _ = DeleteItems([item]);

        var row = new ListBoxItem
        {
            Content = grid,
            Tag = item,
            ContextMenu = new ContextMenu { ItemsSource = new Control[] { copy, keep, new Separator(), delete } },
        };
        AutomationProperties.SetName(row, hasWords ? entry.Text : "Nothing recognized");
        return row;
    }

    private ListBoxItem RecordingRow(LibraryItem.Recording item, bool showsSeconds)
    {
        ILibraryRecording recording = item.Value;
        string kind = RecordingsPlaceholder.Kind(recording.Kind);
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        top.Children.Add(new TextBlock
        {
            Text = recording.Title ?? kind,
            FontFamily = new FontFamily(SerifFamily),
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var duration = new TextBlock { Classes = { "secondary" }, Text = Clock(recording.Duration), Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(duration, 1);
        top.Children.Add(duration);

        string start = TimeZoneInfo.ConvertTime(recording.StartedAt, TimeZoneInfo.Local)
            .ToString(showsSeconds ? "HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture);
        var status = new TextBlock { Classes = { "secondary" }, Text = start, Tag = start, TextTrimming = TextTrimming.CharacterEllipsis };
        statusLines[recording.Id] = status;

        var stack = new StackPanel { Spacing = 2, Children = { top, status } };
        if (recording is LibraryRecording { Metadata: { } metadata }
            && (RecordingsPlaceholder.EndNote(metadata.EndReason) ?? RecordingsPlaceholder.DegradedNote(metadata)) is { } warning)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "⚠ " + warning,
                FontSize = 11,
                Foreground = StatusBrushes.Attention,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
            });
        }

        var rename = new MenuItem { Header = "Rename…" };
        rename.Click += (_, _) => Rename(recording.Id);
        var trash = new MenuItem { Header = "Move to Trash" };
        trash.Click += (_, _) => _ = DeleteItems([item]);

        var row = new ListBoxItem
        {
            Content = stack,
            Tag = item,
            ContextMenu = new ContextMenu { ItemsSource = new Control[] { rename, new Separator(), trash } },
        };
        ToolTip.SetTip(row, TimeZoneInfo.ConvertTime(recording.StartedAt, TimeZoneInfo.Local).ToString("F", CultureInfo.CurrentCulture));
        AutomationProperties.SetName(row, recording.Title ?? RecordingsPlaceholder.DefaultTitle(recording.StartedAt));
        AutomationProperties.SetHelpText(row, kind);
        return row;
    }

    /// <summary>Import progress rewrites only the status lines; the start time comes back when it is done.</summary>
    private void RenderStatusLines()
    {
        RecordingImportActivity activity = state.ImportActivity;
        bool isDictating = state.DictationStateValue != DictationState.Idle;
        foreach ((Guid id, TextBlock line) in statusLines)
        {
            line.Text = RecordingsPlaceholder.ImportLine(id, activity, isDictating) ?? (string?)line.Tag;
        }
    }

    private static string FirstWords(string text)
    {
        string flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 120 ? flat : flat[..120];
    }

    private static string Clock(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    // MARK: - Selection

    private LibraryItem? Primary => selection.Count == 1 ? listed.FirstOrDefault(item => item.Id == selection.First()) : null;

    private List<LibraryItem> SelectedItems => [.. listed.Where(item => selection.Contains(item.Id))];

    private void Select(IEnumerable<Guid> ids)
    {
        selection = [.. ids];
        updating = true;
        try
        {
            ApplySelectionToList();
        }
        finally
        {
            updating = false;
        }

        RenderDetail();
        RenderToolbar();
    }

    private void ApplySelectionToList()
    {
        ItemList.SelectedItems?.Clear();
        if (ItemList.ItemsSource is not IEnumerable<ListBoxItem> rows) { return; }
        ListBoxItem? last = null;
        foreach (ListBoxItem row in rows)
        {
            if (row.Tag is LibraryItem item && selection.Contains(item.Id))
            {
                ItemList.SelectedItems?.Add(row);
                last = row;
            }
        }

        if (last is not null) { ItemList.ScrollIntoView(last); }
    }

    private void OnListSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (updating) { return; }
        selection = [.. (ItemList.SelectedItems?.OfType<ListBoxItem>() ?? []).Select(row => row.Tag).OfType<LibraryItem>().Select(item => item.Id)];
        RenderDetail();
        RenderToolbar();
    }

    private void RenderDetail()
    {
        LibraryItem? primary = Primary;
        (Guid, bool)? key = primary is null ? null : (primary.Id, primary is LibraryItem.Recording);
        // The placeholder depends on the list too (nothing found / nothing selected): always redone.
        if (key is not null && key == detailFor && Detail.Content is not null) { return; }
        detailFor = key;
        DetachDetail();
        Detail.Content = CreateDetail(primary);
    }

    // MARK: - Toolbar

    private void RenderToolbar()
    {
        RecordingImportActivity activity = state.ImportActivity;
        ImportButton.IsEnabled = state.CanImport;
        ImportSpinner.IsVisible = activity.Copying > 0;
        ImportLabel.IsVisible = activity.Copying == 0;

        LibraryItem? primary = Primary;
        RevealButton.IsEnabled = primary is not null;
        ToolTip.SetTip(RevealButton, primary is LibraryItem.Dictation ? "Show in folder" : "Open recording's folder");

        CopyButton.IsEnabled = primary switch
        {
            LibraryItem.Dictation dictation => dictation.Entry.Text.Length > 0,
            LibraryItem.Recording recording => state.Transcript(recording.Id).Count > 0,
            _ => false,
        };
        string copyTip = primary is LibraryItem.Dictation ? "Copy this dictation" : "Copy all text transcribed so far";
        ToolTip.SetTip(CopyButton, copyTip);
        AutomationProperties.SetName(CopyButton, copyTip);

        TrashButton.IsVisible = selection.Count > 0;
        string trashTip = DeleteTitle();
        ToolTip.SetTip(TrashButton, trashTip);
        AutomationProperties.SetName(TrashButton, trashTip);
    }

    private string DeleteTitle()
    {
        List<LibraryItem> items = SelectedItems;
        int dictations = items.Count(item => item is LibraryItem.Dictation);
        if (dictations == items.Count) { return items.Count == 1 ? "Delete this dictation" : "Delete selected dictations"; }
        if (dictations == 0) { return items.Count == 1 ? "Move recording to Trash" : "Move selected recordings to Trash"; }
        return "Delete selected items";
    }

    private void OnImport(object? sender, RoutedEventArgs args) => _ = ChooseFiles();

    private async Task ChooseFiles()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Audio or Video",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Audio and video") { Patterns = ImportPatterns }],
        }).ConfigureAwait(true);
        Import(files.Select(file => file.TryGetLocalPath()));
    }

    private void Import(IEnumerable<string?> paths)
    {
        string[] local = [.. paths.OfType<string>()];
        if (local.Length > 0) { _ = state.ImportFiles(local); }
    }

    private void OnReveal(object? sender, RoutedEventArgs args)
    {
        switch (Primary)
        {
            case LibraryItem.Dictation:
                state.RevealHistoryAudio();
                break;
            case LibraryItem.Recording recording:
                state.RevealRecording(recording.Id);
                break;
        }
    }

    private void OnCopy(object? sender, RoutedEventArgs args)
    {
        switch (Primary)
        {
            case LibraryItem.Dictation dictation:
                state.CopyHistoryEntry(dictation.Entry);
                break;
            case LibraryItem.Recording recording:
                state.CopyTranscript(recording.Id);
                break;
        }
    }

    private void OnTrash(object? sender, RoutedEventArgs args) => _ = DeleteItems(SelectedItems);

    // MARK: - Deleting and renaming

    /// <summary>
    /// Dictations are deleted for good, audio and all; recordings go to the Trash - and where this
    /// desktop has none, only after the person agreed to lose them.
    /// </summary>
    private async Task DeleteItems(IReadOnlyCollection<LibraryItem> items)
    {
        Guid[] dictations = [.. items.OfType<LibraryItem.Dictation>().Select(item => item.Id)];
        Guid[] recordings = [.. items.OfType<LibraryItem.Recording>().Select(item => item.Id)];
        if (dictations.Length > 0) { state.DeleteDictations(dictations); }
        if (recordings.Length == 0) { return; }
        if (state.TrashRecordings(recordings) != RecordingTrashOutcome.NeedsPermanentDeletion) { return; }

        bool confirmed = await Confirm.Ask(
            this,
            recordings.Length == 1 ? "Delete this recording?" : "Delete these recordings?",
            "This desktop has no Trash to move it to, so it is deleted from this PC for good, with its audio and transcript.",
            "Delete").ConfigureAwait(true);
        if (confirmed) { state.DeleteRecordingsPermanently(recordings); }
    }

    /// <summary>Renaming happens in the detail pane's title: the recording is selected, then its title opens.</summary>
    private void Rename(Guid id)
    {
        Select([id]);
        // A pane made just now is not in the window yet; its title can take the caret once it is.
        Dispatcher.UIThread.Post(
            () => (Detail.Content as RecordingDetailView)?.BeginRename(),
            DispatcherPriority.Loaded);
    }

    private void OnListKeyDown(object? sender, KeyEventArgs args)
    {
        switch (args.Key)
        {
            case Key.Delete when selection.Count > 0:
                _ = DeleteItems(SelectedItems);
                args.Handled = true;
                break;
            case Key.Enter when Primary is LibraryItem.Recording recording:
                Rename(recording.Id);
                args.Handled = true;
                break;
        }
    }

    // MARK: - Drag and drop

    private void OnDragEnter(object? sender, DragEventArgs args)
    {
        bool files = args.DataTransfer.Contains(DataFormat.File);
        args.DragEffects = files ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.IsVisible = files && state.CanImport;
    }

    /// <summary>
    /// Leave and enter bubble up from every child the pointer crosses; the overlay follows the
    /// last over, so crossing from one child to the next does not leave it hidden.
    /// </summary>
    private void OnDragOver(object? sender, DragEventArgs args)
    {
        bool accepts = args.DataTransfer.Contains(DataFormat.File) && state.CanImport;
        args.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.IsVisible = accepts;
    }

    private void OnDrop(object? sender, DragEventArgs args)
    {
        DropOverlay.IsVisible = false;
        if (!state.CanImport) { return; }
        Import((args.DataTransfer.TryGetFiles() ?? []).Select(item => item.TryGetLocalPath()));
    }
}
