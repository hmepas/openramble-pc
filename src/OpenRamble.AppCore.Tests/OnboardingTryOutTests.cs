using DictationCore;
using OpenRamble.AppCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The try-out step, as a person watches it.
///
/// What is checked is that every stage of the chain has its own visible answer: the key has not
/// arrived, the key arrived, the microphone is live, the words are being recognized, the words
/// landed. Collapsing any two of those is how "nothing happens when I press the key" becomes
/// impossible to tell apart from "the recognition came back empty".
/// </summary>
public sealed class OnboardingTryOutTests
{
    private static OnboardingTryOut Make(
        bool held = false,
        bool everPressed = false,
        DictationState dictation = DictationState.Idle,
        bool succeeded = false,
        DictationHotkey hotkey = DictationHotkey.RightCtrl,
        bool buttonHeld = false,
        string? trackingFailure = null) =>
        OnboardingTryOut.Make(
            hotkey, held, everPressed, dictation, succeeded, buttonHeld, trackingFailure);

    /// <summary>
    /// Before anything happens the step says what to do and admits it has seen nothing.
    ///
    /// The admission is the point: a step that only says "hold the key" leaves a person who is
    /// already holding it with no idea that their press is not arriving.
    /// </summary>
    [Fact]
    public void NothingSeenYet()
    {
        OnboardingTryOut view = Make();

        Assert.Equal(OnboardingTryPhase.Waiting, view.Phase);
        Assert.StartsWith("Hold Right Ctrl, say a sentence, then let go.", view.Instruction);
        Assert.Equal("Press and hold Right Ctrl. Nothing has reached OpenRamble yet.", view.Status);
        Assert.Null(view.Tone);
        Assert.False(view.ShowsInputLevel);
    }

    /// <summary>
    /// The key is down and dictation has not started. This is the case the step exists for: the
    /// press reached us, and whatever is wrong is behind the keyboard, not in it.
    /// </summary>
    [Fact]
    public void KeyDownWithoutDictationStillReacts()
    {
        OnboardingTryOut view = Make(held: true, everPressed: true, dictation: DictationState.Idle);

        Assert.Equal(OnboardingTryPhase.KeyDown, view.Phase);
        Assert.Equal("Right Ctrl is down — OpenRamble sees the key.", view.Status);
        Assert.Equal(StatusColorRole.Processing, view.Tone);
        Assert.False(view.ShowsInputLevel);
    }

    [Fact]
    public void KeyDownWhileTheMicrophoneOpens()
    {
        OnboardingTryOut view = Make(held: true, everPressed: true, dictation: DictationState.Preparing);

        Assert.Equal(OnboardingTryPhase.KeyDown, view.Phase);
        Assert.Equal("Right Ctrl is down — turning on the microphone…", view.Status);
        Assert.False(view.ShowsInputLevel);
    }

    /// <summary>
    /// Recording is the only state that shows the level: a meter next to a microphone that is not
    /// running would sit at zero and read as a dead microphone.
    /// </summary>
    [Fact]
    public void RecordingShowsTheLevel()
    {
        OnboardingTryOut view = Make(held: true, everPressed: true, dictation: DictationState.Listening);

        Assert.Equal(OnboardingTryPhase.Recording, view.Phase);
        Assert.Equal("Right Ctrl is down — recording. Say a sentence, then let go.", view.Status);
        Assert.Equal(StatusColorRole.Recording, view.Tone);
        Assert.True(view.ShowsInputLevel);
    }

    [Fact]
    public void ReleasedIntoRecognition()
    {
        foreach (DictationState working in new[] { DictationState.Transcribing, DictationState.Inserting })
        {
            OnboardingTryOut view = Make(held: false, everPressed: true, dictation: working);

            Assert.Equal(OnboardingTryPhase.Working, view.Phase);
            Assert.Equal("Recognizing what you said…", view.Status);
            Assert.False(view.ShowsInputLevel);
        }
    }

    /// <summary>
    /// The key worked and the dictation did not. Saying "the key works" is what stops the person
    /// rebinding a key that was never the problem.
    /// </summary>
    [Fact]
    public void KeyWorkedButNoTextArrived()
    {
        OnboardingTryOut view = Make(held: false, everPressed: true, dictation: DictationState.Idle);

        Assert.Equal(OnboardingTryPhase.KeyReached, view.Phase);
        Assert.StartsWith("Right Ctrl reaches OpenRamble.", view.Status);
        Assert.Equal(StatusColorRole.Attention, view.Tone);
    }

    [Fact]
    public void SuccessKeepsTheOriginalPromise()
    {
        OnboardingTryOut view = Make(held: false, everPressed: true, succeeded: true);

        Assert.Equal(OnboardingTryPhase.Succeeded, view.Phase);
        Assert.Contains("That is the whole product. You are set up.", view.Status);
        Assert.Equal(StatusColorRole.Success, view.Tone);
    }

    /// <summary>
    /// A second press after a success must not take the success away: the words are in the box,
    /// and a step that unsays that is a step that undoes itself under the person's hands.
    /// </summary>
    [Fact]
    public void SuccessSurvivesTheNextPress()
    {
        OnboardingTryOut view = Make(
            held: true,
            everPressed: true,
            dictation: DictationState.Listening,
            succeeded: true);

        Assert.Equal(OnboardingTryPhase.Succeeded, view.Phase);
    }

    /// <summary>The chosen key is named everywhere, never "the dictation key".</summary>
    [Fact]
    public void TheKeyIsNamedByItsOwnName()
    {
        foreach (DictationHotkey hotkey in DictationHotkeyInfo.AllCases)
        {
            Assert.Contains(hotkey.Title, Make(hotkey: hotkey).Status);
            Assert.Contains(hotkey.Title, Make(hotkey: hotkey).Instruction);
            Assert.Contains(hotkey.Title, Make(hotkey: hotkey, held: true, everPressed: true).Status);
        }
    }

    /// <summary>
    /// Every reachable combination says something, and only a live microphone shows a meter.
    ///
    /// A blank status line is the failure this whole type exists to prevent, and an empty one is
    /// exactly what a missed branch would produce.
    /// </summary>
    [Fact]
    public void NoCombinationIsSilent()
    {
        foreach (bool held in new[] { true, false })
        {
            foreach (bool everPressed in new[] { true, false })
            {
                foreach (bool succeeded in new[] { true, false })
                {
                    foreach (DictationState dictation in Enum.GetValues<DictationState>())
                    {
                        OnboardingTryOut view = Make(held, everPressed, dictation, succeeded);

                        Assert.False(string.IsNullOrWhiteSpace(view.Status));
                        Assert.False(string.IsNullOrWhiteSpace(view.Instruction));
                        Assert.Equal(
                            view.Phase == OnboardingTryPhase.Recording,
                            view.ShowsInputLevel);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Tracking that never installed is said out loud, before any "nothing has arrived yet".
    ///
    /// This is the one fact the step cannot establish by waiting, and the one that makes waiting
    /// pointless: a hook the system refused looks exactly like a person who has not pressed
    /// anything, for the rest of the process.
    /// </summary>
    [Fact]
    public void TrackingThatNeverStartedIsNamed()
    {
        const string reason = "OpenRamble couldn't start watching the dictation key.";
        OnboardingTryOut view = Make(trackingFailure: reason);

        Assert.Equal(OnboardingTryPhase.TrackingUnavailable, view.Phase);
        Assert.Equal(reason, view.Status);
        Assert.Equal(StatusColorRole.Attention, view.Tone);
        Assert.False(view.ShowsInputLevel);
    }

    /// <summary>
    /// A key that arrives outranks the report that it cannot: the reason was recorded once at
    /// launch, and the evidence in front of the person now is worth more than it.
    /// </summary>
    [Fact]
    public void AKeyThatArrivesOutranksTheTrackingReport()
    {
        const string reason = "OpenRamble couldn't start watching the dictation key.";

        Assert.Equal(
            OnboardingTryPhase.Recording,
            Make(held: true, everPressed: true, dictation: DictationState.Listening, trackingFailure: reason)
                .Phase);

        // A dictation that is finishing outranks it too: a reason recorded at launch must not
        // paint over what is happening in front of the person right now.
        Assert.Equal(
            OnboardingTryPhase.Working,
            Make(dictation: DictationState.Transcribing, trackingFailure: reason).Phase);
    }

    /// <summary>
    /// The button says "button", never "the key is down".
    ///
    /// The badge is this step's evidence about the key, and a mouse press is not evidence about
    /// the key. Wording the hold as a keypress would make the one screen that exists to answer
    /// "does my keyboard reach OpenRamble" answer it wrongly.
    /// </summary>
    [Fact]
    public void HoldingTheButtonNeverClaimsTheKey()
    {
        OnboardingTryOut recording = Make(buttonHeld: true, dictation: DictationState.Listening);

        Assert.Equal(OnboardingTryPhase.ButtonHeld, recording.Phase);
        Assert.Equal(
            "Holding the button — recording. Say a sentence, then let go of the mouse.",
            recording.Status);
        Assert.Equal(StatusColorRole.Recording, recording.Tone);
        Assert.True(recording.ShowsInputLevel);
        Assert.DoesNotContain("Right Ctrl is down", recording.Status, StringComparison.Ordinal);

        OnboardingTryOut opening = Make(buttonHeld: true, dictation: DictationState.Preparing);

        Assert.Equal(OnboardingTryPhase.ButtonHeld, opening.Phase);
        Assert.Equal("Holding the button — turning on the microphone…", opening.Status);
        Assert.False(opening.ShowsInputLevel);
    }

    /// <summary>
    /// The button works when the key never arrives - that is the whole reason it exists.
    /// </summary>
    [Fact]
    public void TheButtonSpeaksOverADeadHook()
    {
        OnboardingTryOut view = Make(
            buttonHeld: true,
            dictation: DictationState.Listening,
            trackingFailure: "OpenRamble couldn't start watching the dictation key.");

        Assert.Equal(OnboardingTryPhase.ButtonHeld, view.Phase);
        Assert.True(view.ShowsInputLevel);
    }

    /// <summary>Success still outranks a hold, whichever hand is holding.</summary>
    [Fact]
    public void SuccessSurvivesAButtonHold()
    {
        OnboardingTryOut view = Make(
            buttonHeld: true,
            dictation: DictationState.Listening,
            succeeded: true);

        Assert.Equal(OnboardingTryPhase.Succeeded, view.Phase);
    }

    /// <summary>The instruction names both ways in, so neither has to be discovered.</summary>
    [Fact]
    public void TheInstructionOffersTheButtonToo()
    {
        Assert.Contains("hold the button", Make().Instruction, StringComparison.Ordinal);
    }

    /// <summary>
    /// The wider table, with the button and a dead hook in it: still nothing silent, and still a
    /// meter only where a microphone is genuinely running.
    /// </summary>
    [Fact]
    public void NoCombinationIsSilentWithTheButtonAndADeadHook()
    {
        foreach (bool held in new[] { true, false })
        {
            foreach (bool buttonHeld in new[] { true, false })
            {
                foreach (string? failure in new[] { null, "tracking is down" })
                {
                    foreach (DictationState dictation in Enum.GetValues<DictationState>())
                    {
                        OnboardingTryOut view = Make(
                            held: held,
                            everPressed: held,
                            dictation: dictation,
                            buttonHeld: buttonHeld,
                            trackingFailure: failure);

                        Assert.False(string.IsNullOrWhiteSpace(view.Status));
                        Assert.False(string.IsNullOrWhiteSpace(view.Instruction));
                        bool live = dictation == DictationState.Listening
                            && (held || buttonHeld);
                        Assert.Equal(live, view.ShowsInputLevel);
                    }
                }
            }
        }
    }
}
