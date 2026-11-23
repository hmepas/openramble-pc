using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
}
