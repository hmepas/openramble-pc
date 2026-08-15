using DictationCore;
using OpenRamble.AppCore;

namespace OpenRamble.LinuxInput.Tests;

/// <summary>
/// Getting the recognized text into somebody else's window, and giving the clipboard back.
/// </summary>
/// <remarks>
/// Every one of these runs against a desktop that does not exist, because the failures worth
/// testing are the ones a real desktop only produces when it is broken: a compositor without the
/// virtual-keyboard protocol, a wl-clipboard too old to know <c>--sensitive</c>, a person who
/// copies something of their own inside the restore window, an image on the clipboard that no
/// text tool can put back.
/// </remarks>
[Collection(LinuxInputCollection.Name)]
public sealed class LinuxTextInserterTests
{
    /// <summary>Two sentences - the ordinary dictation, and short enough to be typed.</summary>
    private const string Short = "The quick brown fox jumps over the lazy dog.";

    /// <summary>Past <c>InsertionPathPolicy.SynthesizedKeystrokeLimit</c>: the clipboard path.</summary>
    private static readonly string Long = new('x', InsertionPathPolicy.SynthesizedKeystrokeLimit + 1);

    private static readonly TimeSpan QuickRestore = TimeSpan.FromMilliseconds(60);

    [Fact]
    public async Task TheOrdinaryDictationIsTypedAndTheClipboardIsNeverTouched()
    {
        // The whole reason the direct path exists: nothing to borrow, nothing to lose, and nothing
        // for a clipboard manager to see.
        var desktop = new FakeWaylandDesktop();
        desktop.SomebodyCopies("something the person copied");
        var inserter = new LinuxTextInserter(desktop);

        await inserter.Insert(Short, inserter.FrontmostApplication());

        Assert.Equal([Short], desktop.Typed);
        Assert.Equal("something the person copied", desktop.ClipboardText);
        Assert.DoesNotContain(desktop.Runs, run => run.Executable is WaylandClipboard.Copy or WaylandClipboard.Paste);
    }

    [Fact]
    public void TheCommandLinesAreExactly()
    {
        // Asserted argument for argument, because these are the whole specification of what reaches
        // somebody else's window - and because "-" and "/dev/stdin" are what keep the dictation out
        // of the process table.
        Assert.Equal(["-"], SynthesizedKeyboard.TypeArguments(SynthesizedKeyboard.Wtype));
        Assert.Equal(
            ["type", "--file", "/dev/stdin"],
            SynthesizedKeyboard.TypeArguments(SynthesizedKeyboard.Ydotool));
        Assert.Equal(
            ["-M", "ctrl", "-k", "v", "-m", "ctrl"],
            SynthesizedKeyboard.PasteArguments(SynthesizedKeyboard.Wtype));
        // Evdev codes with the state after the colon: Ctrl down, V down, V up, Ctrl up. Any other
        // order leaves a modifier stuck down in the person's session.
        Assert.Equal(
            ["key", "29:1", "47:1", "47:0", "29:0"],
            SynthesizedKeyboard.PasteArguments(SynthesizedKeyboard.Ydotool));
        Assert.Equal(["-k", "Return"], SynthesizedKeyboard.ReturnArguments(SynthesizedKeyboard.Wtype));
        Assert.Equal(["key", "28:1", "28:0"], SynthesizedKeyboard.ReturnArguments(SynthesizedKeyboard.Ydotool));
        Assert.Equal(["--no-newline", "--type", "text/plain"], WaylandClipboard.ReadArguments());
        Assert.Equal(["--sensitive", "--type", "text/plain"], WaylandClipboard.WriteArguments(sensitive: true));
        Assert.Equal(["--type", "text/plain"], WaylandClipboard.WriteArguments(sensitive: false));
        Assert.Equal(["--clear"], WaylandClipboard.ClearArguments());
    }

    [Fact]
    public async Task TheDictationIsNeverAnArgument()
    {
        // /proc/<pid>/cmdline is readable by everything else the person is running. A dictation
        // product that puts what was said into the process table has leaked it.
        var desktop = new FakeWaylandDesktop();
        var inserter = new LinuxTextInserter(desktop, QuickRestore);

        await inserter.Insert(Short, inserter.FrontmostApplication());
        await inserter.Insert(Long, inserter.FrontmostApplication());

        foreach (CommandRequest run in desktop.Runs)
        {
            Assert.DoesNotContain(run.Arguments, argument =>
                argument.Contains(Short, StringComparison.Ordinal)
                || argument.Contains(Long, StringComparison.Ordinal));
        }

        Assert.Contains(desktop.Runs, run => run.Input == Short);
    }

    [Fact]
    public async Task LongTextGoesThroughTheClipboardAndThePersonGetsTheirsBack()
    {
        var desktop = new FakeWaylandDesktop();
        desktop.SomebodyCopies("a link the person was about to paste");
        var inserter = new LinuxTextInserter(desktop, QuickRestore);

        await inserter.Insert(Long, inserter.FrontmostApplication());

        // At this instant the dictation is on the clipboard and Ctrl+V has been sent.
        Assert.Equal(1, desktop.Pastes);
        Assert.Empty(desktop.Typed);

        Assert.True(Until.True(() => desktop.ClipboardText == "a link the person was about to paste"));
        // And the promise LinuxClipboardRestrictions actually makes: the dictation is not there
        // afterwards.
        Assert.True(LinuxClipboardRestrictions.ClearsAfterPaste);
        Assert.NotEqual(Long, desktop.ClipboardText);
    }

    [Fact]
    public async Task AClipboardThatWasEmptyIsEmptyAgain()
    {
        var desktop = new FakeWaylandDesktop();
        var inserter = new LinuxTextInserter(desktop, QuickRestore);

        await inserter.Insert(Long, inserter.FrontmostApplication());

        Assert.True(Until.True(() => desktop.ClipboardText is null));
        Assert.Contains(desktop.Runs, run =>
            run.Executable == WaylandClipboard.Copy && run.Arguments.Contains("--clear"));
    }

    [Fact]
    public async Task TheWriteAsksNotToBeRecorded()
    {
        // The one opt-out any Linux clipboard manager honours, and only Klipper does. Worth the one
        // extra flag; not worth calling a guarantee.
        var desktop = new FakeWaylandDesktop();
        var inserter = new LinuxTextInserter(desktop, TimeSpan.FromSeconds(30));

        await inserter.Insert(Long, inserter.FrontmostApplication());

        Assert.True(desktop.LastWriteWasSensitive);
        Assert.Contains(LinuxClipboardRestrictions.KdePasswordManagerHint, LinuxClipboardRestrictions.BestEffortFormats);
        Assert.Empty(LinuxClipboardRestrictions.RequiredFormats);
    }

    [Fact]
    public async Task AWlClipboardTooOldToKnowTheMarkerStillInserts()
    {
        using var diagnostics = new DiagnosticRecorder();
        var desktop = new FakeWaylandDesktop { SupportsSensitive = false };
        var inserter = new LinuxTextInserter(desktop, QuickRestore);

        await inserter.Insert(Long, inserter.FrontmostApplication());

        // The dictation must not fail on a supported desktop over a marker that one desktop
        // environment reads.
        Assert.Equal(1, desktop.Pastes);
        Assert.NotNull(diagnostics.Event("insert.clipboard.no-sensitive-hint"));
    }

    [Fact]
    public async Task AClipboardThePersonHasSinceWrittenToIsLeftAlone()
    {
        var desktop = new FakeWaylandDesktop();
        desktop.SomebodyCopies("theirs");
        var inserter = new LinuxTextInserter(desktop, TimeSpan.FromMilliseconds(300));

        await inserter.Insert(Long, inserter.FrontmostApplication());
        desktop.SomebodyCopies("something they copied a moment later");

        // Restoring over their write would be worse than doing nothing: what they copied last is
        // what they meant to paste next.
        await Task.Delay(TimeSpan.FromMilliseconds(700));
        Assert.Equal("something they copied a moment later", desktop.ClipboardText);
    }

    [Fact]
    public async Task AnImageOnTheClipboardIsReportedRatherThanQuietlyLost()
    {
        // wl-copy cannot be handed arbitrary bytes back with their MIME type through this seam, so
        // the image is gone either way. Leaving the dictation there instead would be worse, and
        // calling it a success would be worst.
        var desktop = new FakeWaylandDesktop { ClipboardHasImage = true };
        TextInsertionError? reported = null;
        var inserter = new LinuxTextInserter(desktop, QuickRestore, error => reported = error);

        await inserter.Insert(Long, inserter.FrontmostApplication());

        Assert.True(Until.True(() => reported is not null));
        Assert.IsType<TextInsertionError.InsertedButClipboardRestoreFailed>(reported);
        Assert.Null(desktop.ClipboardText);
    }

    [Fact]
    public async Task ASecondDictationInsideTheRestoreWindowDoesNotEatTheClipboard()
    {
        // Two short replies in a chat. The second write lands on OUR text; snapshotting the board
        // there would capture the first dictation, and the person's own content would be gone for
        // good.
        var desktop = new FakeWaylandDesktop();
        desktop.SomebodyCopies("what they had");
        var inserter = new LinuxTextInserter(desktop, TimeSpan.FromMilliseconds(400));
        TargetApplication? target = inserter.FrontmostApplication();

        await inserter.Insert(Long, target);
        await inserter.Insert(Long + "and more", target);

        Assert.True(Until.True(() => desktop.ClipboardText == "what they had"));
    }

    [Fact]
    public async Task WhenTheFirstToolCannotTypeTheSecondDoes()
    {
        // wtype exits before its first keystroke where the compositor will not give it the
        // protocol - GNOME, or anything on X11 - which is what makes falling through safe.
        using var diagnostics = new DiagnosticRecorder();
        var desktop = new FakeWaylandDesktop();
        desktop.Broken.Add(SynthesizedKeyboard.Wtype);
        var inserter = new LinuxTextInserter(desktop);

        await inserter.Insert(Short, inserter.FrontmostApplication());

        Assert.Equal([Short], desktop.Typed);
        Assert.Equal(
            [SynthesizedKeyboard.Wtype, SynthesizedKeyboard.Ydotool],
            desktop.Runs.Select(run => run.Executable));
        Assert.Equal(SynthesizedKeyboard.Wtype, diagnostics.Field("insert.tool-failed", "tool"));
        Assert.Equal("true", diagnostics.Field("insert.tool-failed", "installed"));
    }

    [Fact]
    public async Task AToolThatIsNotInstalledIsSkipped()
    {
        using var diagnostics = new DiagnosticRecorder();
        var desktop = new FakeWaylandDesktop();
        desktop.Missing.Add(SynthesizedKeyboard.Wtype);
        var inserter = new LinuxTextInserter(desktop);

        await inserter.Insert(Short, inserter.FrontmostApplication());

        Assert.Equal([Short], desktop.Typed);
        Assert.Equal("false", diagnostics.Field("insert.tool-failed", "installed"));
    }

    [Fact]
    public async Task TheToolThatWorkedIsTriedFirstNextTime()
    {
        var desktop = new FakeWaylandDesktop();
        desktop.Missing.Add(SynthesizedKeyboard.Wtype);
        var inserter = new LinuxTextInserter(desktop);
        TargetApplication? target = inserter.FrontmostApplication();
        await inserter.Insert(Short, target);
        desktop.Runs.Clear();

        await inserter.Insert(Short, target);

        // Otherwise the missing tool is paid for on every dictation, and the failure written to the
        // log on every dictation with it.
        Assert.Equal([SynthesizedKeyboard.Ydotool], desktop.Runs.Select(run => run.Executable));
    }

    [Fact]
    public async Task WithNoWayToTypeAtAllTheFailureIsLoud()
    {
        using var diagnostics = new DiagnosticRecorder();
        var desktop = new FakeWaylandDesktop();
        desktop.Missing.Add(SynthesizedKeyboard.Wtype);
        desktop.Missing.Add(SynthesizedKeyboard.Ydotool);
        var inserter = new LinuxTextInserter(desktop);

        await Assert.ThrowsAsync<TextInsertionError.TargetUnavailable>(
            () => inserter.Insert(Short, inserter.FrontmostApplication()));

        // A dictation that produced nothing and said nothing is the one failure this product must
        // never have.
        Assert.NotNull(diagnostics.Event("insert.failed"));
        Assert.Equal("SynthesizedKeystrokes", diagnostics.Field("insert.failed", "path"));
    }

    [Fact]
    public async Task AClipboardThatCannotBeWrittenStopsBeforeTheKeystroke()
    {
        // The order is the product: nothing irreversible happens until the text is actually on the
        // clipboard, or Ctrl+V would paste whatever was there before.
        var desktop = new FakeWaylandDesktop();
        desktop.Missing.Add(WaylandClipboard.Copy);
        var inserter = new LinuxTextInserter(desktop, QuickRestore);

        await Assert.ThrowsAsync<TextInsertionError.ClipboardWriteFailed>(
            () => inserter.Insert(Long, inserter.FrontmostApplication()));

        Assert.Equal(0, desktop.Pastes);
    }

    [Fact]
    public async Task ModifiersStillHeldAreWaitedForAndThenReported()
    {
        // Ctrl+V sent while the person still holds Shift is somebody else's combination. Windows
        // asks GetAsyncKeyState; here the answer comes from the evdev monitor, and a build with no
        // monitor waits for nothing rather than pretending.
        var desktop = new FakeWaylandDesktop();
        var inserter = new LinuxTextInserter(
            desktop,
            QuickRestore,
            heldModifiers: () => HotkeyModifiers.Shift);

        await Assert.ThrowsAsync<TextInsertionError.ModifiersStillHeld>(
            () => inserter.Insert(Short, inserter.FrontmostApplication()));

        Assert.Empty(desktop.Typed);
    }

    [Fact]
    public async Task TheModifierWaitEndsWhenTheKeyComesUp()
    {
        var desktop = new FakeWaylandDesktop();
        HotkeyModifiers held = HotkeyModifiers.Control;
        var inserter = new LinuxTextInserter(desktop, QuickRestore, heldModifiers: () => held);
        Task insertion = inserter.Insert(Short, inserter.FrontmostApplication());

        await Task.Delay(TimeSpan.FromMilliseconds(30));
        held = HotkeyModifiers.None;
        await insertion;

        Assert.Equal([Short], desktop.Typed);
    }

    [Fact]
    public async Task ThePathIsChosenByTheSharedPolicyAndNotByAThresholdOfItsOwn()
    {
        var desktop = new FakeWaylandDesktop();
        var inserter = new LinuxTextInserter(desktop, QuickRestore);
        TargetApplication? target = inserter.FrontmostApplication();

        await inserter.Insert(new string('a', InsertionPathPolicy.SynthesizedKeystrokeLimit), target);
        Assert.Single(desktop.Typed);
        Assert.Equal(0, desktop.Pastes);

        await inserter.Insert(new string('a', InsertionPathPolicy.SynthesizedKeystrokeLimit + 1), target);
        Assert.Single(desktop.Typed);
        Assert.Equal(1, desktop.Pastes);
    }

    [Fact]
    public async Task NothingToInsertDoesNothingAndSaysSo()
    {
        using var diagnostics = new DiagnosticRecorder();
        var desktop = new FakeWaylandDesktop();
        var inserter = new LinuxTextInserter(desktop);

        await inserter.Insert(string.Empty, inserter.FrontmostApplication());

        Assert.Empty(desktop.Runs);
        Assert.Equal("none", diagnostics.Field("insert.end", "path"));
    }

    [Fact]
    public async Task WithoutATargetNothingIsInserted()
    {
        using var diagnostics = new DiagnosticRecorder();
        var desktop = new FakeWaylandDesktop();
        var inserter = new LinuxTextInserter(desktop);

        await Assert.ThrowsAsync<TextInsertionError.TargetUnavailable>(
            () => inserter.Insert(Short, null));

        Assert.Empty(desktop.Runs);
        Assert.Equal("TargetUnavailable", diagnostics.Field("insert.refused", "reason"));
    }

    [Fact]
    public void TheTargetIsAPlaceholderBecauseWaylandHasNoAnswer()
    {
        // No client may ask which surface has focus. A null target would refuse every dictation, so
        // what is returned is a target with no process and no name - and PromptMode reads the name,
        // finds nothing, and falls back to its own safe default.
        var inserter = new LinuxTextInserter(new FakeWaylandDesktop());

        TargetApplication? target = inserter.FrontmostApplication();

        Assert.NotNull(target);
        Assert.Equal(0, target.ProcessIdentifier);
        Assert.Null(target.LocalizedName);
        Assert.Equal(ApplicationCategory.Unknown, PromptModePolicy.Category(target));
    }

    [Fact]
    public async Task ReturnIsPressedForTheDictationThatEndedWithSend()
    {
        var desktop = new FakeWaylandDesktop();
        var inserter = new LinuxTextInserter(desktop);

        await inserter.PressReturn();

        Assert.Equal(1, desktop.Returns);
    }

    [Fact]
    public void CopyingForThePersonGoesThroughTheSameWriter()
    {
        // "Copy last as spoken" and the recent-dictation menu. A second clipboard writer elsewhere
        // in the application would be a second chance to forget the do-not-record marker.
        var desktop = new FakeWaylandDesktop();
        var inserter = new LinuxTextInserter(desktop);

        inserter.CopyForPerson(Short);

        Assert.Equal(Short, desktop.ClipboardText);
        Assert.True(desktop.LastWriteWasSensitive);
    }

    [Fact]
    public async Task TheSpeedReportGetsTheMomentTheTextLanded()
    {
        // The mark exists so the readout measures up to the keystroke and not up to the end of a
        // second of clipboard protection.
        var desktop = new FakeWaylandDesktop();
        var inserter = new LinuxTextInserter(desktop, TimeSpan.FromSeconds(5));

        InsertionMarks marks = await inserter.InsertReportingMarks(Long, inserter.FrontmostApplication());

        Assert.NotNull(marks.PasteDispatchedAt);
    }
}
