using OpenRamble.AppCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// Virtual-key codes in the form the low-level hook reports them.
/// </summary>
/// <remarks>
/// The macOS test file starts with a table of modifier bit masks, because there the shared flag
/// said "a key of this type is down" and a side bit said which one. Windows needs no such table:
/// the code IS the side.
/// </remarks>
file static class Vk
{
    public const int LeftControl = 0xA2;
    public const int RightControl = 0xA3;
    public const int LeftMenu = 0xA4;
    public const int RightMenu = 0xA5;
    public const int LeftShift = 0xA0;
    public const int Capital = 0x14;
    public const int F12 = 0x7B;
    public const int F13 = 0x7C;
    public const int LetterC = 0x43;
}

public sealed class HotkeyGestureMachineTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch.AddSeconds(1_000_000);

    private static HotkeyEvent Down(
        DictationHotkey hotkey,
        HotkeyModifiers held,
        double after = 0) =>
        new(hotkey.VirtualKey, IsDown: true, held, Start.AddSeconds(after));

    private static HotkeyEvent Up(
        DictationHotkey hotkey,
        HotkeyModifiers held,
        double after = 0) =>
        new(hotkey.VirtualKey, IsDown: false, held, Start.AddSeconds(after));

    private static HotkeyEvent Foreign(
        int virtualKey,
        HotkeyModifiers held,
        double after = 0,
        bool isDown = true) =>
        new(virtualKey, isDown, held, Start.AddSeconds(after));

    private static HotkeyAction ReleaseAfter(double seconds) =>
        HotkeyAction.Release(TimeSpan.FromMilliseconds(Math.Round(seconds * 1000)));

    // MARK: - Shortcuts are not dictation

    /// <summary>
    /// Ctrl+C: modifier pressed, then a letter. This is a shortcut, not a dictation - the
    /// started gesture ends quietly, without insertion and without messages.
    /// </summary>
    [Fact]
    public void Scenario001()
    {
        var machine = new HotkeyGestureMachine(DictationHotkey.RightCtrl);

        Assert.Equal(HotkeyAction.Press, machine.Handle(Down(DictationHotkey.RightCtrl, HotkeyModifiers.Control)));
        Assert.Equal(
            HotkeyAction.AbortShortcut,
            machine.Handle(Foreign(Vk.LetterC, HotkeyModifiers.Control, after: 0.05)));
        // Releasing the modifier after the shortcut does not insert anything.
        Assert.Equal(
            HotkeyAction.None,
            machine.Handle(Up(DictationHotkey.RightCtrl, HotkeyModifiers.None, after: 0.1)));
    }

    /// <summary>
    /// Ctrl+C, then Ctrl+V within the double-tap window. An earlier version turned the second
    /// press into recording without holding - in the background and forever.
    /// </summary>
    [Fact]
    public void Scenario002()
    {
        var machine = new HotkeyGestureMachine(DictationHotkey.RightCtrl);

        machine.Handle(Down(DictationHotkey.RightCtrl, HotkeyModifiers.Control));
        Assert.Equal(
            HotkeyAction.AbortShortcut,
            machine.Handle(Foreign(Vk.LetterC, HotkeyModifiers.Control, after: 0.04)));
        machine.Handle(Up(DictationHotkey.RightCtrl, HotkeyModifiers.None, after: 0.08));

        // The second shortcut immediately follows: press, but NOT doubleTap.
        Assert.Equal(
            HotkeyAction.Press,
            machine.Handle(Down(DictationHotkey.RightCtrl, HotkeyModifiers.Control, after: 0.15)));
    }

    /// <summary>
    /// A chord of modifiers (Alt already held, Ctrl added) is not a press: the person is
    /// reaching for Ctrl+Alt+…, not for dictation.
    /// </summary>
    [Fact]
    public void Scenario003()
    {
        var machine = new HotkeyGestureMachine(DictationHotkey.RightCtrl);

        Assert.Equal(
            HotkeyAction.None,
            machine.Handle(Down(DictationHotkey.RightCtrl, HotkeyModifiers.Alt | HotkeyModifiers.Control)));
    }

    /// <summary>A second modifier added DURING the hold also terminates the gesture.</summary>
    [Fact]
    public void Scenario004()
    {
        var machine = new HotkeyGestureMachine(DictationHotkey.RightCtrl);

        machine.Handle(Down(DictationHotkey.RightCtrl, HotkeyModifiers.Control));
        Assert.Equal(
            HotkeyAction.AbortShortcut,
            machine.Handle(Foreign(Vk.LeftMenu, HotkeyModifiers.Control | HotkeyModifiers.Alt, after: 0.05)));
        Assert.Equal(
            HotkeyAction.None,
            machine.Handle(Up(DictationHotkey.RightCtrl, HotkeyModifiers.Alt, after: 0.2)));
    }

    /// <summary>
    /// Breaking a recording that runs without holding is impossible: the modifier was released
    /// long ago, and the person's shortcuts have nothing to do with the gesture.
    /// </summary>
    [Fact]
    public void Scenario005()
    {
        var machine = new HotkeyGestureMachine(DictationHotkey.RightCtrl) { IsHandsFreeActive = true };

        Assert.Equal(HotkeyAction.None, machine.Handle(Foreign(Vk.LetterC, HotkeyModifiers.None)));
    }

    // MARK: - Hold

    [Fact]
    public void Scenario006()
    {
        var machine = new HotkeyGestureMachine(DictationHotkey.RightCtrl);

        Assert.Equal(HotkeyAction.Press, machine.Handle(Down(DictationHotkey.RightCtrl, HotkeyModifiers.Control)));
        Assert.Equal(
            ReleaseAfter(0),
            machine.Handle(Up(DictationHotkey.RightCtrl, HotkeyModifiers.None, after: 1)));
    }

    [Fact]
    public void Scenario007()
    {
        var machine = new HotkeyGestureMachine(DictationHotkey.RightCtrl);

        Assert.Equal(HotkeyAction.Press, machine.Handle(Down(DictationHotkey.RightCtrl, HotkeyModifiers.Control)));
        // Auto-repeat and other superfluous events: the gesture has already started, there is
        // no second start.
        Assert.Equal(
            HotkeyAction.None,
            machine.Handle(Down(DictationHotkey.RightCtrl, HotkeyModifiers.Control, after: 0.1)));
        Assert.Equal(
            HotkeyAction.None,
            machine.Handle(Down(DictationHotkey.RightCtrl, HotkeyModifiers.Control, after: 0.2)));
        Assert.Equal(
            ReleaseAfter(0.05),
            machine.Handle(Up(DictationHotkey.RightCtrl, HotkeyModifiers.None, after: 0.3)));
    }

    [Fact]
    public void Scenario008()
    {
        var machine = new HotkeyGestureMachine(DictationHotkey.RightCtrl);

        Assert.Equal(HotkeyAction.None, machine.Handle(Foreign(Vk.LeftShift, HotkeyModifiers.Shift)));
        Assert.False(machine.IsHeld);
    }

    /// <summary>
    /// A neighbouring key never looks like the hotkey.
    ///
    /// This is where the macOS original had its ugliest case: the Fn flag is raised by a whole
    /// category of keys - arrows, F1–F12, Home - so without filtering by key code, dictation
    /// started on an arrow press. Windows identifies a key by its virtual-key code alone, and
    /// the whole class of bug is gone. The check stays as the guarantee that nothing
    /// reintroduces it.
    /// </summary>
    [Fact]
    public void Scenario009()
    {
        var machine = new HotkeyGestureMachine(DictationHotkey.F13);

        Assert.Equal(HotkeyAction.None, machine.Handle(Foreign(Vk.F12, HotkeyModifiers.None)));
        Assert.False(machine.IsHeld);

        Assert.Equal(
            HotkeyAction.Press,
            machine.Handle(Down(DictationHotkey.F13, HotkeyModifiers.None, after: 0.1)));
    }

    /// <summary>
    /// Releasing the right key while the left twin is held must work.
    ///
    /// On macOS this was a real defect: the shared <c>.command</c> flag stayed raised while ANY
    /// Command was down, the release of the right one was invisible, and dictation stayed on
    /// forever - the microphone live, the recording running, nothing left to stop it. Windows
    /// reports the side in the key code, so the release arrives; and pressing the left twin
    /// mid-hold is still just Ctrl, not a chord, so it must not abort.
    /// </summary>
    [Fact]
    public void Scenario010()
    {
        var machine = new HotkeyGestureMachine(DictationHotkey.RightCtrl);

        // The left Ctrl is already held: its events are filtered out by key code.
        Assert.Equal(HotkeyAction.None, machine.Handle(Foreign(Vk.LeftControl, HotkeyModifiers.Control)));

        Assert.Equal(
            HotkeyAction.Press,
            machine.Handle(Down(DictationHotkey.RightCtrl, HotkeyModifiers.Control, after: 0.1)));

        // The right one is released, the left one is still held - the shared kind stays raised.
        Assert.Equal(
            ReleaseAfter(0),
            machine.Handle(Up(DictationHotkey.RightCtrl, HotkeyModifiers.Control, after: 0.5)));
    }

    /// <summary>The left twin pressed mid-hold is not a shortcut.</summary>
    [Fact]
    public void Scenario011()
    {
        var machine = new HotkeyGestureMachine(DictationHotkey.RightCtrl);

        Assert.Equal(HotkeyAction.Press, machine.Handle(Down(DictationHotkey.RightCtrl, HotkeyModifiers.Control)));
        Assert.Equal(
            HotkeyAction.None,
            machine.Handle(Foreign(Vk.LeftControl, HotkeyModifiers.Control, after: 0.2)));
        Assert.Equal(
            ReleaseAfter(0),
            machine.Handle(Up(DictationHotkey.RightCtrl, HotkeyModifiers.Control, after: 0.4)));
    }

    /// <summary>The left Alt is not the right Alt, whatever the chord state says.</summary>
    [Fact]
    public void Scenario012()
    {
        var machine = new HotkeyGestureMachine(DictationHotkey.RightAlt);

        Assert.Equal(HotkeyAction.None, machine.Handle(Foreign(Vk.LeftMenu, HotkeyModifiers.Alt)));
        Assert.False(machine.IsHeld);

        Assert.Equal(
            HotkeyAction.Press,
            machine.Handle(Down(DictationHotkey.RightAlt, HotkeyModifiers.Alt, after: 0.1)));
    }

    /// <summary>Caps Lock is not a modifier: holding it raises no chord at all.</summary>
    [Fact]
    public void Scenario013()
    {
        var machine = new HotkeyGestureMachine(DictationHotkey.CapsLock);

        Assert.Equal(HotkeyAction.Press, machine.Handle(Down(DictationHotkey.CapsLock, HotkeyModifiers.None)));
        Assert.Equal(
            ReleaseAfter(0),
            machine.Handle(Up(DictationHotkey.CapsLock, HotkeyModifiers.None, after: 0.6)));

        // …and any modifier held alongside it means a shortcut, not dictation.
        var chorded = new HotkeyGestureMachine(DictationHotkey.CapsLock);
        Assert.Equal(
            HotkeyAction.None,
            chorded.Handle(Down(DictationHotkey.CapsLock, HotkeyModifiers.Control)));
        Assert.False(chorded.IsHeld);
    }

    // MARK: - Double tap

    [Fact]
    public void Scenario014()
    {
        var machine = new HotkeyGestureMachine(DictationHotkey.RightCtrl, TimeSpan.FromSeconds(0.35));

        Assert.Equal(HotkeyAction.Press, machine.Handle(Down(DictationHotkey.RightCtrl, HotkeyModifiers.Control)));
        Assert.Equal(
            ReleaseAfter(0.3),
            machine.Handle(Up(DictationHotkey.RightCtrl, HotkeyModifiers.None, after: 0.05)));
        Assert.Equal(
            HotkeyAction.DoubleTap,
            machine.Handle(Down(DictationHotkey.RightCtrl, HotkeyModifiers.Control, after: 0.2)));
    }

    [Fact]
    public void Scenario015()
    {
        var machine = new HotkeyGestureMachine(DictationHotkey.RightCtrl, TimeSpan.FromSeconds(0.35));

        Assert.Equal(HotkeyAction.Press, machine.Handle(Down(DictationHotkey.RightCtrl, HotkeyModifiers.Control)));
        Assert.Equal(
            ReleaseAfter(0.3),
            machine.Handle(Up(DictationHotkey.RightCtrl, HotkeyModifiers.None, after: 0.05)));
        Assert.Equal(
            HotkeyAction.Press,
            machine.Handle(Down(DictationHotkey.RightCtrl, HotkeyModifiers.Control, after: 0.5)));
    }

    [Fact]
    public void Scenario016()
    {
        var machine = new HotkeyGestureMachine(DictationHotkey.RightCtrl, TimeSpan.FromSeconds(0.35));

        Assert.Equal(HotkeyAction.Press, machine.Handle(Down(DictationHotkey.RightCtrl, HotkeyModifiers.Control)));
        Assert.Equal(
            ReleaseAfter(0.3),
            machine.Handle(Up(DictationHotkey.RightCtrl, HotkeyModifiers.None, after: 0.05)));
        Assert.Equal(
            HotkeyAction.DoubleTap,
            machine.Handle(Down(DictationHotkey.RightCtrl, HotkeyModifiers.Control, after: 0.1)));
        Assert.Equal(
            ReleaseAfter(0.3),
            machine.Handle(Up(DictationHotkey.RightCtrl, HotkeyModifiers.None, after: 0.15)));
        // Three taps in a row does not mean "double twice".
        Assert.Equal(
            HotkeyAction.Press,
            machine.Handle(Down(DictationHotkey.RightCtrl, HotkeyModifiers.Control, after: 0.2)));
    }

    // MARK: - No-hold mode

    [Fact]
    public void Scenario017()
    {
        var machine = new HotkeyGestureMachine(DictationHotkey.RightCtrl) { IsHandsFreeActive = true };

        Assert.Equal(
            HotkeyAction.StopHandsFree,
            machine.Handle(Down(DictationHotkey.RightCtrl, HotkeyModifiers.Control)));
        // Releasing means nothing here: recording continues until the next press.
        Assert.Equal(
            HotkeyAction.None,
            machine.Handle(Up(DictationHotkey.RightCtrl, HotkeyModifiers.None, after: 0.1)));
    }

    [Fact]
    public void Scenario018()
    {
        var machine = new HotkeyGestureMachine(DictationHotkey.RightCtrl) { IsHandsFreeActive = true };

        Assert.Equal(
            HotkeyAction.StopHandsFree,
            machine.Handle(Down(DictationHotkey.RightCtrl, HotkeyModifiers.Control)));
        machine.IsHandsFreeActive = false;
        Assert.Equal(
            ReleaseAfter(0.25),
            machine.Handle(Up(DictationHotkey.RightCtrl, HotkeyModifiers.None, after: 0.1)));
    }

    // MARK: - Change the key

    /// <summary>
    /// The key was changed while it was held.
    ///
    /// Events of the old key never arrive after this: the hook compares against the new code.
    /// Without an explicit closing gesture, the release means nothing and dictation stays on
    /// forever.
    /// </summary>
    [Fact]
    public void Scenario019()
    {
        var machine = new HotkeyGestureMachine(DictationHotkey.RightCtrl);

        Assert.Equal(HotkeyAction.Press, machine.Handle(Down(DictationHotkey.RightCtrl, HotkeyModifiers.Control)));
        Assert.Equal(ReleaseAfter(0), machine.SetHotkey(DictationHotkey.CapsLock));
        Assert.False(machine.IsHeld);
    }

    [Fact]
    public void Scenario020()
    {
        var machine = new HotkeyGestureMachine(DictationHotkey.RightCtrl);
        Assert.Equal(HotkeyAction.None, machine.SetHotkey(DictationHotkey.CapsLock));
    }

    [Fact]
    public void Scenario021()
    {
        var machine = new HotkeyGestureMachine(DictationHotkey.RightCtrl) { IsHandsFreeActive = true };
        machine.Handle(Down(DictationHotkey.RightCtrl, HotkeyModifiers.Control));

        // In this mode recording is stopped by a press, not by a release.
        Assert.Equal(HotkeyAction.None, machine.SetHotkey(DictationHotkey.CapsLock));
    }

    [Fact]
    public void Scenario022()
    {
        var machine = new HotkeyGestureMachine(DictationHotkey.RightCtrl);
        machine.SetHotkey(DictationHotkey.CapsLock);

        Assert.Equal(
            HotkeyAction.None,
            machine.Handle(Down(DictationHotkey.RightCtrl, HotkeyModifiers.Control)));
        Assert.Equal(
            HotkeyAction.Press,
            machine.Handle(Down(DictationHotkey.CapsLock, HotkeyModifiers.None, after: 0.1)));
    }

    [Fact]
    public void Scenario023()
    {
        var machine = new HotkeyGestureMachine(DictationHotkey.RightCtrl);
        machine.Handle(Down(DictationHotkey.RightCtrl, HotkeyModifiers.Control));

        machine.Reset();

        Assert.False(machine.IsHeld);
        // Releasing an already broken gesture does not issue a second stop.
        Assert.Equal(
            HotkeyAction.None,
            machine.Handle(Up(DictationHotkey.RightCtrl, HotkeyModifiers.None, after: 0.1)));
    }

    // MARK: - Key layout

    [Fact]
    public void Scenario024()
    {
        // The codes must differ: they are what filters out somebody else's events.
        int[] codes = DictationHotkeyInfo.AllCases.Select(hotkey => hotkey.VirtualKey).ToArray();
        Assert.Equal(codes.Length, codes.Distinct().Count());

        Assert.Equal(Vk.RightControl, DictationHotkey.RightCtrl.VirtualKey);
        Assert.Equal(Vk.RightMenu, DictationHotkey.RightAlt.VirtualKey);
        Assert.Equal(Vk.Capital, DictationHotkey.CapsLock.VirtualKey);
        Assert.Equal(Vk.F13, DictationHotkey.F13.VirtualKey);

        // A candidate must never be the LEFT twin of a modifier: the left keys carry the
        // shortcuts people actually type.
        Assert.DoesNotContain(Vk.LeftControl, codes);
        Assert.DoesNotContain(Vk.LeftMenu, codes);

        // Every candidate must have a title to show and a chord kind to exclude itself by.
        foreach (DictationHotkey hotkey in DictationHotkeyInfo.AllCases)
        {
            Assert.False(string.IsNullOrWhiteSpace(hotkey.Title));
        }

        Assert.Equal(HotkeyModifiers.Control, DictationHotkey.RightCtrl.Kind);
        Assert.Equal(HotkeyModifiers.Alt, DictationHotkey.RightAlt.Kind);
        Assert.Equal(HotkeyModifiers.None, DictationHotkey.CapsLock.Kind);
        Assert.Equal(HotkeyModifiers.None, DictationHotkey.F13.Kind);

        // Caps Lock toggles on key-down: without swallowing it, every dictation would leave the
        // keyboard shouting.
        Assert.True(DictationHotkey.CapsLock.NeedsSuppression);
    }

    // MARK: - The phantom press a virtual machine sends

    /// <summary>
    /// A down and an up with the same timestamp is one short hold, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A QEMU/spice guest re-synchronises the Caps Lock LED by sending a compensating pair of
    /// events, both stamped with the same millisecond. Nothing this side of the hook can tell
    /// that from a person brushing the key, and nothing should try: the machine already has an
    /// answer for a press of no length, and it is the answer a person's shortest tap gets.
    /// </para>
    /// <para>
    /// What matters is what it is <b>not</b>. It is not a hands-free toggle - that takes a second
    /// press inside the window, and one phantom pair is one press. It is not an immediate stop
    /// either: the release waits out the whole double-tap window, which is why such a press still
    /// costs a third of a second of open microphone. That third of a second is under
    /// <c>DictationDurationPolicy.Minimum</c> and is dropped there, unheard.
    /// </para>
    /// </remarks>
    [Fact]
    public void TestPhantomInstantPressIsAShortHoldNotAHandsFreeToggle()
    {
        var machine = new HotkeyGestureMachine(DictationHotkey.CapsLock);

        Assert.Equal(HotkeyAction.Press, machine.Handle(Down(DictationHotkey.CapsLock, HotkeyModifiers.None)));
        Assert.Equal(
            ReleaseAfter(0.35),
            machine.Handle(Up(DictationHotkey.CapsLock, HotkeyModifiers.None)));
        Assert.False(machine.IsHeld);
        Assert.False(machine.IsHandsFreeActive);

        // The next phantom pair arrives seconds later, as they did in the field log: a fresh
        // press, not the second half of a double tap.
        Assert.Equal(
            HotkeyAction.Press,
            machine.Handle(Down(DictationHotkey.CapsLock, HotkeyModifiers.None, after: 2)));
        Assert.False(machine.IsHandsFreeActive);
    }
}
