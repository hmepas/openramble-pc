using DictationCore;
using OpenRamble.AppCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The decision half of <c>TextInserterTests</c>: everything that file checks without touching a
/// clipboard or posting a key.
/// </summary>
public sealed class TextInsertionPolicyTests
{
    private static readonly TargetApplication Target = new("Notepad", 4242, "Notepad");

    private static InsertionEnvironment Frontmost(bool isProtected = false) =>
        new(Target.ProcessIdentifier, isProtected);

    /// <summary>
    /// Protected input: an elevated window, the macOS secure-input case recast. Not a failure but
    /// a normal situation - and the clipboard may not be touched for it.
    /// </summary>
    [Fact]
    public void Scenario001()
    {
        Assert.Equal(
            InsertionRefusal.ProtectedTarget,
            TextInsertionPolicy.BeforeClipboard(Target, targetIsAlive: true, Frontmost(isProtected: true)));
    }

    /// <summary>
    /// The receiving application was closed while recognition was running.
    ///
    /// Inserting "somewhere" is not an option: there is an arbitrary window in front now, up to
    /// somebody else's password field. And the clipboard must stay untouched - otherwise the
    /// person loses what they had copied to an insertion that never happened.
    /// </summary>
    [Fact]
    public void Scenario003()
    {
        Assert.Equal(
            InsertionRefusal.TargetUnavailable,
            TextInsertionPolicy.BeforeClipboard(Target, targetIsAlive: false, Frontmost()));
    }

    [Fact]
    public void Scenario004()
    {
        Assert.True(TextInsertionPolicy.IsNoOp(string.Empty));
        Assert.False(TextInsertionPolicy.IsNoOp("hello"));
    }

    [Fact]
    public void Scenario005()
    {
        Assert.Null(TextInsertionPolicy.BeforeClipboard(Target, targetIsAlive: true, Frontmost()));
    }

    [Fact]
    public void Scenario006()
    {
        Assert.Equal(
            InsertionRefusal.TargetUnavailable,
            TextInsertionPolicy.BeforeClipboard(null, targetIsAlive: true, Frontmost()));
    }

    // MARK: - Waiting for modifiers

    /// <summary>
    /// Every hotkey that raises a chord modifier must be in the wait mask.
    ///
    /// This is the list that is easy to "tidy up" without noticing that one of its entries is a
    /// key offered as the hotkey. On macOS that was Fn: without <c>.maskSecondaryFn</c> the wait
    /// ended while the key was still held, and Cmd+V reached the application as Fn+Cmd+V - a
    /// completely different combination.
    /// </summary>
    [Fact]
    public void Scenario007()
    {
        foreach (DictationHotkey hotkey in DictationHotkeyInfo.AllCases)
        {
            if (hotkey.Kind == HotkeyModifiers.None)
            {
                // Caps Lock and F13 raise no chord: holding them cannot corrupt Ctrl+V.
                continue;
            }

            Assert.False(
                ModifierReleasePolicy.IsClear(hotkey.Kind),
                $"{hotkey.Title} is offered as a hotkey and is missing from the wait mask");
        }
    }

    [Fact]
    public void Scenario008()
    {
        Assert.False(ModifierReleasePolicy.IsClear(HotkeyModifiers.Control));
        Assert.False(ModifierReleasePolicy.IsClear(HotkeyModifiers.Alt));
        Assert.False(ModifierReleasePolicy.IsClear(HotkeyModifiers.Shift));
        Assert.False(ModifierReleasePolicy.IsClear(HotkeyModifiers.Windows));
        Assert.True(ModifierReleasePolicy.IsClear(HotkeyModifiers.None));
    }

    [Fact]
    public void Scenario010()
    {
        foreach (HotkeyModifiers modifier in new[]
        {
            HotkeyModifiers.Control, HotkeyModifiers.Alt, HotkeyModifiers.Shift, HotkeyModifiers.Windows,
        })
        {
            Assert.True((ModifierReleasePolicy.Watched & modifier) == modifier, $"{modifier} is not watched");
        }

        Assert.True(ModifierReleasePolicy.PollLimit > 0);
        Assert.True(ModifierReleasePolicy.PollInterval > TimeSpan.Zero);
        // The whole wait must stay well under a person's patience, and well over a normal
        // release.
        Assert.True(ModifierReleasePolicy.PollLimit * ModifierReleasePolicy.PollInterval
            <= TimeSpan.FromSeconds(1));
    }

    // MARK: - Which way the text travels

    /// <summary>
    /// The common dictation never touches the clipboard.
    ///
    /// This is the one place where the Windows build is better off than the macOS one, and the
    /// reason is worth pinning: a clipboard that is never borrowed can never be lost, and a
    /// dictation that never reaches the clipboard cannot leak from it.
    /// </summary>
    [Fact]
    public void Scenario011()
    {
        Assert.Equal(InsertionPath.SynthesizedKeystrokes, InsertionPathPolicy.Choose(1));
        Assert.Equal(
            InsertionPath.SynthesizedKeystrokes,
            InsertionPathPolicy.Choose(InsertionPathPolicy.SynthesizedKeystrokeLimit));
    }

    /// <summary>
    /// A paragraph goes through the clipboard: a few hundred synthesised events interleave with
    /// real typing and take visible time to land.
    /// </summary>
    [Fact]
    public void Scenario012()
    {
        Assert.Equal(
            InsertionPath.ClipboardPaste,
            InsertionPathPolicy.Choose(InsertionPathPolicy.SynthesizedKeystrokeLimit + 1));
        Assert.Equal(InsertionPath.ClipboardPaste, InsertionPathPolicy.Choose(10_000));
    }

    // MARK: - The last look before the irreversible paste

    /// <summary>
    /// The active application changed between the clipboard write and the paste. The dictation
    /// must not land in the window that took the focus.
    /// </summary>
    [Fact]
    public void Scenario013()
    {
        Assert.Equal(
            InsertionRefusal.TargetChanged,
            TextInsertionPolicy.Recheck(Target, new InsertionEnvironment(999, TargetIsProtected: false)));
    }

    /// <summary>Protection can appear between the two checks - a password prompt takes the focus.</summary>
    [Fact]
    public void Scenario014()
    {
        Assert.Equal(
            InsertionRefusal.ProtectedTarget,
            TextInsertionPolicy.Recheck(Target, Frontmost(isProtected: true)));
    }

    [Fact]
    public void Scenario015()
    {
        Assert.Null(TextInsertionPolicy.Recheck(Target, Frontmost()));
    }

    /// <summary>
    /// Every refusal reaches the dictation core as an error it already knows: a refusal with
    /// nowhere to go would be a silently dropped dictation.
    /// </summary>
    [Fact]
    public void Scenario016()
    {
        Assert.IsType<TextInsertionError.TargetUnavailable>(
            TextInsertionPolicy.ToError(InsertionRefusal.TargetUnavailable));
        Assert.IsType<TextInsertionError.TargetChanged>(
            TextInsertionPolicy.ToError(InsertionRefusal.TargetChanged));
        Assert.IsType<TextInsertionError.ModifiersStillHeld>(
            TextInsertionPolicy.ToError(InsertionRefusal.ModifiersStillHeld));
        // UIPI is the Windows form of secure input: you cannot insert there, and it is not a
        // breakage.
        Assert.IsType<TextInsertionError.SecureInputActive>(
            TextInsertionPolicy.ToError(InsertionRefusal.ProtectedTarget));
    }
}
