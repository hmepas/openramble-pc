using OpenRamble.AppCore;

namespace OpenRamble.LinuxInput.Tests;

/// <summary>
/// The copy shortcut, from bytes on a device to one callback.
/// </summary>
/// <remarks>
/// The chord rules themselves are <c>KeyCombination</c>'s and are tested in AppCore. What is
/// tested here is the plumbing: that the shortcut reads the same devices the dictation key reads,
/// that the modifiers it is judged against are the ones the keyboard monitor tracked, that a held
/// key does not fire on every repeat, and that stopping actually stops.
/// </remarks>
[Collection(LinuxInputCollection.Name)]
public sealed class EvdevShortcutMonitorTests
{
    private static readonly TimeSpan Rescan = TimeSpan.FromMilliseconds(30);
    private static readonly KeyCombination CtrlAltC = new(Keys.C, HotkeyModifiers.Control | HotkeyModifiers.Alt);

    private static (KeyFeed Keyboard, EvdevKeyboardMonitor Keys) Machine()
    {
        var source = new FakeKeyboardDeviceSource();
        KeyFeed keyboard = source.Add();
        var keys = new EvdevKeyboardMonitor(source, Rescan);
        return (keyboard, keys);
    }

    private static void Chord(KeyFeed keyboard, int key, params int[] modifiers)
    {
        foreach (int modifier in modifiers) { keyboard.Press(modifier); }

        keyboard.Press(key);
        keyboard.Release(key);
        foreach (int modifier in modifiers) { keyboard.Release(modifier); }
    }

    [Fact]
    public void TheChordFiresOnce()
    {
        (KeyFeed keyboard, EvdevKeyboardMonitor keys) = Machine();
        using (keys)
        using (var monitor = new EvdevShortcutMonitor(keys))
        {
            int presses = 0;
            monitor.OnPress = () => Interlocked.Increment(ref presses);
            monitor.SetShortcut(CtrlAltC);
            keys.Start();
            monitor.Start();

            Chord(keyboard, Keys.C, Keys.LeftCtrl, Keys.LeftAlt);

            Assert.True(Until.True(() => Volatile.Read(ref presses) == 1));
            Assert.True(monitor.IsRunning);
        }
    }

    [Fact]
    public void AnExtraModifierIsADifferentShortcut()
    {
        // Ctrl+Alt+Shift+C means something else in most applications; the set is compared exactly.
        (KeyFeed keyboard, EvdevKeyboardMonitor keys) = Machine();
        using (keys)
        using (var monitor = new EvdevShortcutMonitor(keys))
        {
            int presses = 0;
            monitor.OnPress = () => Interlocked.Increment(ref presses);
            monitor.SetShortcut(CtrlAltC);
            keys.Start();
            monitor.Start();

            Chord(keyboard, Keys.C, Keys.LeftCtrl, Keys.LeftAlt, Keys.LeftShift);
            // A bare C is not it either.
            Chord(keyboard, Keys.C);
            // The one that is, so the test can tell "not yet" from "never".
            Chord(keyboard, Keys.C, Keys.LeftCtrl, Keys.LeftAlt);

            Assert.True(Until.True(() => Volatile.Read(ref presses) == 1));
            Assert.Equal(1, Volatile.Read(ref presses));
        }
    }

    [Fact]
    public void AutoRepeatIsNotAnotherPress()
    {
        (KeyFeed keyboard, EvdevKeyboardMonitor keys) = Machine();
        using (keys)
        using (var monitor = new EvdevShortcutMonitor(keys))
        {
            int presses = 0;
            monitor.OnPress = () => Interlocked.Increment(ref presses);
            monitor.SetShortcut(CtrlAltC);
            keys.Start();
            monitor.Start();

            keyboard.Press(Keys.LeftCtrl);
            keyboard.Press(Keys.LeftAlt);
            keyboard.Press(Keys.C);
            keyboard.Repeat(Keys.C);
            keyboard.Repeat(Keys.C);
            keyboard.Release(Keys.C);
            keyboard.Release(Keys.LeftAlt);
            keyboard.Release(Keys.LeftCtrl);
            // A second real press, so the assertion below waits for something after the repeats.
            Chord(keyboard, Keys.C, Keys.LeftCtrl, Keys.LeftAlt);

            Assert.True(Until.True(() => Volatile.Read(ref presses) == 2));
            Assert.Equal(2, Volatile.Read(ref presses));
        }
    }

    [Fact]
    public void AFunctionKeyStandsAlone()
    {
        (KeyFeed keyboard, EvdevKeyboardMonitor keys) = Machine();
        using (keys)
        using (var monitor = new EvdevShortcutMonitor(keys))
        {
            int presses = 0;
            monitor.OnPress = () => Interlocked.Increment(ref presses);
            monitor.SetShortcut(new KeyCombination(Keys.F13, HotkeyModifiers.None));
            keys.Start();
            monitor.Start();

            Chord(keyboard, Keys.F13);

            Assert.True(Until.True(() => Volatile.Read(ref presses) == 1));
        }
    }

    [Fact]
    public void LockKeysDoNotChangeTheChord()
    {
        // Caps Lock is a hotkey candidate and passes through the same stream; it is not a chord
        // modifier and must not stop Ctrl+Alt+C from matching while it is down.
        (KeyFeed keyboard, EvdevKeyboardMonitor keys) = Machine();
        using (keys)
        using (var monitor = new EvdevShortcutMonitor(keys))
        {
            int presses = 0;
            monitor.OnPress = () => Interlocked.Increment(ref presses);
            monitor.SetShortcut(CtrlAltC);
            keys.Start();
            monitor.Start();

            keyboard.Press(Keys.CapsLock);
            Chord(keyboard, Keys.C, Keys.LeftCtrl, Keys.LeftAlt);
            keyboard.Release(Keys.CapsLock);

            Assert.True(Until.True(() => Volatile.Read(ref presses) == 1));
        }
    }

    [Fact]
    public void StoppedMeansStopped()
    {
        (KeyFeed keyboard, EvdevKeyboardMonitor keys) = Machine();
        using (keys)
        using (var monitor = new EvdevShortcutMonitor(keys))
        {
            int presses = 0;
            int hotkeyDowns = 0;
            keys.OnHotkeyDown = () => Interlocked.Increment(ref hotkeyDowns);
            monitor.OnPress = () => Interlocked.Increment(ref presses);
            monitor.SetShortcut(CtrlAltC);
            keys.Start();
            monitor.Start();
            monitor.Stop();
            Assert.False(monitor.IsRunning);

            Chord(keyboard, Keys.C, Keys.LeftCtrl, Keys.LeftAlt);
            // The dictation key still works - only the shortcut left the keyboard - and its edge
            // arriving proves the chord above was read and refused rather than not read yet.
            keyboard.Press(Keys.RightCtrl);
            keyboard.Release(Keys.RightCtrl);

            Assert.True(Until.True(() => Volatile.Read(ref hotkeyDowns) == 1));
            Assert.Equal(0, Volatile.Read(ref presses));

            monitor.Start();
            Chord(keyboard, Keys.C, Keys.LeftCtrl, Keys.LeftAlt);
            Assert.True(Until.True(() => Volatile.Read(ref presses) == 1));
        }
    }

    [Fact]
    public void ClearingTheShortcutTakesItOffTheKeyboard()
    {
        (_, EvdevKeyboardMonitor keys) = Machine();
        using (keys)
        using (var monitor = new EvdevShortcutMonitor(keys))
        {
            monitor.SetShortcut(CtrlAltC);
            keys.Start();
            monitor.Start();
            Assert.True(monitor.IsRunning);

            monitor.SetShortcut(null);

            Assert.False(monitor.IsRunning);
            // And a start with nothing to look for stays stopped.
            monitor.Start();
            Assert.False(monitor.IsRunning);
        }
    }

    [Fact]
    public void AnInjectedEdgeIsNotAKeyAnyChordCanSee()
    {
        // The control socket says "the dictation key went down" and nothing else, so it can
        // never complete a chord - and a shortcut bound to a bare hotkey-shaped key must not fire
        // from it either.
        (_, EvdevKeyboardMonitor keys) = Machine();
        using (keys)
        using (var monitor = new EvdevShortcutMonitor(keys))
        {
            int presses = 0;
            monitor.OnPress = () => Interlocked.Increment(ref presses);
            monitor.SetShortcut(new KeyCombination(Keys.RightCtrl, HotkeyModifiers.None));
            keys.Start();
            monitor.Start();

            Assert.True(keys.InjectHotkeyEdge(true));
            Assert.True(keys.InjectHotkeyEdge(false));
            Thread.Sleep(50);

            Assert.Equal(0, Volatile.Read(ref presses));
        }
    }
}
