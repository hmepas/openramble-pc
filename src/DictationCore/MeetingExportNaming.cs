using System.Globalization;
using System.Text;

namespace DictationCore;

/// <summary>
/// What a shared recording is called once it leaves the app.
/// </summary>
/// <remarks>
/// The name is the person's own title, because that is what they will look for in a Downloads
/// folder a month later. It only has to survive being a file name: separators and colons would
/// silently become subdirectories or be rejected, a leading dot would hide it, and some
/// filesystems stop at 255 bytes. A title made entirely of those characters leaves nothing, so the
/// date stands in rather than an empty name. Pure.
/// </remarks>
public static class MeetingExportNaming
{
    /// <summary>
    /// Characters that cannot appear in a file name on the systems these files travel to, plus
    /// line breaks (Swift's <c>CharacterSet.newlines</c>). Each becomes a space. UTF-16 units; all
    /// are in the BMP.
    /// </summary>
    private static readonly char[] Replaced =
        ['/', '\\', ':', '\0', '\n', '\u000B', '\u000C', '\r', '\u0085', '\u2028', '\u2029'];

    /// <summary>The longest name in UTF-8 bytes: room for an extension inside the 255-byte limit.</summary>
    private const int MaxBytes = 200;

    public static string FileName(string title, string fallback)
    {
        var builder = new StringBuilder(title);
        foreach (char character in Replaced) { builder.Replace(character, ' '); }
        string cleaned = builder.ToString().Trim();
        string name = cleaned.Length == 0 ? fallback : cleaned;
        name = name.TrimStart('.').Trim();
        if (name.Length == 0) { name = fallback; }
        return Truncate(name).Trim();
    }

    /// <summary>
    /// The longest prefix of whole grapheme clusters that fits in <see cref="MaxBytes"/> UTF-8
    /// bytes, so a name never ends mid-emoji.
    /// </summary>
    private static string Truncate(string name)
    {
        if (Encoding.UTF8.GetByteCount(name) <= MaxBytes) { return name; }
        int bytes = 0;
        int length = 0;
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(name);
        while (elements.MoveNext())
        {
            int size = Encoding.UTF8.GetByteCount(elements.GetTextElement());
            if (bytes + size > MaxBytes) { break; }
            bytes += size;
            length = elements.ElementIndex + elements.GetTextElement().Length;
        }

        return name[..length];
    }
}
