using OpenRamble.AppCore;

namespace OpenRamble.LinuxInput.Tests;

/// <summary>
/// The hotkey, from bytes on a device to the gestures the application acts on.
/// </summary>
/// <remarks>
/// The gesture table itself is <c>HotkeyGestureMachine</c>'s and is tested in AppCore against both
/// keymaps. What is tested here is everything the machine cannot see: that the right devices are
/// opened, that the evdev numbering reaches the machine unmangled, that the chord is assembled
/// from a stream instead of asked of a system that will not answer, that a keyboard plugged in
/// afterwards is picked up, and that a person who cannot read <c>/dev/input</c> is told so in words
/// they can act on.
/// </remarks>
[Collection(LinuxInputCollection.Name)]
public sealed class EvdevKeyboardMonitorTests
{
    private static readonly TimeSpan Rescan = TimeSpan.FromMilliseconds(30);

    [Fact]
    public void TheHotkeyGoingDownAndUpIsAPressAndARelease()
    {
        var source = new FakeKeyboardDeviceSource();
        KeyFeed keyboard = source.Add();
        using var monitor = new EvdevKeyboardMonitor(source, Rescan);
        int presses = 0;
        int releases = 0;
        monitor.OnPress = () => Interlocked.Increment(ref presses);
        monitor.OnRelease = () => Interlocked.Increment(ref releases);

        monitor.Start();
        keyboard.Press(Keys.RightCtrl);

        Assert.True(Until.True(() => Volatile.Read(ref presses) == 1));

        keyboard.Release(Keys.RightCtrl);

        // The release of a short tap waits out the double-tap window before it means "stop", which
        // is the gesture machine's decision and is honoured here rather than short-circuited.
        Assert.True(Until.True(() => Volatile.Read(ref releases) == 1));
        Assert.Equal(1, Volatile.Read(ref presses));
    }

    [Fact]
    public void TheRawEdgeIsReportedEvenWhenTheGestureIsNot()
    {
        // The whole point of the edge: a press that starts no dictation - engine loading, chord in
        // progress - is otherwise indistinguishable from a key that never arrived, and the
        // first-launch try-out exists to tell those two apart.
        using var diagnostics = new DiagnosticRecorder();
        var source = new FakeKeyboardDeviceSource();
        KeyFeed keyboard = source.Add();
        using var monitor = new EvdevKeyboardMonitor(source, Rescan);
        int downs = 0;
        int ups = 0;
        int presses = 0;
        monitor.OnHotkeyDown = () => Interlocked.Increment(ref downs);
        monitor.OnHotkeyUp = () => Interlocked.Increment(ref ups);
        monitor.OnPress = () => Interlocked.Increment(ref presses);

        monitor.Start();
        // Shift first, so the hotkey arrives inside a chord and the gesture refuses it.
        keyboard.Press(Keys.LeftShift);
        keyboard.Press(Keys.RightCtrl);

        Assert.True(Until.True(() => Volatile.Read(ref downs) == 1));
        Assert.Equal(0, Volatile.Read(ref presses));

        keyboard.Release(Keys.RightCtrl);

        Assert.True(Until.True(() => Volatile.Read(ref ups) == 1));
        Assert.Equal("RightCtrl", diagnostics.Field("hotkey.edge", "key"));
        Assert.Equal("true", diagnostics.Field("hotkey.edge", "down"));
    }

    [Fact]
    public void AutoRepeatIsNotASecondPress()
    {
        var source = new FakeKeyboardDeviceSource();
        KeyFeed keyboard = source.Add();
        using var monitor = new EvdevKeyboardMonitor(source, Rescan);
        int downs = 0;
        int presses = 0;
        monitor.OnHotkeyDown = () => Interlocked.Increment(ref downs);
        monitor.OnPress = () => Interlocked.Increment(ref presses);

        monitor.Start();
        keyboard.Press(Keys.RightCtrl);
        keyboard.Repeat(Keys.RightCtrl);
        keyboard.Repeat(Keys.RightCtrl);

        Assert.True(Until.True(() => Volatile.Read(ref presses) == 1));
        Thread.Sleep(50);
        Assert.Equal(1, Volatile.Read(ref downs));
        Assert.Equal(1, Volatile.Read(ref presses));
    }

    [Fact]
    public void AnotherKeyDuringTheHoldIsAShortcut()
    {
        var source = new FakeKeyboardDeviceSource();
        KeyFeed keyboard = source.Add();
        using var monitor = new EvdevKeyboardMonitor(source, Rescan);
        int aborts = 0;
        int releases = 0;
        monitor.OnAbortShortcut = () => Interlocked.Increment(ref aborts);
        monitor.OnRelease = () => Interlocked.Increment(ref releases);

        monitor.Start();
        keyboard.Press(Keys.RightCtrl);
        keyboard.Press(Keys.C);
        keyboard.Release(Keys.C);
        keyboard.Release(Keys.RightCtrl);

        Assert.True(Until.True(() => Volatile.Read(ref aborts) == 1));
        Thread.Sleep(TimeSpan.FromMilliseconds(500));
        // Ctrl+C was a copy, not a dictation: the release that followed must insert nothing.
        Assert.Equal(0, Volatile.Read(ref releases));
    }

    [Fact]
    public void TheOtherSideOfTheSameModifierIsNotAShortcut()
    {
        // Left Ctrl arriving while right Ctrl is held is still just Ctrl - the person is not
        // reaching for a combination, and the chord state has to say so. This is the case the
        // evdev numbering makes exact: the side IS the code, with nothing to mask off.
        var source = new FakeKeyboardDeviceSource();
        KeyFeed keyboard = source.Add();
        using var monitor = new EvdevKeyboardMonitor(source, Rescan);
        int aborts = 0;
        int presses = 0;
        monitor.OnAbortShortcut = () => Interlocked.Increment(ref aborts);
        monitor.OnPress = () => Interlocked.Increment(ref presses);

        monitor.Start();
        keyboard.Press(Keys.RightCtrl);
        keyboard.Press(Keys.LeftCtrl);

        Assert.True(Until.True(() => Volatile.Read(ref presses) == 1));
        Thread.Sleep(50);
        Assert.Equal(0, Volatile.Read(ref aborts));
    }

    [Fact]
    public void EscapeCancelsAndIsNotAGesture()
    {
        var source = new FakeKeyboardDeviceSource();
        KeyFeed keyboard = source.Add();
        using var monitor = new EvdevKeyboardMonitor(source, Rescan);
        int escapes = 0;
        int aborts = 0;
        monitor.OnEscape = () => Interlocked.Increment(ref escapes);
        monitor.OnAbortShortcut = () => Interlocked.Increment(ref aborts);

        monitor.Start();
        keyboard.Press(Keys.RightCtrl);
        keyboard.Press(Keys.Escape);
        keyboard.Release(Keys.Escape);

        Assert.True(Until.True(() => Volatile.Read(ref escapes) == 1));
        Thread.Sleep(50);
        // Escape is a cancel, never a shortcut over the hotkey and never swallowed - the same
        // decision the Windows hook makes, and it must not drift between the two.
        Assert.Equal(0, Volatile.Read(ref aborts));
        Assert.Equal(1, Volatile.Read(ref escapes));
    }

    [Fact]
    public void TwoTapsInARowAreADoubleTap()
    {
        var source = new FakeKeyboardDeviceSource();
        KeyFeed keyboard = source.Add();
        using var monitor = new EvdevKeyboardMonitor(source, Rescan);
        int doubles = 0;
        int releases = 0;
        monitor.OnDoubleTap = () => Interlocked.Increment(ref doubles);
        monitor.OnRelease = () => Interlocked.Increment(ref releases);

        monitor.Start();
        keyboard.Press(Keys.RightCtrl);
        keyboard.Release(Keys.RightCtrl);
        keyboard.Press(Keys.RightCtrl);

        Assert.True(Until.True(() => Volatile.Read(ref doubles) == 1));
        // The first tap's release was still waiting out its window; the second press cancels it,
        // or hands-free dictation would stop the moment it started.
        Assert.Equal(0, Volatile.Read(ref releases));
    }

    [Fact]
    public void TheChordIsAssembledFromTheStreamForTheInserter()
    {
        // Wayland has no GetAsyncKeyState, so this is the only answer the insertion path can get to
        // "may I send Ctrl+V yet".
        var source = new FakeKeyboardDeviceSource();
        KeyFeed keyboard = source.Add();
        using var monitor = new EvdevKeyboardMonitor(source, Rescan);

        monitor.Start();
        keyboard.Press(Keys.RightShift);
        keyboard.Press(Keys.LeftCtrl);

        Assert.True(Until.True(() =>
            monitor.HeldModifiers == (HotkeyModifiers.Shift | HotkeyModifiers.Control)));

        keyboard.Release(Keys.LeftCtrl);
        Assert.True(Until.True(() => monitor.HeldModifiers == HotkeyModifiers.Shift));

        keyboard.Release(Keys.RightShift);
        Assert.True(Until.True(() => monitor.HeldModifiers == HotkeyModifiers.None));
    }

    [Fact]
    public void EveryKeyboardIsWatched()
    {
        // A laptop with an external keyboard, or a remapper's virtual device next to the physical
        // one: the hotkey has to work on whichever the person is typing on.
        var source = new FakeKeyboardDeviceSource();
        KeyFeed builtIn = source.Add("event1", "AT Translated Set 2 keyboard");
        KeyFeed external = source.Add("event9", "SIGMACHIP USB Keyboard");
        using var monitor = new EvdevKeyboardMonitor(source, Rescan);
        int presses = 0;
        monitor.OnPress = () => Interlocked.Increment(ref presses);

        monitor.Start();
        builtIn.Press(Keys.RightCtrl);
        Assert.True(Until.True(() => Volatile.Read(ref presses) == 1));
        builtIn.Release(Keys.RightCtrl);
        Assert.True(Until.True(() => !monitor.HeldModifiers.HasFlag(HotkeyModifiers.Control)));

        // Out of the double-tap window first: a second press inside it is a double tap, which is a
        // different gesture and would not answer the question this test asks.
        Thread.Sleep(TimeSpan.FromMilliseconds(450));
        external.Press(Keys.RightCtrl);
        Assert.True(Until.True(() => Volatile.Read(ref presses) == 2));
    }

    [Fact]
    public void AKeyboardPluggedInLaterIsPickedUp()
    {
        var source = new FakeKeyboardDeviceSource();
        source.Add("event1");
        using var monitor = new EvdevKeyboardMonitor(source, Rescan);
        int presses = 0;
        monitor.OnPress = () => Interlocked.Increment(ref presses);
        monitor.Start();

        KeyFeed plugged = source.Add("event20", "Plugged In Later");

        Assert.True(Until.True(() =>
        {
            // Pressed on every attempt: the rescan may not have run yet, and a press into a device
            // nobody is reading is exactly what "not picked up" looks like.
            plugged.Press(Keys.RightCtrl);
            plugged.Release(Keys.RightCtrl);
            return Volatile.Read(ref presses) > 0;
        }));
    }

    [Fact]
    public void WithoutPermissionThePersonIsToldWhichGroupToJoin()
    {
        using var diagnostics = new DiagnosticRecorder();
        var source = new FakeKeyboardDeviceSource();
        source.AddUnreadable("event2", "SIGMACHIP USB Keyboard");
        source.AddUnreadable("event3", "AT Translated Set 2 keyboard");
        using var monitor = new EvdevKeyboardMonitor(source, Rescan);
        string? told = null;
        monitor.OnTrackingFailure = message => told = message;

        monitor.Start();

        // Not a crash and not silence: the key would otherwise do nothing for the rest of the
        // session, which looks exactly like a person who is not pressing it.
        Assert.NotNull(told);
        Assert.Contains("input", told, StringComparison.Ordinal);
        Assert.Contains("gpasswd", told, StringComparison.Ordinal);
        Assert.False(monitor.IsRunning);
        Assert.Equal("false", diagnostics.Field("hook.install", "ok"));
        Assert.Equal("permission", diagnostics.Field("hook.install", "error"));
        Assert.Equal("2", diagnostics.Field("hook.install", "keyboards"));
        Assert.Equal("2", diagnostics.Field("hook.install", "denied"));
    }

    [Fact]
    public void WithNoKeyboardAtAllTheMessageSaysThatInstead()
    {
        // "Add yourself to the input group" told to somebody whose keyboard is simply not there
        // wastes an evening. The two failures have two remedies and must not share a sentence.
        using var diagnostics = new DiagnosticRecorder();
        using var monitor = new EvdevKeyboardMonitor(new FakeKeyboardDeviceSource(), Rescan);
        string? told = null;
        monitor.OnTrackingFailure = message => told = message;

        monitor.Start();

        Assert.NotNull(told);
        Assert.Contains("/dev/input", told, StringComparison.Ordinal);
        Assert.DoesNotContain("gpasswd", told, StringComparison.Ordinal);
        Assert.Equal("no-keyboard", diagnostics.Field("hook.install", "error"));
    }

    [Fact]
    public void APermissionThatIsFixedLaterIsPickedUpWithoutARestart()
    {
        var source = new FakeKeyboardDeviceSource();
        source.AddUnreadable("event2");
        using var monitor = new EvdevKeyboardMonitor(source, Rescan);
        int presses = 0;
        monitor.OnPress = () => Interlocked.Increment(ref presses);
        monitor.Start();
        Assert.False(monitor.IsRunning);

        KeyFeed granted = source.Add("event3", "Now Readable");

        Assert.True(Until.True(() =>
        {
            granted.Press(Keys.RightCtrl);
            granted.Release(Keys.RightCtrl);
            return Volatile.Read(ref presses) > 0;
        }));
    }

    [Fact]
    public void StoppingLetsGoOfEveryDeviceAndSaysSo()
    {
        using var diagnostics = new DiagnosticRecorder();
        var source = new FakeKeyboardDeviceSource();
        KeyFeed keyboard = source.Add("event4");
        using var monitor = new EvdevKeyboardMonitor(source, Rescan);
        int presses = 0;
        monitor.OnPress = () => Interlocked.Increment(ref presses);

        monitor.Start();
        keyboard.Press(Keys.RightCtrl);
        Assert.True(Until.True(() => Volatile.Read(ref presses) == 1));

        monitor.Stop();

        Assert.Contains("/dev/input/event4", source.Closed);
        Assert.NotNull(diagnostics.Event("hook.removed"));
        Assert.False(monitor.IsRunning);

        // A device that outlived the stop must not be able to start a dictation with it.
        keyboard.Press(Keys.RightCtrl);
        Thread.Sleep(100);
        Assert.Equal(1, Volatile.Read(ref presses));
    }

    [Fact]
    public void AKeyHeldWhenTrackingStopsIsReportedAsReleased()
    {
        // Otherwise the try-out shows a key held down forever, and the overlay stays lit with
        // nothing left to turn it off.
        var source = new FakeKeyboardDeviceSource();
        KeyFeed keyboard = source.Add();
        using var monitor = new EvdevKeyboardMonitor(source, Rescan);
        int downs = 0;
        int ups = 0;
        monitor.OnHotkeyDown = () => Interlocked.Increment(ref downs);
        monitor.OnHotkeyUp = () => Interlocked.Increment(ref ups);

        monitor.Start();
        keyboard.Press(Keys.RightCtrl);
        Assert.True(Until.True(() => Volatile.Read(ref downs) == 1));

        monitor.Stop();

        Assert.Equal(1, Volatile.Read(ref ups));
    }

    [Fact]
    public void ChangingTheKeyMidHoldEndsTheHold()
    {
        using var diagnostics = new DiagnosticRecorder();
        var source = new FakeKeyboardDeviceSource();
        KeyFeed keyboard = source.Add();
        using var monitor = new EvdevKeyboardMonitor(source, Rescan);
        int releases = 0;
        int ups = 0;
        monitor.OnRelease = () => Interlocked.Increment(ref releases);
        monitor.OnHotkeyUp = () => Interlocked.Increment(ref ups);
        monitor.Start();
        keyboard.Press(Keys.RightCtrl);
        Assert.True(Until.True(() => monitor.HeldModifiers == HotkeyModifiers.Control));

        monitor.SetHotkey(DictationHotkey.CapsLock);

        // Events of the old key stop meaning anything, so its release would never come and the
        // dictation would stay on with no key left to stop it.
        Assert.True(Until.True(() => Volatile.Read(ref releases) == 1));
        Assert.Equal(1, Volatile.Read(ref ups));
        Assert.Equal("CapsLock", diagnostics.Field("hotkey.configured", "key"));
        // Read and not acted on: an evdev grab is how suppression would be done here, and it takes
        // the keyboard away from the compositor. Caps Lock still toggles, and the log says so.
        Assert.True(DictationHotkey.CapsLock.NeedsSuppression);
        Assert.Equal("false", diagnostics.Field("hotkey.configured", "suppressed"));
    }

    [Fact]
    public void TheNewKeyWorksAfterTheChange()
    {
        var source = new FakeKeyboardDeviceSource();
        KeyFeed keyboard = source.Add();
        using var monitor = new EvdevKeyboardMonitor(source, Rescan);
        int presses = 0;
        monitor.OnPress = () => Interlocked.Increment(ref presses);
        monitor.Start();
        monitor.SetHotkey(DictationHotkey.CapsLock);

        keyboard.Press(Keys.CapsLock);

        Assert.True(Until.True(() => Volatile.Read(ref presses) == 1));
    }

    [Fact]
    public void AHandlerMayStopTheMonitorFromInsideACallback()
    {
        // A handler runs on the reader thread, and one of the things a shell plausibly does from
        // Escape or from a tracking failure is stop tracking. If the stop waited for the reader
        // threads without excluding the current one, it would be waiting for itself.
        var source = new FakeKeyboardDeviceSource();
        KeyFeed keyboard = source.Add();
        using var monitor = new EvdevKeyboardMonitor(source, Rescan);
        int stops = 0;
        monitor.OnEscape = () =>
        {
            monitor.Stop();
            Interlocked.Increment(ref stops);
        };

        monitor.Start();
        keyboard.Press(Keys.Escape);

        Assert.True(Until.True(() => Volatile.Read(ref stops) == 1));
        Assert.False(monitor.IsRunning);
    }

    [Fact]
    public void StartingTwiceDoesNotOpenTheDevicesTwice()
    {
        // Re-opening would drop the memory of a key currently held, and the release that followed
        // would be lost with dictation left switched on.
        var source = new FakeKeyboardDeviceSource();
        source.Add();
        using var monitor = new EvdevKeyboardMonitor(source, TimeSpan.FromMinutes(1));

        monitor.Start();
        monitor.Start();

        Assert.Equal(1, source.Opens);
    }
}

/// <summary>
/// The two tables that describe evdev modifiers must not drift apart.
/// </summary>
/// <remarks>
/// <c>LinuxHotkeyKeymap.IsModifierCode</c> decides whether a key arriving during a hold ends the
/// gesture; <see cref="LinuxModifierCodes"/> decides which flag that key raises. A code in one and
/// not the other is a silent bug in either direction - a modifier that aborts every dictation, or a
/// chord that never looks complete.
/// </remarks>
public sealed class LinuxModifierCodeTests
{
    [Fact]
    public void EveryModifierCodeHasAKindAndNothingElseDoes()
    {
        for (int code = 0; code <= 255; code++)
        {
            bool isModifier = LinuxHotkeyKeymap.Instance.IsModifierCode(code);
            HotkeyModifiers kind = LinuxModifierCodes.KindOf(code);

            Assert.Equal(isModifier, kind != HotkeyModifiers.None);
        }
    }

    [Fact]
    public void EachHotkeyCandidateRaisesTheKindItsOwnTableClaims()
    {
        foreach (DictationHotkey hotkey in DictationHotkeyInfo.AllCases)
        {
            int code = LinuxHotkeyKeymap.Instance.CodeFor(hotkey);

            // DictationHotkeyInfo.Kind is what the gesture machine masks the chord against; if the
            // code's kind disagreed, holding the hotkey would look like a chord and no dictation
            // would ever start.
            Assert.Equal(hotkey.Kind, LinuxModifierCodes.KindOf(code));
        }
    }
}
