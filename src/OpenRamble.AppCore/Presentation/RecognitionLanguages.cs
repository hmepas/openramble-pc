using System.Globalization;
using LocalASR;

namespace OpenRamble.AppCore;

/// <summary>
/// Catalog of recognition languages for the interface.
///
/// The codes come from the engine - the only source of truth about what it accepts - and the
/// names from the English locale. A hard-coded list of names would drift away from the codes the
/// first time the engine is updated.
/// </summary>
public static class RecognitionLanguages
{
    public sealed record Option(string Code, string Name);

    public static IReadOnlyList<Option> Options { get; } = SherpaOnnxAdapter.SupportedLanguageHints
        .Select(code => new Option(code, EnglishName(code)))
        .OrderBy(option => option.Name, StringComparer.Ordinal)
        .ToArray();

    public static string Name(string? code)
    {
        if (code is null) { return "Automatic"; }
        return Options.FirstOrDefault(option => option.Code == code)?.Name ?? code;
    }

    /// <summary>
    /// Is this a code the engine will accept?
    ///
    /// Asked on load: a saved code the engine no longer knows would fail every dictation on
    /// "unsupported language hint" and leave the picker showing a blank row. An unfamiliar code
    /// means automatic detection, exactly as it was before the person chose anything.
    /// </summary>
    public static bool IsSupported(string? code) =>
        code is not null && SherpaOnnxAdapter.SupportedLanguageHints.Contains(code);

    /// <remarks>
    /// <c>Locale.localizedString(forLanguageCode:)</c> has no direct .NET equivalent;
    /// <c>CultureInfo.EnglishName</c> is the same idea, and its names for these 25 codes arrive
    /// already capitalized ("German", "Ukrainian"). An invariant fallback keeps a machine with a
    /// trimmed ICU set from showing an empty picker.
    /// </remarks>
    private static string EnglishName(string code)
    {
        try
        {
            var culture = new CultureInfo(code);
            // A code ICU does not know yields the invariant culture rather than throwing.
            return culture.TwoLetterISOLanguageName == code ? culture.EnglishName : code;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }
}
