using Avalonia.Input;
using OpenRamble.App.Linux;
using OpenRamble.AppCore;

namespace OpenRamble.App.Linux.Tests;

/// <summary>
/// The recorder and the monitor speak different vocabularies; the table between them has to
/// agree with the one the monitor's labels are read from.
/// </summary>
public sealed class EvdevKeyCodesTests
{
    [Theory]
    [InlineData(PhysicalKey.C, 46)]
    [InlineData(PhysicalKey.Q, 16)]
    [InlineData(PhysicalKey.A, 30)]
    [InlineData(PhysicalKey.Z, 44)]
    [InlineData(PhysicalKey.Digit1, 2)]
    [InlineData(PhysicalKey.Digit0, 11)]
    [InlineData(PhysicalKey.F1, 59)]
    [InlineData(PhysicalKey.F10, 68)]
    [InlineData(PhysicalKey.F11, 87)]
    [InlineData(PhysicalKey.F12, 88)]
    [InlineData(PhysicalKey.F13, 183)]
    [InlineData(PhysicalKey.F24, 194)]
    [InlineData(PhysicalKey.Space, 57)]
    [InlineData(PhysicalKey.Escape, 1)]
    [InlineData(PhysicalKey.Backspace, 14)]
    [InlineData(PhysicalKey.Delete, 111)]
    public void Positions_translate_to_the_kernels_numbers(PhysicalKey key, int expected)
    {
        Assert.Equal(expected, EvdevKeyCodes.Of(key));
    }

    /// <summary>
    /// Every code the table hands out is one the monitor's keymap can name, and the name is the
    /// letter on the key - otherwise a recorded chord would be shown as "Key 46".
    /// </summary>
    [Theory]
    [InlineData(PhysicalKey.C, "C")]
    [InlineData(PhysicalKey.F13, "F13")]
    [InlineData(PhysicalKey.Space, "Space")]
    [InlineData(PhysicalKey.Digit7, "7")]
    public void The_monitors_keymap_names_every_translated_key(PhysicalKey key, string label)
    {
        int code = Assert.IsType<int>(EvdevKeyCodes.Of(key));
        Assert.Equal(label, LinuxShortcutKeymap.Instance.LabelFor(code));
    }

    [Fact]
    public void The_function_row_reads_as_the_function_row_on_both_sides()
    {
        foreach (PhysicalKey key in new[] { PhysicalKey.F1, PhysicalKey.F12, PhysicalKey.F13, PhysicalKey.F24 })
        {
            int code = Assert.IsType<int>(EvdevKeyCodes.Of(key));
            Assert.True(LinuxShortcutKeymap.Instance.IsFunctionKey(code));
        }

        Assert.False(LinuxShortcutKeymap.Instance.IsFunctionKey(EvdevKeyCodes.Of(PhysicalKey.C)!.Value));
    }

    [Fact]
    public void Escape_and_delete_keep_their_recorder_meaning()
    {
        Assert.Equal(ShortcutSpecialKey.Escape, LinuxShortcutKeymap.Instance.Special(EvdevKeyCodes.Of(PhysicalKey.Escape)!.Value));
        Assert.Equal(ShortcutSpecialKey.Delete, LinuxShortcutKeymap.Instance.Special(EvdevKeyCodes.Of(PhysicalKey.Backspace)!.Value));
        Assert.Equal(ShortcutSpecialKey.Delete, LinuxShortcutKeymap.Instance.Special(EvdevKeyCodes.Of(PhysicalKey.Delete)!.Value));
    }

    [Fact]
    public void Keys_the_monitor_could_never_see_are_not_recordable()
    {
        Assert.Null(EvdevKeyCodes.Of(PhysicalKey.None));
        Assert.Null(EvdevKeyCodes.Of(PhysicalKey.MediaPlayPause));
    }

    [Fact]
    public void Modifiers_and_lock_keys_are_not_keys_a_chord_ends_on()
    {
        Assert.True(EvdevKeyCodes.IsModifier(PhysicalKey.ControlLeft));
        Assert.True(EvdevKeyCodes.IsModifier(PhysicalKey.MetaRight));
        Assert.True(EvdevKeyCodes.IsModifier(PhysicalKey.CapsLock));
        Assert.False(EvdevKeyCodes.IsModifier(PhysicalKey.C));
    }

    [Fact]
    public void Avalonias_modifiers_become_the_chords_modifiers()
    {
        Assert.Equal(
            HotkeyModifiers.Control | HotkeyModifiers.Alt,
            EvdevKeyCodes.Modifiers(KeyModifiers.Control | KeyModifiers.Alt));
        Assert.Equal(HotkeyModifiers.Windows, EvdevKeyCodes.Modifiers(KeyModifiers.Meta));
        Assert.Equal(HotkeyModifiers.Shift, EvdevKeyCodes.Modifiers(KeyModifiers.Shift));
        Assert.Equal(HotkeyModifiers.None, EvdevKeyCodes.Modifiers(KeyModifiers.None));
    }

    /// <summary>
    /// The whole round trip: what the recorder decides for Ctrl+Alt+C is exactly what the
    /// evdev monitor would match on the same physical press.
    /// </summary>
    [Fact]
    public void A_recorded_chord_matches_the_kernels_view_of_the_same_press()
    {
        int code = EvdevKeyCodes.Of(PhysicalKey.C)!.Value;
        ShortcutRecording.Outcome outcome = ShortcutRecording.Decide(
            code,
            EvdevKeyCodes.Modifiers(KeyModifiers.Control | KeyModifiers.Alt),
            LinuxShortcutKeymap.Instance);

        var commit = Assert.IsType<ShortcutRecording.Outcome.Commit>(outcome);
        Assert.True(commit.Combination.Matches(46, HotkeyModifiers.Control | HotkeyModifiers.Alt));
        Assert.False(commit.Combination.Matches(46, HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift));
        Assert.Equal("Ctrl+Alt+C", commit.Combination.DisplayString(LinuxShortcutKeymap.Instance));
    }
}
