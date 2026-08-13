namespace OpenRamble.AppCore;

/// <summary>
/// What else the chosen key already does on this machine.
///
/// The macOS original is <c>FnKeyUsage</c>: it read the <c>AppleFnUsageType</c> preference to
/// find out whether the system had already claimed the 🌐 key. Windows has no such preference
/// and no Fn key, so the question is recast rather than translated - here the answer follows
/// from the key itself plus the active keyboard layout, and the type keeps the original's
/// shape: classify, ask "is it taken", name the action.
/// </summary>
public enum WindowsKeyUsage
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

public static class WindowsKeyUsageInfo
{
    /// <summary>
    /// What the chosen key is already used for.
    /// </summary>
    /// <param name="layoutHasAltGraph">
    /// Whether the active keyboard layout composes with Right Alt. Read from the layout, not
    /// guessed from the locale: the same person switches between a layout that has AltGr and
    /// one that does not, several times an hour.
    /// </param>
    public static WindowsKeyUsage Of(DictationHotkey hotkey, bool layoutHasAltGraph) => hotkey switch
    {
        DictationHotkey.RightCtrl => WindowsKeyUsage.Free,
        DictationHotkey.RightAlt => layoutHasAltGraph ? WindowsKeyUsage.AltGraph : WindowsKeyUsage.Free,
        DictationHotkey.CapsLock => WindowsKeyUsage.CapsLockToggle,
        DictationHotkey.F13 => WindowsKeyUsage.MissingOnManyKeyboards,
        _ => WindowsKeyUsage.Unknown,
    };

    extension(WindowsKeyUsage usage)
    {
        /// <summary>Whether something else takes the press for itself.</summary>
        public bool IsTakenBySystem => usage switch
        {
            WindowsKeyUsage.Free => false,
            // Not taken - absent. The warning it earns is about a different problem.
            WindowsKeyUsage.MissingOnManyKeyboards => false,
            // We consider the unfamiliar value to be busy: staying silent here is more
            // expensive than warning again.
            _ => true,
        };

        public string? SystemAction => usage switch
        {
            WindowsKeyUsage.Free => null,
            WindowsKeyUsage.MissingOnManyKeyboards => null,
            WindowsKeyUsage.CapsLockToggle => "the caps lock toggle",
            WindowsKeyUsage.AltGraph => "AltGr character composition",
            _ => "a system action",
        };
    }
}

/// <summary>Warnings about the selected hotkey.</summary>
public static class HotkeyAdvice
{
    /// <summary>
    /// What's wrong with the choice. <c>null</c> - everything is fine.
    ///
    /// A warning, not a ban: every key on the list works, and each one is the most
    /// comfortable choice for somebody. The default is Right Ctrl, so only a person who
    /// chose otherwise gets here.
    /// </summary>
    public static string? Warning(DictationHotkey hotkey, WindowsKeyUsage usage)
    {
        if (hotkey == DictationHotkey.F13 || usage == WindowsKeyUsage.MissingOnManyKeyboards)
        {
            return "Most keyboards cannot send F13 at all — without a remapper or a macro key, "
                + "dictation won't start.";
        }

        if (!usage.IsTakenBySystem)
        {
            return null;
        }

        if (usage.SystemAction is string action)
        {
            return $"Pressing {hotkey.Title} already triggers {action} — dictation and that "
                + "action will fire together unless OpenRamble is allowed to swallow the key. "
                + "To change this: Settings → Time & language → Typing.";
        }

        return $"Make sure {hotkey.Title} is not assigned to anything else — another hook or "
            + "remapper holding the same key will fire together with dictation.";
    }
}
