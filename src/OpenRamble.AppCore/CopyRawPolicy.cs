namespace OpenRamble.AppCore;

/// <summary>
/// "Copy Last as Spoken": what the person said, before the dictionary and before the cosmetics.
///
/// The promise that the verbatim text is always available is backed by the recording's
/// provenance in process memory: no reading of foreign fields, no disk write is needed for it.
/// </summary>
public static class CopyRawPolicy
{
    /// <summary>
    /// Is there anything to copy as spoken.
    ///
    /// Only when the raw words actually differ from the inserted text: for most dictations the
    /// dictionary and the cosmetics change nothing, and the menu item would be a duplicate of
    /// the last recent dictation.
    /// </summary>
    public static bool CanCopyRaw(string? raw, string? insertedText)
    {
        if (raw is null || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return raw != insertedText;
    }
}
