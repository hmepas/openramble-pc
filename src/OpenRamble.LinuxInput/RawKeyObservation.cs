using OpenRamble.AppCore;

namespace OpenRamble.LinuxInput;

/// <summary>
/// One key event as the keyboard reader saw it, after the chord bookkeeping and before any
/// gesture was read out of it.
/// </summary>
/// <param name="Code">The evdev keycode.</param>
/// <param name="Down">Whether the key went down. Auto-repeat counts as down.</param>
/// <param name="Repeat">The kernel repeating a held key, which is not a press.</param>
/// <param name="Held">
/// The chord modifiers down at that instant, the key itself included when it is one, with lock
/// keys already folded away - exactly what <see cref="KeyCombination.Matches"/> asks for.
/// </param>
public readonly record struct RawKeyEvent(int Code, bool Down, bool Repeat, HotkeyModifiers Held);

/// <summary>
/// Where the copy shortcut reads the keyboard from: the same devices the dictation key is read
/// from, through the same reader threads.
/// </summary>
/// <remarks>
/// <para>
/// A second monitor opening <c>/dev/input</c> on its own would double every reader thread and
/// every open descriptor for one chord's worth of comparison, so the keyboard monitor publishes
/// what it already reads and the shortcut monitor subscribes. What is published is not
/// remembered anywhere: a subscriber compares a code against one chord and drops the rest, which
/// is the same twenty lines of discipline the dictation key itself is held to. Nothing here logs,
/// counts or times a key.
/// </para>
/// <para>
/// Only keyboard events are published. An edge injected over the control socket names the
/// configured dictation key and nothing else, so it can never be part of a chord; the compositor
/// path (<c>hotkey press</c>/<c>release</c>) therefore cannot drive the copy shortcut, and a
/// person on that path binds their copy key in the compositor instead.
/// </para>
/// </remarks>
public interface IRawKeySource
{
    /// <summary>Raised on the reader thread of the device the key came from, in order.</summary>
    event Action<RawKeyEvent>? KeyObserved;
}
