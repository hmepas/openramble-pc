using DictationCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The menu's takes (upstream 0.37 and 0.41): the three newest of this session, a click pastes
/// one, the Copy submenu copies one, and a take deleted in History leaves the menu.
/// </summary>
[Collection(DiagnosticLogCollection.Name)]
public sealed class AppStateMenuTakesTests : IDisposable
{
    private readonly AppHarness harness = new();

    private FakeHotkeyMonitor Monitor => harness.Monitor;

    public void Dispose() => harness.Dispose();

    /// <summary>A menu is not a history: the three newest, the last take first.</summary>
    [Fact]
    public async Task TheMenuOffersTheThreeNewestTakes()
    {
        AppState state = await ReadyState();
        foreach (string words in new[] { "One", "Two", "Three", "Four" }) { await DictateOnce(state, words); }

        Assert.Equal(["Four", "Three", "Two"], state.MenuTakes.Select(take => take.Text));
    }

    /// <summary>The same words twice are one row, so three rows are three different takes.</summary>
    [Fact]
    public async Task TheSameWordsTwiceAreOneRow()
    {
        AppState state = await ReadyState();
        foreach (string words in new[] { "One", "Two", "Two" }) { await DictateOnce(state, words); }

        Assert.Equal(["Two", "One"], state.MenuTakes.Select(take => take.Text));
    }

    /// <summary>The takes are this session's: History survives a relaunch, the menu does not.</summary>
    [Fact]
    public async Task TheTakesAreThisSessionsOnly()
    {
        AppState state = await ReadyState();
        await DictateOnce(state, "One");

        AppState restarted = harness.MakeState();

        Assert.Single(restarted.History);
        Assert.Empty(restarted.MenuTakes);
    }

    /// <summary>Deleted in History means gone from the menu too; the other takes stay.</summary>
    [Fact]
    public async Task ATakeDeletedInHistoryLeavesTheMenu()
    {
        AppState state = await ReadyState();
        await DictateOnce(state, "One");
        await DictateOnce(state, "Two");

        state.DeleteHistoryEntry(state.History.Single(entry => entry.Text == "Two"));

        Assert.Equal(["One"], state.MenuTakes.Select(take => take.Text));
    }

    /// <summary>Delete All empties the menu.</summary>
    [Fact]
    public async Task DeleteAllClearsTheTakes()
    {
        AppState state = await ReadyState();
        await DictateOnce(state, "One");

        state.ClearHistory();

        Assert.Empty(state.MenuTakes);
    }

    /// <summary>A click pastes the take through the inserter, not the clipboard.</summary>
    [Fact]
    public async Task ClickingATakePastesIt()
    {
        AppState state = await ReadyState();
        await DictateOnce(state, "One");
        await DictateOnce(state, "Two");

        state.PasteTake(state.MenuTakes[1]);

        await TestWait.For("the take was pasted", () => harness.Inserter.InsertedTexts.Count == 3);
        await TestWait.For("the paste ended", () => state.DictationStateValue == DictationState.Idle);
        Assert.Equal("One", harness.Inserter.InsertedTexts[^1]);
        Assert.Empty(harness.Clipboard.Copied);
    }

    /// <summary>A paste nobody read says so; the take stays on the menu.</summary>
    [Fact]
    public async Task APasteThatDidNotLandSaysSo()
    {
        AppState state = await ReadyState();
        await DictateOnce(state, "One");
        harness.Inserter.SetError(new TextInsertionError.PasteNotRead());

        state.PasteLastTake();

        await TestWait.For(
            "the failure was said",
            () => harness.Overlay.Notices.Any(notice => notice.Message.Contains("couldn't be pasted", StringComparison.Ordinal)));
        await TestWait.For("the paste ended", () => state.DictationStateValue == DictationState.Idle);
        Assert.Equal(["One"], state.MenuTakes.Select(take => take.Text));
    }

    /// <summary>The Copy submenu copies, host-only.</summary>
    [Fact]
    public async Task TheCopyVariantCopies()
    {
        AppState state = await ReadyState();
        await DictateOnce(state, "One");

        state.CopyRecentDictation(state.MenuTakes[0]);

        Assert.Equal(["One"], harness.Clipboard.Copied);
    }

    /// <summary>Only the last take's row carries the chord: it is the one the chord pastes.</summary>
    [Fact]
    public async Task OnlyTheLastTakeShowsTheShortcut()
    {
        AppState state = await ReadyState();
        await DictateOnce(state, "One");
        state.LastTakeShortcut = new KeyCombination(67, HotkeyModifiers.Control | HotkeyModifiers.Alt);
        AppState.RecentDictation take = state.MenuTakes[0];

        Assert.Equal("“One”   Ctrl+Alt+C", state.TakeMenuTitle(take, isLastTake: true));
        Assert.Equal("“One”", state.TakeMenuTitle(take, isLastTake: false));

        state.LastTakeShortcut = null;
        Assert.Equal("“One”", state.TakeMenuTitle(take, isLastTake: true));
    }

    private async Task<AppState> ReadyState()
    {
        harness.InstallModelMarker();
        AppState state = harness.MakeState();
        await state.RefreshModelState();
        Assert.True(state.IsDictationReady);
        return state;
    }

    private async Task DictateOnce(AppState state, string words)
    {
        harness.Transcription.Text = words;
        int before = state.History.Count;
        Monitor.OnPress!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);
        Monitor.OnRelease!();
        await TestWait.For("the take was archived", () => state.History.Count > before);
        await TestWait.For("dictation ended", () => state.DictationStateValue == DictationState.Idle);
    }
}
