using DictationCore;
using OpenRamble.AppCore.Audio;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// A take the model found no words in is kept in History, not lost; a take that heard only
/// silence says which microphone it came from.
/// </summary>
/// <remarks>
/// Ported from the macOS <c>AppStateEmptyTakeTests</c>. Its journal assertions
/// (<c>dictationUnrecognized</c>, <c>dictationWarning</c>) are left out: this port has no error
/// journal, only the diagnostic log.
/// </remarks>
public sealed class AppStateEmptyTakeTests : IDisposable
{
    private readonly AppHarness harness = new();

    /// <summary>Room noise, well under the speech floor.</summary>
    private static readonly float[] Quiet = Enumerable.Repeat(0.001f, 16_000).ToArray();

    public AppStateEmptyTakeTests()
    {
        harness.InstallModelMarker();
        harness.Transcription.Text = "";
    }

    public void Dispose() => harness.Dispose();

    private async Task Dictate(AppState state)
    {
        await state.RefreshModelState();
        harness.Monitor.OnPress!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);
        harness.Monitor.OnRelease!();
        await TestWait.For(
            "the take ended and said so",
            () => state.DictationStateValue == DictationState.Idle && state.LastNotice is not null);
    }

    [Fact]
    public async Task AnEmptyTakeWithSoundIsKeptInHistoryWithNoText()
    {
        harness.Capture.SetBufferedSamples([.. Quiet, 0.1f, .. Quiet]);
        AppState state = harness.MakeState();

        await Dictate(state);

        HistoryEntry entry = Assert.Single(state.History);
        Assert.Equal("", entry.Text);
        Assert.True(state.HistoryAudioPath(entry) is not null, "with its audio, to play and recognize again");
        Assert.Equal(DictationNoticeKind.Info, state.LastNotice!.Kind);
        Assert.Equal("Nothing was recognized — the recording is in History.", state.LastNotice.Message);
    }

    /// <summary>
    /// The menu's recent dictations are there to be copied, and a take with no words has nothing
    /// to copy: offered there, it would put an empty string over whatever the person had on the
    /// clipboard.
    /// </summary>
    [Fact]
    public async Task AWordlessTakeIsNotOfferedAmongRecentDictations()
    {
        harness.Capture.SetBufferedSamples([.. Quiet, 0.1f, .. Quiet]);
        AppState state = harness.MakeState();

        await Dictate(state);

        Assert.Equal([""], state.History.Select(entry => entry.Text));
        Assert.Empty(state.RecentDictations);
    }

    /// <summary>The name is the microphone chosen in Settings, when it is plugged in.</summary>
    [Fact]
    public async Task SilenceNamesTheMicrophoneTheCaptureRecordsFrom()
    {
        harness.Capture.SetBufferedSamples(Quiet);
        harness.InputDevices.Devices.Add(new AudioInputDevice("usb-headset", "USB Headset"));
        harness.Settings.Write(new AppSettings { InputDeviceUid = "usb-headset" });
        AppState state = harness.MakeState();

        await Dictate(state);

        Assert.True(state.History.Count == 0, "silence is not kept");
        Assert.Equal("Heard only silence from USB Headset.", state.LastNotice!.Message);
        Assert.Equal(DictationNoticeKind.Warning, state.LastNotice.Kind);
    }

    private AppState SeedWordlessTake()
    {
        string audio = Path.Combine(harness.Paths.Support(), "seed.wav");
        Directory.CreateDirectory(harness.Paths.Support());
        File.WriteAllText(audio, "RIFF-test-audio");
        _ = new DictationHistoryStore(Path.Combine(harness.Paths.Support(), "History")).Record("", audio, 5);
        return harness.MakeState();
    }

    [Fact]
    public async Task RecognizingAWordlessTakeAgainToNothingSaysSoAndKeepsIt()
    {
        var engine = new CountingASREngine { Text = "" };
        harness.WarmUpEngine = engine;
        AppState state = SeedWordlessTake();
        await TestWait.For("the engine warmed up", () => state.IsEngineReady);
        HistoryEntry entry = state.History[0];
        string seed = Path.Combine(harness.Paths.Support(), "History", entry.AudioFileName!);
        WriteSilentWav(seed);

        state.RetranscribeHistoryEntry(entry);
        await TestWait.For("the re-run finished", () => state.LastNotice is not null && !state.IsRetranscribing);

        Assert.Equal("Still nothing recognized.", state.LastNotice!.Message);
        Assert.Equal([entry], state.History);
    }

    [Fact]
    public async Task RecognizingAWordlessTakeAgainToWordsFillsTheRow()
    {
        var engine = new CountingASREngine { Text = "hello there" };
        harness.WarmUpEngine = engine;
        AppState state = SeedWordlessTake();
        await TestWait.For("the engine warmed up", () => state.IsEngineReady);
        HistoryEntry entry = state.History[0];
        WriteSilentWav(Path.Combine(harness.Paths.Support(), "History", entry.AudioFileName!));

        state.RetranscribeHistoryEntry(entry);
        await TestWait.For(
            "the row was filled",
            () => state.History.Count > 0 && state.History[0].Text.Length > 0 && !state.IsRetranscribing);

        HistoryEntry filled = state.History[0];
        Assert.Equal(entry.Id, filled.Id);
        Assert.Equal("Hello there", filled.Text);
        Assert.True(state.HistoryAudioPath(filled) is not null, "the audio stays for another try");
    }

    /// <summary>
    /// Delete All means every copy of the words this app holds: the rows, and the last dictation
    /// behind "Copy Last" with them.
    /// </summary>
    [Fact]
    public async Task DeleteAllAlsoForgetsTheLastDictation()
    {
        harness.Transcription.Text = "Ping test";
        AppState state = harness.MakeState();
        await state.RefreshModelState();
        harness.Monitor.OnPress!();
        await TestWait.For("recording has started", () => state.DictationStateValue == DictationState.Listening);
        harness.Monitor.OnRelease!();
        await TestWait.For("the take was archived", () => state.History.Count == 1);
        await TestWait.For("dictation ended", () => state.DictationStateValue == DictationState.Idle);
        Assert.NotNull(state.LastDictationValue);

        state.ClearHistory();

        Assert.Empty(state.History);
        Assert.Null(state.LastDictationValue);
        Assert.Empty(state.RecentDictations);
    }

    /// <summary>One second of 16 kHz mono silence, a file the reader can open.</summary>
    private static void WriteSilentWav(string path)
    {
        const int samples = 16_000;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + (samples * 2));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16_000);
        writer.Write(32_000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 2);
        writer.Write(new byte[samples * 2]);
    }
}
