using DictationCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// Which keys the pickers offer, and what happens to a choice that is no longer among them.
/// </summary>
public sealed class HotkeyCandidatesTests : IDisposable
{
    private readonly AppHarness harness = new();

    public void Dispose() => harness.Dispose();

    /// <summary>
    /// Six keys, in the order the pickers show them: the four that just work - right side first,
    /// then left - then the two that carry a warning. The left pair is what upstream 0.15.0 added
    /// on the Mac, and what a keyd-remapped Linux keyboard needs, since keyd folds the right
    /// modifiers into the left codes.
    /// </summary>
    /// <remarks>
    /// Both pickers - the one in Settings and the one on the try-out page - read this list, so
    /// this is where "what can a person choose" is decided, once.
    /// </remarks>
    [Fact]
    public void ThePickersOfferTheSixKeysAPersonCanActuallyPress() =>
        Assert.Equal(
            [
                DictationHotkey.RightCtrl,
                DictationHotkey.RightShift,
                DictationHotkey.LeftCtrl,
                DictationHotkey.LeftShift,
                DictationHotkey.RightAlt,
                DictationHotkey.CapsLock,
            ],
            DictationHotkeyInfo.AllCases);

    /// <summary>
    /// F13 is not offered.
    /// </summary>
    /// <remarks>
    /// It is the one candidate a person cannot try on the keyboard they are choosing it with:
    /// sending it takes a remapper or a macro key. A picker row that cannot be tested where it is
    /// chosen costs attention and answers nothing - and the try-out page exists precisely so that
    /// a key choice can be proved on the spot.
    /// </remarks>
    [Fact]
    public void F13IsNoLongerAmongThem() =>
        Assert.DoesNotContain(DictationHotkey.F13, DictationHotkeyInfo.AllCases);

    /// <summary>
    /// A settings file that still says F13 comes back as the default, not as a silent key.
    /// </summary>
    /// <remarks>
    /// This is why the enum case survives: the file is read with <c>Enum.TryParse</c>, and a
    /// value that no longer parses would land in the same fallback anyway - but a value that
    /// parses and is not offered would otherwise arm a key the person can neither press nor see
    /// selected in the picker.
    /// </remarks>
    [Fact]
    public void AStoredF13FallsBackToTheDefaultKey()
    {
        AppSettings stored = harness.Settings.Read();
        harness.Settings.Write(stored with { Hotkey = nameof(DictationHotkey.F13) });

        AppState state = harness.MakeState();

        Assert.Equal(DictationHotkey.RightCtrl, state.Hotkey);
        Assert.Equal(DictationHotkey.RightCtrl, harness.Monitor.Hotkey);
    }
}
