using DictationCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// Dictation history over a real <see cref="AppState"/>: every finished take is kept with its
/// audio, the limit trims, a star exempts, and the words are reachable again.
/// </summary>
/// <remarks>
/// Ported from the macOS <c>AppStateTests</c> history scenarios; the store's own rules are in
/// <see cref="DictationHistoryStoreTests"/>.
/// </remarks>
public sealed class AppStateHistoryTests : IDisposable
{
    private readonly AppHarness harness = new();

    private FakeHotkeyMonitor Monitor => harness.Monitor;

    public void Dispose() => harness.Dispose();

    /// <summary>
    /// A take that produced text is archived after insertion is attempted and before the
    /// recording is deleted, so the history gets both halves while both still exist.
    /// </summary>
    [Fact]
    public async Task AFinishedTakeIsKeptWithItsAudio()
    {
        AppState state = await ReadyState();
        Assert.Empty(state.History);

        await DictateOnce(state);

        HistoryEntry entry = Assert.Single(state.History);
        Assert.Equal("Ping test", entry.Text);
        Assert.False(entry.IsKept);
        string? audio = state.HistoryAudioPath(entry);
        Assert.NotNull(audio);
        Assert.True(File.Exists(audio));
        Assert.StartsWith(Path.Combine(harness.Paths.Support(), "History"), audio, StringComparison.Ordinal);
    }

    /// <summary>The words were inserted; the take is still archived when insertion fails.</summary>
    [Fact]
    public async Task AFailedInsertionIsStillKept()
    {
        AppState state = await ReadyState();
        harness.Inserter.SetError(new TextInsertionError.SecureInputActive());

        Monitor.OnPress!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);
        Monitor.OnRelease!();
        await TestWait.For("the take was archived", () => state.History.Count == 1);

        Assert.Equal("Ping test", state.History[0].Text);
    }

    /// <summary>The history survives a relaunch, and so does its limit.</summary>
    [Fact]
    public async Task TheHistorySurvivesARelaunch()
    {
        AppState state = await ReadyState();
        await DictateOnce(state);
        state.HistoryLimit = 7;

        AppState restarted = harness.MakeState();

        Assert.Single(restarted.History);
        Assert.Equal(7, restarted.HistoryLimit);
        Assert.Equal(7, harness.Settings.Read().HistoryLimit);
    }

    /// <summary>Lowering the limit is a deletion the person can rely on; a star exempts an entry.</summary>
    [Fact]
    public async Task LoweringTheLimitTrimsAndAStarExempts()
    {
        AppState state = await ReadyState();
        Assert.Equal(DictationHistoryStore.DefaultLimit, state.HistoryLimit);
        for (int take = 0; take < 3; take++) { await DictateOnce(state); }
        Assert.Equal(3, state.History.Count);

        HistoryEntry oldest = state.History[^1];
        state.SetHistoryEntryKept(true, oldest);
        state.HistoryLimit = 1;

        Assert.Equal(2, state.History.Count);
        Assert.Contains(state.History, entry => entry.Id == oldest.Id && entry.IsKept);
        Assert.Equal(1, state.History.Count(entry => !entry.IsKept));
    }

    /// <summary>Deleting removes the recording as well; clearing removes everything.</summary>
    [Fact]
    public async Task DeletingRemovesTheAudioToo()
    {
        AppState state = await ReadyState();
        await DictateOnce(state);
        await DictateOnce(state);
        HistoryEntry entry = state.History[0];
        string audio = state.HistoryAudioPath(entry)!;

        state.DeleteHistoryEntry(entry);

        Assert.Single(state.History);
        Assert.False(File.Exists(audio));

        state.ClearHistory();

        Assert.Empty(state.History);
        Assert.Empty(Directory.GetFiles(Path.Combine(harness.Paths.Support(), "History"), "*.wav"));
    }

    /// <summary>A stored transcript goes back on the clipboard, host-only like every copy.</summary>
    [Fact]
    public async Task AStoredTranscriptCanBeCopied()
    {
        AppState state = await ReadyState();
        await DictateOnce(state);

        state.CopyHistoryEntry(state.History[0]);

        Assert.Equal(["Ping test"], harness.Clipboard.Copied);
    }

    /// <summary>The folder opens on a recording rather than on a bare directory.</summary>
    [Fact]
    public async Task RevealingTheAudioSelectsARecording()
    {
        AppState state = await ReadyState();
        state.RevealHistoryAudio();
        await TestWait.For("the empty history said so", () => harness.Overlay.Notices.Any(notice => notice.Message == "No recordings kept yet."));

        await DictateOnce(state);
        state.RevealHistoryAudio();

        Assert.Equal([state.HistoryAudioPath(state.History[0])!], harness.Folders.Opened);
    }

    /// <summary>
    /// A re-run that fails changes nothing, rather than blanking a record of something the person
    /// actually said - and it needs a loaded engine to try at all.
    /// </summary>
    [Fact]
    public async Task ARerunThatFailsLeavesTheTextIntact()
    {
        harness.WarmUpEngine = new CountingASREngine();
        harness.InstallModelMarker();
        AppState state = harness.MakeState();
        await TestWait.For("the engine warmed up", () => state.IsEngineReady);
        Assert.True(state.IsDictationReady);
        await DictateOnce(state);
        HistoryEntry entry = state.History[0];

        // The fake capture's file is not a recording the reader can open.
        state.RetranscribeHistoryEntry(entry);
        await TestWait.For("the re-run finished", () => !state.IsRetranscribing);
        await TestWait.For(
            "the failure was said",
            () => harness.Overlay.Notices.Any(notice => notice.Message.Contains("recognise that recording again", StringComparison.Ordinal)));

        Assert.Equal("Ping test", state.History[0].Text);
        Assert.Equal(entry.Id, state.History[0].Id);
    }

    /// <summary>The words never reach the log: the archive event carries counts only.</summary>
    [Fact]
    public async Task NothingSpokenReachesTheLog()
    {
        var sink = new StringWriter();
        var log = new DiagnosticLog(sink);
        AppState state;
        using (DiagnosticLog.Install(log))
        {
            state = await ReadyState();
            await DictateOnce(state);
        }

        log.Dispose();
        Assert.DoesNotContain("Ping test", sink.ToString(), StringComparison.Ordinal);
        Assert.Contains("dictation.speed", sink.ToString(), StringComparison.Ordinal);
    }

    // MARK: - Helpers

    private async Task<AppState> ReadyState()
    {
        harness.InstallModelMarker();
        AppState state = harness.MakeState();
        await state.RefreshModelState();
        Assert.True(state.IsDictationReady);
        return state;
    }

    private async Task DictateOnce(AppState state)
    {
        int before = state.History.Count;
        Monitor.OnPress!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);
        Monitor.OnRelease!();
        await TestWait.For("the take was archived", () => state.History.Count > before);
        await TestWait.For("dictation ended", () => state.DictationStateValue == DictationState.Idle);
    }
}
