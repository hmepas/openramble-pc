using System.Runtime.InteropServices;
using OpenRamble.AppCore;

namespace OpenRamble.App;

/// <summary>
/// <see cref="WindowsShortcutKeymap"/> with the key's label asked of the live keyboard layout.
///
/// The table in AppCore names the keys whose names are words - the function row, Enter, the
/// arrows - and the letters and digits, whose virtual-key codes are their own ASCII. What it
/// cannot name is the punctuation row: <c>VK_OEM_1</c> is <c>;</c> on a US keyboard and
/// something else on most others. For those this asks Windows what the key types, the way the
/// macOS original asks <c>UCKeyTranslate</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>An ASCII-capable layout, not necessarily the current one.</b> A shortcut recorded on a
/// Latin layout must still name a real key after switching to Cyrillic - a Mac menu shows ⌘Q
/// while you type in Russian, because the shortcut lives on the physical key rather than on
/// the letter. So the current layout is asked first and its answer used if it is printable
/// ASCII; otherwise every installed layout is tried for one that is; and if none is, the table
/// answers with <c>Key 186</c>, which is at least a name.
/// </para>
/// <para>
/// The translation is asked with no modifiers and with the flag that leaves the keyboard state
/// alone, so asking about a dead key does not swallow the next character someone types.
/// </para>
/// <para>
/// Untested on this host: the WPF app only cross-compiles here. The pure part -
/// <see cref="PickLabel"/> - is the only decision and is kept obviously right.
/// </para>
/// </remarks>
public sealed partial class LiveWindowsShortcutKeymap : IShortcutKeymap
{
    private const uint MapvkVkToVsc = 0;

    /// <summary>Bit 2 of <c>ToUnicodeEx</c>'s flags: do not change the keyboard state (Windows 10 1607+).</summary>
    private const uint DoNotChangeKeyboardState = 0x4;

    private readonly IShortcutKeymap table = WindowsShortcutKeymap.Instance;

    public static LiveWindowsShortcutKeymap Instance { get; } = new();

    public string SuperLabel => table.SuperLabel;

    public bool IsFunctionKey(int keyCode) => table.IsFunctionKey(keyCode);

    public ShortcutSpecialKey Special(int keyCode) => table.Special(keyCode);

    public string LabelFor(int keyCode)
    {
        string fromTable = table.LabelFor(keyCode);
        if (!IsUnnamed(fromTable))
        {
            return fromTable;
        }

        return PickLabel(Translations(keyCode), fromTable);
    }

    /// <summary>
    /// The first translation that is printable ASCII, uppercased; the fallback otherwise.
    /// </summary>
    internal static string PickLabel(IEnumerable<string?> translations, string fallback)
    {
        foreach (string? translated in translations)
        {
            if (translated is { Length: 1 } text && text[0] is >= '!' and <= '~')
            {
                return text.ToUpperInvariant();
            }
        }

        return fallback;
    }

    /// <summary>The table's answer for a key it does not know: <c>Key 186</c>.</summary>
    private static bool IsUnnamed(string label) => label.StartsWith("Key ", StringComparison.Ordinal);

    /// <summary>The current layout first, then every other installed one.</summary>
    private static IEnumerable<string?> Translations(int keyCode)
    {
        nint current = GetKeyboardLayout(0);
        yield return Translate(keyCode, current);

        int count = GetKeyboardLayoutList(0, null);
        if (count <= 0)
        {
            yield break;
        }

        var layouts = new nint[count];
        count = GetKeyboardLayoutList(count, layouts);
        for (int index = 0; index < count; index++)
        {
            if (layouts[index] != current)
            {
                yield return Translate(keyCode, layouts[index]);
            }
        }
    }

    private static string? Translate(int keyCode, nint layout)
    {
        uint scanCode = MapVirtualKeyExW((uint)keyCode, MapvkVkToVsc, layout);
        var keyState = new byte[256];
        // UTF-16 code units, as the source generator marshals them without a char encoding
        // question; the string is rebuilt below.
        var buffer = new ushort[4];
        int written = ToUnicodeEx(
            (uint)keyCode,
            scanCode,
            keyState,
            buffer,
            buffer.Length,
            DoNotChangeKeyboardState,
            layout);
        // Negative is a dead key, zero is "types nothing": neither names the key.
        if (written <= 0)
        {
            return null;
        }

        var characters = new char[written];
        for (int index = 0; index < written; index++)
        {
            characters[index] = (char)buffer[index];
        }

        return new string(characters);
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetKeyboardLayout(uint idThread);

    [LibraryImport("user32.dll")]
    private static partial int GetKeyboardLayoutList(int nBuff, [Out] nint[]? lpList);

    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKeyExW(uint uCode, uint uMapType, nint dwhkl);

    [LibraryImport("user32.dll")]
    private static partial int ToUnicodeEx(
        uint wVirtKey,
        uint wScanCode,
        [In] byte[] lpKeyState,
        [Out] ushort[] pwszBuff,
        int cchBuff,
        uint wFlags,
        nint dwhkl);
}
