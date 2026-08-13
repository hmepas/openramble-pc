namespace DictationCore.Tests;

/// <summary>
/// Sound means "look at me", and nothing else.
///
/// A dictation that works needs no sound: the panel already shows the
/// recording, and the text appearing at the cursor is the confirmation. A
/// chime on every start and every stop trains the person to ignore the one
/// case where a sound genuinely matters - the words did not reach the field.
/// So: a notice is surfaced ⟺ a sound plays. Silence means it worked.
/// </summary>
[Collection(DictationControllerCollection.Name)]
public sealed class SoundPolicyTests
{
    private sealed class AttentionSounds : ISounding
    {
        private int plays;

        public int Plays => Volatile.Read(ref plays);

        public Task PlayAttention()
        {
            Interlocked.Increment(ref plays);
            return Task.CompletedTask;
        }
    }

    private readonly ScriptedCapture capture = new();
    private readonly ScriptedInserter inserter = new();
    private readonly CollectingOverlay overlay = new();
    private readonly AttentionSounds sounds = new();

    private DictationController MakeController(
        string recognized = "words",
        bool allowPressReturnCommand = false) =>
        new(
            capture: capture,
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                recognized,
                audioDuration: TimeSpan.FromSeconds(2),
                processingDuration: TimeSpan.FromSeconds(0.1))),
            inserter: inserter,
            overlay: overlay,
            sounds: sounds,
            pipeline: () => new TextPipeline(allowPressReturnCommand: allowPressReturnCommand));

    private static async Task RunFullDictation(DictationController controller)
    {
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        for (int i = 0; i < 12; i += 1)
        {
            await Task.Yield();
            await Task.Delay(5);
        }

        controller.Stop();
        for (int i = 0; i < 200 && controller.State != DictationState.Idle; i += 1)
        {
            await Task.Yield();
            await Task.Delay(5);
        }
    }

    /// <summary>The whole point: a dictation that lands is silent from beginning to end.</summary>
    [Fact]
    public async Task TestSuccessfulDictationIsCompletelySilent()
    {
        var controller = MakeController();
        await RunFullDictation(controller);

        // The text must have landed.
        Assert.Equal(["Words"], inserter.InsertedTexts);
        // Success needs no sound - the text at the cursor is the receipt.
        Assert.Equal(0, sounds.Plays);
    }

    /// <summary>The one case the person must not miss: the words did not reach the field.</summary>
    [Fact]
    public async Task TestTextThatDidNotLandSounds()
    {
        var controller = MakeController();
        inserter.SetInsertError(new TextInsertionError.TargetUnavailable());
        await RunFullDictation(controller);

        // A person looking at their editor learns of this only by ear.
        Assert.Equal(1, sounds.Plays);
        // And the panel offers the text back.
        Assert.Contains(overlay.Notices, notice => notice.RecoverableText is not null);
    }

    /// <summary>Spoke, and nothing came out: no text landed, so it sounds.</summary>
    [Fact]
    public async Task TestEmptyRecognitionSounds()
    {
        var controller = MakeController(recognized: string.Empty);
        await RunFullDictation(controller);

        Assert.Equal(1, sounds.Plays);
    }

    /// <summary>
    /// Escape is the person's own decision. They are already looking at the
    /// screen; a sound here would be the app arguing with them.
    /// </summary>
    [Fact]
    public async Task TestCancellationStaysSilent()
    {
        var controller = MakeController();
        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        for (int i = 0; i < 12; i += 1)
        {
            await Task.Yield();
            await Task.Delay(5);
        }

        controller.Cancel();
        for (int i = 0; i < 200 && controller.State != DictationState.Idle; i += 1)
        {
            await Task.Yield();
            await Task.Delay(5);
        }

        Assert.Equal(0, sounds.Plays);
    }

    /// <summary>
    /// The words landed; only the Return press failed. The person sees their
    /// text in the field - a warning is shown, but sounding it would be an
    /// alarm about nothing lost.
    /// </summary>
    [Fact]
    public async Task TestLandedTextWithFailedReturnStaysSilent()
    {
        inserter.SetReturnError(new TextInsertionError.ModifiersStillHeld());
        var controller = MakeController(recognized: "готово отправь", allowPressReturnCommand: true);
        await RunFullDictation(controller);

        // The text itself must have landed.
        Assert.NotEmpty(inserter.InsertedTexts);
        // The warning is still shown.
        Assert.Contains(overlay.Notices, notice => notice.Kind == DictationNoticeKind.Warning);
        // Words in the field need no alarm.
        Assert.Equal(0, sounds.Plays);
    }

    /// <summary>
    /// Exactly one sound per session, no matter how many notices the failure
    /// path produces on the way out.
    /// </summary>
    [Fact]
    public async Task TestOneSessionNeverSoundsTwice()
    {
        var controller = MakeController();
        inserter.SetInsertError(new TextInsertionError.TargetUnavailable());
        await RunFullDictation(controller);
        int first = sounds.Plays;

        inserter.SetInsertError(null);
        await RunFullDictation(controller);
        int second = sounds.Plays;

        Assert.Equal(1, first);
        // The healthy dictation that followed added nothing.
        Assert.Equal(1, second);
    }
}
