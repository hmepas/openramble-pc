using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DictationCore;
using OpenRamble.AppCore;
using OpenRamble.AppCore.Tests;

namespace OpenRamble.App.Linux.UiTests;

/// <summary>
/// The Library's list over a real <see cref="AppState"/>: what changes beside it (the history's
/// limit, a new take, a renamed recording) must not move what the person is looking at, and a
/// search starts from the top.
/// </summary>
public sealed class LibraryWindowTests : IDisposable
{
    private readonly AppHarness harness = new();
    private LibraryWindow? window;

    public void Dispose()
    {
        window?.Close();
        harness.Dispose();
    }

    /// <summary>Dictations of today and yesterday, and two recordings, on disk before the state reads them.</summary>
    private void Seed(int dictations = 8)
    {
        var history = new DictationHistoryStore(Path.Combine(harness.Paths.Support(), "History"));
        DateTimeOffset now = DateTimeOffset.Now;
        for (int index = 0; index < dictations; index++)
        {
            _ = history.Record($"take number {index} with a few words in it", null, 50, now.AddMinutes(-index - (index >= dictations / 2 ? 24 * 60 : 0)));
        }

        var recordings = new MeetingStore(harness.Paths.Recordings(), harness.RecordingTrasher);
        for (int index = 0; index < 2; index++)
        {
            recordings.Write(MeetingRecordingMetadata.Create(
                startedAt: now.AddMinutes(-30 - index),
                duration: 12,
                title: $"Call {index}",
                systemAudio: new SystemAudioSummary(wasRequested: false),
                endReason: MeetingEndReason.StoppedByUser));
        }
    }

    private LibraryWindow Open(AppState state)
    {
        state.HistoryLimit = 50;
        window = new LibraryWindow(state);
        window.Show();
        Ui.Settle();
        return window;
    }

    private ListBox List => window!.FindControl<ListBox>("ItemList")!;

    /// <summary>Where the list's first row - a day header - sits, in the window.</summary>
    private double FirstHeaderY()
    {
        Ui.Settle();
        Control first = List.ContainerFromIndex(0) ?? throw new InvalidOperationException("no first row");
        Assert.Contains("dayHeader", first.Classes);
        return first.TranslatePoint(default, window!)!.Value.Y;
    }

    private Vector Offset => List.Scroll?.Offset ?? default;

    [AvaloniaFact]
    public void OpeningWithItemsDoesNotThrow()
    {
        Seed();
        AppState state = harness.MakeState();

        Open(state);

        Assert.True(List.ItemCount > 0);
        Assert.Single(List.SelectedItems!.Cast<object>());
    }

    [AvaloniaFact]
    public void ChangingTheHistoryLimitLeavesTheListWhereItWas()
    {
        Seed();
        AppState state = harness.MakeState();
        Open(state);
        double header = FirstHeaderY();
        Vector offset = Offset;

        foreach (int limit in new[] { 20, 10, 20, 30, 40 })
        {
            state.HistoryLimit = limit;
            Assert.Equal(header, FirstHeaderY(), 0.5);
            Assert.Equal(offset, Offset);
        }
    }

    [AvaloniaFact]
    public void ARenamedRecordingLeavesTheListWhereItWas()
    {
        Seed();
        AppState state = harness.MakeState();
        Open(state);
        double header = FirstHeaderY();
        Vector offset = Offset;
        Guid id = state.Recordings[0].Id;

        foreach (string title in new[] { "One", "Two", "Three" })
        {
            state.RenameRecording(id, title);
            Assert.Equal(header, FirstHeaderY(), 0.5);
            Assert.Equal(offset, Offset);
        }
    }

    [AvaloniaFact]
    public async Task ANewTakeLeavesTheListWhereItWas()
    {
        Seed();
        harness.InstallModelMarker();
        AppState state = harness.MakeState();
        await state.RefreshModelState();
        Open(state);
        double header = FirstHeaderY();
        Vector offset = Offset;

        for (int take = 0; take < 3; take++)
        {
            int before = state.History.Count;
            harness.Monitor.OnPress!();
            await Ui.WaitFor("recording has started", () => state.DictationStateValue == DictationState.Listening);
            harness.Monitor.OnRelease!();
            await Ui.WaitFor("the take was archived", () => state.History.Count > before);
            await Ui.WaitFor("dictation ended", () => state.DictationStateValue == DictationState.Idle);

            Assert.Equal(header, FirstHeaderY(), 0.5);
            Assert.Equal(offset, Offset);
        }
    }

    [AvaloniaFact]
    public void TypingASearchStartsTheListFromItsTop()
    {
        Seed(dictations: 30);
        AppState state = harness.MakeState();
        Open(state);
        double header = FirstHeaderY();
        TextBox search = window!.FindControl<TextBox>("SearchBox")!;

        foreach (string query in new[] { "t", "ta", "tak", "take", "take n" })
        {
            search.Text = query;
            Assert.Equal(header, FirstHeaderY(), 0.5);
            Assert.Equal(default, Offset);
        }

        search.Text = string.Empty;
        Assert.Equal(header, FirstHeaderY(), 0.5);
        Assert.Equal(default, Offset);
    }

    // MARK: - Recording, renaming, deleting

    /// <summary>The list's row containers from the top, realized ones only.</summary>
    private List<ListBoxItem> Rows()
    {
        Ui.Settle();
        return [.. Enumerable.Range(0, List.ItemCount).Select(index => List.ContainerFromIndex(index)).OfType<ListBoxItem>()];
    }

    private static string? Name(Control container) => AutomationProperties.GetName(container);

    private double Y(Control control) => control.TranslatePoint(default, window!)!.Value.Y;

    private ListBoxItem Row(string name) => Rows().First(row => Name(row) == name);

    private Point Centre(Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window!)!.Value;

    private void Click(Control control, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Point at = Centre(control);
        window!.MouseDown(at, MouseButton.Left, modifiers);
        window!.MouseUp(at, MouseButton.Left, modifiers);
        Ui.Settle();
    }

    private void Press(Key key, PhysicalKey physical)
    {
        window!.KeyPress(key, RawInputModifiers.None, physical, null);
        window!.KeyRelease(key, RawInputModifiers.None, physical, null);
        Ui.Settle();
    }

    private void Type(string text)
    {
        window!.KeyTextInput(text);
        Ui.Settle();
    }

    private List<string?> SelectedNames() => [.. Rows().Where(row => row.IsSelected).Select(Name)];

    /// <summary>Each realized row below the live one, by name, where it sits in the window.</summary>
    private List<(string? Name, double Y)> Layout() =>
        [.. Rows().Where(row => Name(row) is not ("Recording" or "Recording, paused")).Select(row => (Name(row), Y(row)))];

    [AvaloniaFact]
    public async Task StartingAndStoppingARecordingMovesTheListOnlyByTheLiveRow()
    {
        Seed();
        AppState state = harness.MakeState();
        Open(state);
        double header = FirstHeaderY();
        List<(string? Name, double Y)> before = Layout();
        List<(string? Name, double Y)>? recordingLayout = null, stoppedLayout = null;

        for (int cycle = 0; cycle < 3; cycle++)
        {
            await state.StartRecording(includeSystemAudio: false);
            await Ui.WaitFor("the live row", () => Rows().FirstOrDefault() is { } first && Name(first) == "Recording");

            ListBoxItem live = Rows()[0];
            Assert.Equal(header, Y(live), 0.5);
            Assert.Equal(["Recording"], SelectedNames());
            Assert.Equal(default, Offset);
            double pushed = live.Bounds.Height;
            List<(string? Name, double Y)> during = Layout();
            // The rows still realized - the last ones may have left the viewport - each pushed down by the live row alone.
            foreach (((string? name, double was), (string? nowName, double now)) in before.Zip(during))
            {
                Assert.Equal(name, nowName);
                Assert.Equal(was + pushed, now, 0.5);
            }

            // Each cycle the same: the live row and the first header below it where they were the first time.
            if (recordingLayout is null) { recordingLayout = during; }
            else
            {
                Assert.Equal(recordingLayout[0], during[0]);
                Assert.Equal(recordingLayout[1], during[1]);
            }

            Guid id = state.LiveRecordingId!.Value;
            await state.StopRecording();
            await Ui.WaitFor("the recording was filed", () => state.RecordingState == MeetingRecordingState.Idle && state.Recordings.Any(stored => stored.Id == id));
            Ui.Settle();

            Assert.DoesNotContain(Rows(), row => Name(row) == "Recording");
            Assert.Equal(header, FirstHeaderY(), 0.5);
            Assert.Equal(default, Offset);
            string finished = state.Recordings.First(stored => stored.Id == id).Title ?? RecordingsPlaceholder.DefaultTitle(state.Recordings.First(stored => stored.Id == id).StartedAt);
            Assert.Equal([finished], SelectedNames());

            // Every cycle files one more recording under today's header: the rows above it hold still.
            List<(string? Name, double Y)> after = Layout();
            if (stoppedLayout is not null)
            {
                Assert.Equal(stoppedLayout[0].Y, after[0].Y, 0.5);
                Assert.Equal(stoppedLayout[1].Y, after[1].Y, 0.5);
            }

            stoppedLayout = after;
            before = after;
        }
    }

    private RecordingDetailView OpenRecording(string title)
    {
        Click(Row(title));
        return Assert.IsType<RecordingDetailView>(window!.FindControl<ContentControl>("Detail")!.Content);
    }

    private static TextBlock TitleOf(RecordingDetailView detail) =>
        detail.GetVisualDescendants().OfType<TextBlock>().First(text => text.Classes.Contains("display24") && text.IsVisible);

    private static TextBox EditorOf(RecordingDetailView detail) =>
        detail.GetVisualDescendants().OfType<TextBox>().First(box => AutomationProperties.GetName(box) == "Recording name");

    private void DoubleClick(Control control)
    {
        Point at = Centre(control);
        window!.MouseDown(at, MouseButton.Left);
        window!.MouseUp(at, MouseButton.Left);
        window!.MouseDown(at, MouseButton.Left);
        window!.MouseUp(at, MouseButton.Left);
        Ui.Settle();
    }

    [AvaloniaFact]
    public void DoubleClickingTheTitleRenamesWithReturnAndEscapeCancels()
    {
        Seed();
        AppState state = harness.MakeState();
        Open(state);
        double header = FirstHeaderY();
        RecordingDetailView detail = OpenRecording("Call 0");
        Guid id = state.Recordings.First(stored => stored.Title == "Call 0").Id;

        DoubleClick(TitleOf(detail));
        TextBox editor = EditorOf(detail);
        Assert.True(editor.IsVisible);
        Assert.True(editor.IsFocused);
        Type("Planning");
        Press(Key.Enter, PhysicalKey.Enter);

        Assert.False(editor.IsVisible);
        Assert.Equal("Planning", state.Recordings.First(stored => stored.Id == id).Title);
        Assert.Equal("Planning", TitleOf(detail).Text);
        Assert.Equal(["Planning"], SelectedNames());
        Assert.Contains(Rows(), row => Name(row) == "Planning");
        Assert.Equal(header, FirstHeaderY(), 0.5);

        DoubleClick(TitleOf(detail));
        Assert.True(editor.IsVisible);
        Type("Never saved");
        Press(Key.Escape, PhysicalKey.Escape);

        Assert.False(editor.IsVisible);
        Assert.Equal("Planning", state.Recordings.First(stored => stored.Id == id).Title);
        Assert.Equal("Planning", TitleOf(detail).Text);
        Assert.Equal(header, FirstHeaderY(), 0.5);
    }

    [AvaloniaFact]
    public void RenameFromTheRowsMenuOpensTheTitle()
    {
        Seed();
        AppState state = harness.MakeState();
        Open(state);
        Guid id = state.Recordings.First(stored => stored.Title == "Call 1").Id;
        MenuItem rename = Row("Call 1").ContextMenu!.ItemsSource!.OfType<MenuItem>().First(item => (string?)item.Header == "Rename…");

        rename.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Ui.Settle();

        var detail = Assert.IsType<RecordingDetailView>(window!.FindControl<ContentControl>("Detail")!.Content);
        TextBox editor = EditorOf(detail);
        Assert.True(editor.IsVisible);
        Assert.True(editor.IsFocused);
        Type("Standup");
        Press(Key.Enter, PhysicalKey.Enter);

        Assert.Equal("Standup", state.Recordings.First(stored => stored.Id == id).Title);
        Assert.Equal(["Standup"], SelectedNames());
    }

    [AvaloniaFact]
    public void DeletingTwoDictationsChosenWithCtrlClickSelectsTheFirstRemaining()
    {
        Seed();
        AppState state = harness.MakeState();
        Open(state);
        double header = FirstHeaderY();
        const string second = "take number 1 with a few words in it", third = "take number 2 with a few words in it";
        int count = state.History.Count;

        Click(Row(second));
        Click(Row(third), RawInputModifiers.Control);
        Assert.Equal([second, third], SelectedNames());

        Press(Key.Delete, PhysicalKey.Delete);

        Assert.Equal(count - 2, state.History.Count);
        Assert.DoesNotContain(state.History, entry => entry.Text is second or third);
        Assert.DoesNotContain(Rows(), row => Name(row) is second or third);
        ListBoxItem first = Rows().First(row => !row.Classes.Contains("dayHeader"));
        Assert.Equal([Name(first)], SelectedNames());
        Assert.Equal(header, FirstHeaderY(), 0.5);
        Assert.Equal(default, Offset);
    }
}
