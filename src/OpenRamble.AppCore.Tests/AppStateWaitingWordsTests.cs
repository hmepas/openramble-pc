using DictationCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// Words that could not land wait on the panel, and the dictation key acts on them (upstream
/// 0.37.0 / 0.39.0, after <c>runner.rs</c>'s tests in the desktop app).
/// </summary>
public sealed class AppStateWaitingWordsTests : IDisposable
{
    private readonly AppHarness harness = new();
    private readonly FakeWaitingOverlay panel = new();

    public AppStateWaitingWordsTests()
    {
        harness.Overlay = panel;
    }

    public void Dispose() => harness.Dispose();

    [Fact]
    public async Task WordsThatWereNotReadWaitInFullAndJoinHistory()
    {
        AppState state = await StateWithWaitingWords(new TextInsertionError.PasteNotRead());

        WaitingWords waiting = state.WaitingWordsValue!;
        Assert.Equal("Ping test", waiting.Words);
        Assert.Equal(WaitingCause.NoTextField, waiting.Cause);
        Assert.Equal(waiting, panel.Waitings[^1]);
        Assert.Equal(["Ping test"], state.RecentDictations.Select(entry => entry.Text));
    }

    /// <summary>Without a panel that shows them, nothing waits and the key stays a dictation key.</summary>
    [Fact]
    public async Task WithoutAPanelNothingWaits()
    {
        harness.Overlay = new FakeOverlay();
        AppState state = await ReadyState();
        harness.Inserter.SetError(new TextInsertionError.PasteNotRead());
        await Dictate(state);

        Assert.Null(state.WaitingWordsValue);
        Assert.Equal("Ping test", state.RecoveredText);
    }

    /// <summary>
    /// a_tap_over_waiting_words_pastes_them: the drawn key goes down with the real one, the paste
    /// is tried again into the app in front now, and the microphone does not open for it.
    /// </summary>
    [Fact]
    public async Task ATapOverWaitingWordsPastesThem()
    {
        AppState state = await StateWithWaitingWords(new TextInsertionError.PasteNotRead());
        harness.Inserter.SetError(null);
        harness.Inserter.Frontmost = new TargetApplication("editor", 42, "Editor");

        harness.Monitor.OnPress!();
        Assert.Equal([true], panel.KeyDowns);
        harness.Monitor.OnRelease!();

        await TestWait.For("the words landed", () => state.WaitingWordsValue is null);
        Assert.Equal([true, false], panel.KeyDowns);
        Assert.Equal(["Ping test"], harness.Inserter.InsertedTexts);
        Assert.Equal(new TakeOutcome.Pasted("Editor"), panel.Outcomes[^1]);
        Assert.Equal(1, harness.Capture.StartCount);
        Assert.Null(state.RecoveredText);
    }

    [Fact]
    public async Task ATapThatDoesNotLandLeavesTheWordsWhereTheyWere()
    {
        AppState state = await StateWithWaitingWords(new TextInsertionError.PasteNotRead());

        harness.Monitor.OnPress!();
        harness.Monitor.OnRelease!();

        await TestWait.For("the retry answered", () => panel.Waitings.Count(w => w is not null) >= 2);
        Assert.Equal("Ping test", state.WaitingWordsValue!.Words);
        Assert.Empty(panel.Outcomes);
        Assert.Equal(1, harness.Capture.StartCount);
    }

    /// <summary>
    /// a_second_tap_over_waiting_words_is_still_a_tap: the double tap cancels the first press's
    /// release, so the first press was a tap, and hands-free never starts while words wait.
    /// </summary>
    [Fact]
    public async Task ASecondTapOverWaitingWordsIsStillATap()
    {
        AppState state = await StateWithWaitingWords(new TextInsertionError.PasteNotRead());
        harness.Inserter.SetError(null);

        harness.Monitor.OnPress!();
        harness.Monitor.OnDoubleTap!();
        harness.Monitor.OnRelease!();

        await TestWait.For("the words landed", () => state.WaitingWordsValue is null);
        Assert.Equal(["Ping test"], harness.Inserter.InsertedTexts);
        Assert.False(state.IsHandsFreeActive);
        Assert.Equal(1, harness.Capture.StartCount);
    }

    /// <summary>While typing is protected a tap copies them, and then the key dictates again.</summary>
    [Fact]
    public async Task WhileTypingIsProtectedATapCopiesAndThenTheKeyDictates()
    {
        AppState state = await StateWithWaitingWords(new TextInsertionError.SecureInputActive());
        Assert.Equal(WaitingAction.Copy, state.WaitingWordsValue!.Action);

        harness.Monitor.OnPress!();
        harness.Monitor.OnRelease!();

        Assert.True(state.WaitingWordsValue!.IsCopied);
        Assert.Equal(["Ping test"], harness.Clipboard.Copied);
        Assert.Empty(harness.Inserter.InsertedTexts);

        harness.Inserter.SetError(null);
        await Dictate(state);
        Assert.Null(state.WaitingWordsValue);
        Assert.Equal(2, harness.Capture.StartCount);
    }

    /// <summary>a_hold_over_waiting_words_lets_them_go: a hold is a new take.</summary>
    [Fact]
    public async Task AHoldOverWaitingWordsLetsThemGoAndDictates()
    {
        harness.WaitingTapLimit = TimeSpan.FromMilliseconds(50);
        AppState state = await StateWithWaitingWords(new TextInsertionError.PasteNotRead());
        harness.Inserter.SetError(null);

        harness.Monitor.OnPress!();
        await TestWait.For("the hold started a take", () => state.DictationStateValue == DictationState.Listening);
        Assert.Null(state.WaitingWordsValue);
        Assert.Null(panel.Waitings[^1]);
        Assert.Empty(panel.Outcomes);

        harness.Monitor.OnRelease!();
        await TestWait.For("the take ended", () => state.DictationStateValue == DictationState.Idle);
        Assert.Equal(["Ping test"], harness.Inserter.InsertedTexts);
        Assert.Equal(2, harness.Capture.StartCount);
    }

    /// <summary>later_says_where_the_words_went; Escape is the same.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterSaysWhereTheWordsWent(bool escape)
    {
        AppState state = await StateWithWaitingWords(new TextInsertionError.PasteNotRead());

        if (escape) { harness.Monitor.OnEscape!(); }
        else { panel.OnWaitingLater!(); }

        Assert.Null(state.WaitingWordsValue);
        Assert.Equal(new TakeOutcome.Kept(InHistory: true, Chord: null), panel.Outcomes[^1]);
        Assert.Equal(["Ping test"], state.RecentDictations.Select(entry => entry.Text));
    }

    /// <summary>The journal says which kind of paste did not land, and none of its words.</summary>
    [Fact]
    public async Task TheJournalNamesThePasteFailureWithoutWords()
    {
        AppState state = await StateWithWaitingWords(new TextInsertionError.PasteNotRead());

        await TestWait.For(
            "the failure reached the journal",
            () => LogText().Contains("\"failureReason\":\"pasteUnread\"", StringComparison.Ordinal));
        Assert.DoesNotContain("Ping test", LogText(), StringComparison.Ordinal);
        Assert.NotNull(state.WaitingWordsValue);
    }

    // MARK: - Helpers

    private async Task<AppState> StateWithWaitingWords(TextInsertionError failure)
    {
        AppState state = await ReadyState();
        harness.Inserter.SetError(failure);
        await Dictate(state);
        Assert.NotNull(state.WaitingWordsValue);
        return state;
    }

    private async Task<AppState> ReadyState()
    {
        harness.InstallModelMarker();
        AppState state = harness.MakeState();
        await state.RefreshModelState();
        Assert.True(state.IsDictationReady);
        return state;
    }

    private async Task Dictate(AppState state)
    {
        harness.Monitor.OnPress!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);
        harness.Monitor.OnRelease!();
        await TestWait.For("dictation ended", () => state.DictationStateValue == DictationState.Idle);
    }

    private string LogText()
    {
        if (!Directory.Exists(harness.MakeStateLogDirectory())) { return string.Empty; }
        return string.Concat(Directory.GetFiles(harness.MakeStateLogDirectory(), "events-*.jsonl").Select(File.ReadAllText));
    }
}
