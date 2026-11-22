namespace OpenRamble.AppCore;

/// <summary>
/// What else the chosen key already does on this machine.
///
/// The macOS original is <c>FnKeyUsage</c>: it read the <c>AppleFnUsageType</c> preference to
/// find out whether the system had already claimed the 🌐 key. A PC has no such preference
/// and no Fn key, so the question is recast rather than translated - here the answer follows
/// from the key itself plus the active keyboard layout, and the type keeps the original's
/// shape: classify, ask "is it taken", name the action.
/// </summary>
/// <remarks>
/// Every case below is a fact about PC keyboards and keyboard layouts, so the classification is
/// shared: Caps Lock shouts and AltGr composes on Windows and on Linux alike. What is not shared
/// is where a person goes to take the key back, and that sentence is passed in - see
/// <see cref="HotkeyAdvice.Warning"/>.
/// </remarks>
public enum KeyUsage
{
    /// <summary>Nothing else uses this key. It can be held for as long as one likes.</summary>
    Free,

    /// <summary>
    /// Caps Lock: the system flips the caps state on key-down. Dictation swallows the key
    /// while it owns it, but a remapper or another hook that got there first still fires.
    /// </summary>
    CapsLockToggle,

    /// <summary>
    /// Right Alt is AltGr on this layout and composes characters - @, €, and most of the
    /// punctuation on European and Russian typographic layouts.
    /// </summary>
    AltGraph,

    /// <summary>
    /// Right Ctrl or Right Shift on a machine where keyd is remapping the keyboard: keyd grabs
    /// the physical keyboards and re-sends this key through its virtual keyboard as the left
    /// twin, so the right-hand code never arrives anywhere. Not taken - gone.
    /// </summary>
    FoldedIntoLeftTwin,

    /// <summary>
    /// F13: no shipping keyboard sends it without a remapper. Not taken by anything - simply
    /// not there.
    /// </summary>
    MissingOnManyKeyboards,

    /// <summary>
    /// The key could not be classified. Treated as taken: staying silent here is more
    /// expensive than warning one extra time.
    /// </summary>
    Unknown,
}

public static class KeyUsageInfo
{
    /// <summary>
    /// What the chosen key is already used for.
    /// </summary>
    /// <param name="layoutHasAltGraph">
    /// Whether the active keyboard layout composes with Right Alt. Read from the layout, not
    /// guessed from the locale: the same person switches between a layout that has AltGr and
    /// one that does not, several times an hour.
    /// </param>
    /// <param name="sourceSwallowsKey">
    /// Whether the thing delivering the hotkey keeps the key's events from the rest of the
    /// system. Only then does Right Alt stop composing: a source that merely reads alongside -
    /// the Windows hook for every key but Caps Lock, evdev on Linux - leaves AltGr working, and
    /// Right Alt plus another key is a chord that ends the hold rather than dictating over it.
    /// A compositor keybind driving the control socket does consume the key.
    /// </param>
    /// <param name="keydFoldsRightModifiers">
    /// Whether keyd's virtual keyboard is present, which means Right Ctrl and Right Shift are
    /// re-sent as their left twins and never arrive as themselves.
    /// </param>
    public static KeyUsage Of(
        DictationHotkey hotkey,
        bool layoutHasAltGraph,
        bool sourceSwallowsKey,
        bool keydFoldsRightModifiers) => hotkey switch
    {
        DictationHotkey.RightCtrl or DictationHotkey.RightShift when keydFoldsRightModifiers =>
            KeyUsage.FoldedIntoLeftTwin,
        DictationHotkey.RightCtrl => KeyUsage.Free,
        // Held alone, Shift types nothing and toggles nothing: it only means something together
        // with another key, and another key ends the gesture before it can mean anything.
        DictationHotkey.RightShift => KeyUsage.Free,
        // The left keys carry every shortcut a person types, and that is exactly why they are
        // safe: a second key while the hotkey is held is a shortcut, and the gesture machine ends
        // the hold rather than dictating over it - the rule the original shipped Left Control with.
        DictationHotkey.LeftCtrl => KeyUsage.Free,
        DictationHotkey.LeftShift => KeyUsage.Free,
        DictationHotkey.RightAlt => layoutHasAltGraph && sourceSwallowsKey
            ? KeyUsage.AltGraph
            : KeyUsage.Free,
        DictationHotkey.CapsLock => KeyUsage.CapsLockToggle,
        DictationHotkey.F13 => KeyUsage.MissingOnManyKeyboards,
        _ => KeyUsage.Unknown,
    };

    extension(KeyUsage usage)
    {
        /// <summary>Whether something else takes the press for itself.</summary>
        public bool IsTakenBySystem => usage switch
        {
            KeyUsage.Free => false,
            // Not taken - absent. The warning it earns is about a different problem.
            KeyUsage.MissingOnManyKeyboards => false,
            KeyUsage.FoldedIntoLeftTwin => false,
            // We consider the unfamiliar value to be busy: staying silent here is more
            // expensive than warning again.
            _ => true,
        };

        public string? SystemAction => usage switch
        {
            KeyUsage.Free => null,
            KeyUsage.MissingOnManyKeyboards => null,
            KeyUsage.FoldedIntoLeftTwin => null,
            KeyUsage.CapsLockToggle => "the caps lock toggle",
            KeyUsage.AltGraph => "AltGr character composition",
            _ => "a system action",
        };
    }
}

/// <summary>Warnings about the selected hotkey.</summary>
public static class HotkeyAdvice
{
    /// <summary>Where a Windows machine keeps the setting that owns the key.</summary>
    public const string WindowsSettingsHint = "To change this: Settings → Time & language → Typing.";

    /// <summary>
    /// The Linux counterpart of <see cref="WindowsSettingsHint"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately vaguer than the Windows one, and that vagueness is the honest answer: there is
    /// no single page to send a person to. Caps Lock behaviour lives in the XKB options - reached
    /// through GNOME Tweaks, KDE's Keyboard page, <c>setxkbmap</c> or a compositor's own config -
    /// and naming one of those would be wrong on every desktop but that one.
    /// </remarks>
    public const string LinuxSettingsHint =
        "To change this, look for the keyboard layout options in your desktop's settings.";

    /// <summary>
    /// What's wrong with the choice. <c>null</c> - everything is fine.
    ///
    /// A warning, not a ban: every key on the list works, and each one is the most
    /// comfortable choice for somebody. The default is Right Ctrl, so only a person who
    /// chose otherwise gets here.
    /// </summary>
    /// <param name="settingsHint">
    /// One sentence saying where the system setting that owns the key can be changed, or
    /// <c>null</c> when the platform has no one place to send a person to. The rest of the copy is
    /// shared, because the rest of it is true everywhere: Caps Lock shouts, AltGr composes, and
    /// almost no keyboard sends F13. Required rather than defaulted, so that adding a platform is
    /// a compile error and not a paragraph that quietly names the wrong Settings app.
    /// </param>
    public static string? Warning(DictationHotkey hotkey, KeyUsage usage, string? settingsHint)
    {
        if (hotkey == DictationHotkey.F13 || usage == KeyUsage.MissingOnManyKeyboards)
        {
            return "Most keyboards cannot send F13 at all — without a remapper or a macro key, "
                + "dictation won't start.";
        }

        if (usage == KeyUsage.FoldedIntoLeftTwin)
        {
            string twin = hotkey == DictationHotkey.RightShift
                ? DictationHotkey.LeftShift.Title
                : DictationHotkey.LeftCtrl.Title;
            return $"keyd is remapping this keyboard and re-sends {hotkey.Title} as {twin}, so "
                + $"{hotkey.Title} never arrives and dictation won't start. Choose {twin} or "
                + $"{DictationHotkey.RightAlt.Title} instead, or map the key in keyd's config.";
        }

        if (!usage.IsTakenBySystem)
        {
            return null;
        }

        if (usage.SystemAction is string action)
        {
            return $"Pressing {hotkey.Title} already triggers {action} — dictation and that "
                + "action will fire together unless OpenRamble is allowed to swallow the key."
                + (settingsHint is null ? "" : " " + settingsHint);
        }

        return $"Make sure {hotkey.Title} is not assigned to anything else — another hook or "
            + "remapper holding the same key will fire together with dictation.";
    }
}
