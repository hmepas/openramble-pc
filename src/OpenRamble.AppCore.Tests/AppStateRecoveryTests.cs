using DictationCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>Text that failed to insert stays in memory only, reachable through Copy and Retry.</summary>
public sealed class AppStateRecoveryTests : IDisposable
{
    private readonly AppHarness harness = new();

    public void Dispose() => harness.Dispose();

    [Fact]
    public async Task Scenario053()
    {
        AppState state = await ReadyState();
        Assert.Null(state.RecoveredText);

        harness.Inserter.SetError(new TextInsertionError.SecureInputActive());
        await Dictate(state);

        Assert.Equal("Ping test", state.RecoveredText);
    }

    [Fact]
    public async Task Scenario054()
    {
        AppState state = await ReadyState();

        harness.Inserter.SetError(new TextInsertionError.SecureInputActive());
        await Dictate(state);
        Assert.NotNull(state.RecoveredText);

        harness.Inserter.SetError(null);
        harness.Monitor.OnPress!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);
        harness.Monitor.OnEscape!();
        await TestWait.For("dictation cancelled", () => state.DictationStateValue == DictationState.Idle);

        Assert.Equal("Ping test", state.RecoveredText);
    }

    /// <summary>
    /// Failed-insert words join Recent Dictations at failure time.
    ///
    /// The single recovery slot is overwritten by the next failure; without this copy the first
    /// failure's words would silently vanish. It is also why the menu needs no Copy/Delete items
    /// for saved text: the words are copyable from Recent Dictations like any other dictation.
    /// </summary>
    [Fact]
    public async Task Scenario055()
    {
        AppState state = await ReadyState();

        harness.Inserter.SetError(new TextInsertionError.SecureInputActive());
        await Dictate(state);

        Assert.Equal("Ping test", state.RecoveredText);
        Assert.Equal(["Ping test"], state.RecentDictations.Select(entry => entry.Text));
    }

    /// <summary>Retrying and failing again must not fill the history with copies.</summary>
    [Fact]
    public async Task FailedRetryDoesNotDuplicateHistory()
    {
        AppState state = await ReadyState();

        harness.Inserter.SetError(new TextInsertionError.SecureInputActive());
        await Dictate(state);
        Assert.Equal("Ping test", state.RecoveredText);

        state.RetryRecoveredText();
        await TestWait.For("the retry finished", () => !state.IsRecoveryOperationActive);

        Assert.Equal("Ping test", state.RecoveredText);
        Assert.Equal(["Ping test"], state.RecentDictations.Select(entry => entry.Text));
    }

    /// <summary>A successful retry closes the recovery without double bookkeeping.</summary>
    [Fact]
    public async Task SuccessfulRetryKeepsOneHistoryEntryAndClearsStaleProvenance()
    {
        AppState state = await ReadyState();

        harness.Inserter.SetError(new TextInsertionError.SecureInputActive());
        await Dictate(state);
        Assert.Equal("Ping test", state.RecoveredText);
        harness.Inserter.SetError(null);

        state.RetryRecoveredText();
        await TestWait.For("the retry finished", () => state.RecoveredText is null);

        Assert.Equal(["Ping test"], state.RecentDictations.Select(entry => entry.Text));
        Assert.Null(state.LastDictationValue);
    }

    [Fact]
    public async Task Scenario056()
    {
        AppState state = await ReadyState();

        harness.Inserter.SetError(new TextInsertionError.SecureInputActive());
        await Dictate(state);
        harness.Inserter.SetError(null);

        state.RetryRecoveredText();
        state.RetryRecoveredText();
        await TestWait.For("the retry finished", () => state.RecoveredText is null);

        Assert.Equal(["Ping test"], harness.Inserter.InsertedTexts);
    }

    /// <summary>
    /// A successful new dictation supersedes an older insertion warning. The menu must not keep
    /// saying that the last dictation failed after text has landed.
    /// </summary>
    [Fact]
    public async Task Scenario057()
    {
        AppState state = await ReadyState();

        harness.Inserter.SetError(new TextInsertionError.SecureInputActive());
        await Dictate(state);
        Assert.Equal("Ping test", state.RecoveredText);

        harness.Inserter.SetError(null);
        await Dictate(state);

        await TestWait.For("the recovery cleared", () => state.RecoveredText is null);
        Assert.Equal("Ping test", state.RecentDictations[0].Text);
    }

    /// <summary>The words are copyable from the menu, host-only, without touching disk.</summary>
    [Fact]
    public async Task RecentDictationIsCopiedHostOnly()
    {
        AppState state = await ReadyState();
        await Dictate(state);
        await TestWait.For("history has an entry", () => state.RecentDictations.Count > 0);

        state.CopyRecentDictation(state.RecentDictations[0]);

        Assert.Equal(["Ping test"], harness.Clipboard.Copied);
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
}
