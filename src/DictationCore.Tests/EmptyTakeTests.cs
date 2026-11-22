namespace DictationCore.Tests;

/// <summary>
/// A take in which the model found no words is never thrown away in silence.
///
/// Sound with no words is kept for History, where it can be played and recognized again.
/// Silence is not kept - a row of nothing helps nobody - but it names the microphone it came
/// from, because a muted or wrong input is the usual reason a held key records nothing.
/// </summary>
/// <remarks>
/// Not ported, with the reason: upstream's tests of a take whose file never reached disk, of a
/// wedged writer, of a copy rebuilt from the samples and of Escape during the handover. The PC
/// captures close the WAV inside <c>StopRecording</c>, so a finished take always has its whole
/// file and the handover to History is one synchronous call with nothing to wait for or race.
/// </remarks>
[Collection(DictationControllerCollection.Name)]
public sealed class EmptyTakeTests : IDisposable
{
    private readonly TakeCapture capture = new();
    private readonly FakeInserter inserter = new();
    private readonly FakeOverlay overlay = new();
    private readonly FakeSounds sounds = new();
    private readonly List<(string Text, string Url)> archived = [];

    /// <summary>Whether History takes what it is handed. It can refuse: a full disk, a folder it may not write to.</summary>
    private bool historyKeeps = true;

    /// <summary>Room noise, well under the speech floor.</summary>
    private static readonly float[] Quiet = Enumerable.Repeat(0.001f, 16_000).ToArray();

    private static float[] QuietWithAClick => [.. Quiet, 0.1f, .. Quiet];

    public void Dispose() => capture.Dispose();

    private DictationController MakeController(
        Func<string, CancellationToken, Task<float[]>>? readSamples = null)
    {
        var controller = new DictationController(
            capture: capture,
            transcribe: (_, _) => Task.FromResult(Nothing),
            inserter: inserter,
            overlay: overlay,
            sounds: sounds,
            pipeline: _ => new TextPipeline(),
            readSamples: readSamples);
        controller.OnTakeFinished = (text, url, _) =>
        {
            if (!historyKeeps) { return false; }
            lock (archived) { archived.Add((text, url)); }
            return true;
        };
        return controller;
    }

    private static ASRResult Nothing => new("", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(0.1));

    /// <summary>One whole take, waited on by condition rather than by a fixed number of sleeps.</summary>
    private async Task Dictate(DictationController controller)
    {
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await WaitUntil(() => controller.State == DictationState.Listening);
        controller.Stop();
        await WaitUntil(() => controller.State == DictationState.Idle);
        // The notice reaches the panel before the session ends.
        await WaitUntil(() => overlay.Notices.Count > 0);
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        DateTime end = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (!predicate() && DateTime.UtcNow < end)
        {
            await Task.Delay(5);
        }

        Assert.True(predicate());
    }

    [Fact]
    public async Task TestAnEmptyTakeWithSoundIsKeptInHistoryWithoutText()
    {
        capture.SetBufferedSamples(QuietWithAClick);

        await Dictate(MakeController());

        Assert.Empty(inserter.InsertedTexts);
        Assert.True(archived.Select(entry => entry.Text).SequenceEqual([""]), "kept so it can be heard and recognized again");
        Assert.Equal([capture.Url], archived.Select(entry => entry.Url));
        DictationNotice notice = Assert.Single(overlay.Notices);
        Assert.Equal(DictationNoticeKind.Info, notice.Kind);
        Assert.Equal("Nothing was recognized — the recording is in History.", notice.Message);
        Assert.True(notice.WordsDidNotLand);
        Assert.Equal(1, sounds.AttentionPlays);
    }

    /// <summary>
    /// History can refuse the take - a full disk, a folder it may not write to. The take must
    /// not be deleted for it: it is left where the launch-time import looks, and the notice says
    /// so rather than pointing at a row that does not exist.
    /// </summary>
    [Fact]
    public async Task TestAWordlessTakeHistoryRefusesIsLeftForAutomaticRecovery()
    {
        capture.SetBufferedSamples(QuietWithAClick);
        historyKeeps = false;

        await Dictate(MakeController());

        Assert.Empty(archived);
        Assert.True(File.Exists(capture.Url), "the take History refused was deleted");
        DictationNotice notice = Assert.Single(overlay.Notices);
        Assert.Equal(
            "Nothing was recognized — nothing was inserted."
                + " The local take will be checked for automatic recovery on the next launch.",
            notice.Message);
        Assert.True(notice.WordsDidNotLand);
        Assert.Null(notice.RecoveryAudio);
    }

    [Fact]
    public async Task TestAnEmptyTakeOfSilenceNamesTheMicrophoneAndKeepsNothing()
    {
        capture.SetBufferedSamples(Quiet);
        DictationController controller = MakeController();
        controller.MicrophoneName = () => "Test Mic";

        await Dictate(controller);

        Assert.True(archived.Count == 0, "silence is not kept");
        DictationNotice notice = Assert.Single(overlay.Notices);
        Assert.Equal("Heard only silence from Test Mic.", notice.Message);
        // Orange: a muted, dead or wrong input needs the person to act.
        Assert.Equal(DictationNoticeKind.Warning, notice.Kind);
        Assert.True(notice.WordsDidNotLand);
        Assert.Null(notice.RecoveryAudio);
        Assert.Equal(1, sounds.AttentionPlays);
        Assert.False(File.Exists(capture.Url), "silence is deleted with its file");
    }

    /// <summary>
    /// Without a name the words change, never the kind: it is the same silence, and it is
    /// counted the same way.
    /// </summary>
    [Fact]
    public async Task TestSilenceFromAnUnnamedMicrophoneSaysNothingWasRecognized()
    {
        capture.SetBufferedSamples(Quiet);
        DictationController controller = MakeController();
        controller.MicrophoneName = () => null;

        await Dictate(controller);

        Assert.Empty(archived);
        DictationNotice notice = Assert.Single(overlay.Notices);
        Assert.Equal("Nothing was recognized — nothing was inserted.", notice.Message);
        Assert.Equal(DictationNoticeKind.Warning, notice.Kind);
        Assert.True(notice.WordsDidNotLand);
    }

    /// <summary>
    /// A take longer than the in-memory bound was recognized from its file, so its file is whole
    /// and answers the same question. Hands-free into a muted microphone for many minutes is
    /// exactly when the name matters.
    /// </summary>
    [Fact]
    public async Task TestALongTakeOfSilenceIsReadBackFromItsFileAndNamesTheMicrophone()
    {
        capture.SetBufferedSamples(null);
        DictationController controller = MakeController(readSamples: (_, _) => Task.FromResult(Quiet));
        controller.MicrophoneName = () => "Test Mic";

        await Dictate(controller);

        Assert.True(archived.Count == 0, "silence is not kept, however long");
        DictationNotice notice = Assert.Single(overlay.Notices);
        Assert.Equal("Heard only silence from Test Mic.", notice.Message);
        Assert.Equal(DictationNoticeKind.Warning, notice.Kind);
    }

    /// <summary>
    /// Without the samples, and without a way to read them back, there is no telling silence
    /// from sound, so the take is kept.
    /// </summary>
    [Fact]
    public async Task TestAnEmptyTakeWithoutPCMIsKeptRatherThanCalledSilence()
    {
        capture.SetBufferedSamples(null);
        DictationController controller = MakeController();
        controller.MicrophoneName = () => "Test Mic";

        await Dictate(controller);

        Assert.True(archived.Select(entry => entry.Text).SequenceEqual([""]));
        Assert.Equal(
            ["Nothing was recognized — the recording is in History."],
            overlay.Notices.Select(notice => notice.Message));
    }

    /// <summary>
    /// Recognition may level its input in place. The silence verdict must be about what the
    /// microphone heard, not about a hiss amplified thirty decibels on its way to the engine.
    /// </summary>
    [Fact]
    public async Task TestSilenceIsJudgedOnWhatTheMicrophoneHeardNotOnTheLevelledInput()
    {
        capture.SetBufferedSamples(Quiet.ToArray());
        var controller = new DictationController(
            capture: capture,
            transcribe: (_, _) => Task.FromResult(Nothing),
            transcribeSamples: (samples, _) =>
            {
                for (int index = 0; index < samples.Length; index += 1) { samples[index] *= 31.6f; }
                return Task.FromResult(Nothing);
            },
            inserter: inserter,
            overlay: overlay,
            sounds: sounds,
            pipeline: _ => new TextPipeline());
        controller.MicrophoneName = () => "Test Mic";

        await Dictate(controller);

        Assert.Equal(["Heard only silence from Test Mic."], overlay.Notices.Select(notice => notice.Message));
    }

    /// <summary>A take on disk at a path of its own, so parallel suites never share one.</summary>
    private sealed class TakeCapture : IAudioCapturing, IDisposable
    {
        private readonly Lock gate = new();
        private float[]? bufferedSamples;

        public string Url { get; } = Path.Combine(Path.GetTempPath(), $"empty-take-{Guid.NewGuid():N}.wav");

        public void SetBufferedSamples(float[]? samples) { lock (gate) { bufferedSamples = samples; } }

        public Task<string> StartRecording()
        {
            File.WriteAllBytes(Url, [0]);
            return Task.FromResult(Url);
        }

        public Task<(string Url, TimeSpan Duration)> StopRecording() =>
            Task.FromResult((Url, TimeSpan.FromSeconds(2)));

        public Task<float[]?> TakeBufferedSamples()
        {
            lock (gate)
            {
                float[]? samples = bufferedSamples;
                bufferedSamples = null;
                return Task.FromResult(samples);
            }
        }

        public Task AbortRecording() => Task.CompletedTask;

        public void Dispose()
        {
            try
            {
                File.Delete(Url);
            }
            catch (IOException)
            {
            }
        }
    }
}
