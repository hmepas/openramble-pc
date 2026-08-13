namespace OpenRamble.AppCore;

/// <summary>
/// What must accompany every clipboard write this application makes.
///
/// The macOS original writes with <c>prepareForNewContents(with: .currentHostOnly)</c> plus the
/// nspasteboard.org markers, because a plain <c>clearContents()</c> hands the dictation to the
/// Universal Clipboard and it appears on every device signed into the same Apple ID. Windows
/// has the identical hole in two places - <b>Clipboard History</b> (Win+V, which keeps the last
/// 25 entries) and <b>Cloud Clipboard</b> (which syncs them across the person's PCs) - and it
/// is closed by three registered clipboard formats. All three are mandatory on every write, and
/// this type is the one place that says so.
///
/// A write that skips any of them leaks dictated text - passwords, numbers, medical notes,
/// whatever the person said - into a store that outlives both the dictation and the
/// application. This is the Windows form of the mac rule, and it is not optional.
/// </summary>
/// <remarks>
/// Only the names and the required payloads are here, because only they are policy. Registering
/// the formats and pushing the bytes is <c>OpenRamble.App</c>'s job and needs Win32.
/// </remarks>
public static class ClipboardRestrictions
{
    /// <summary>
    /// Asks clipboard monitors and history providers not to process this entry at all. The
    /// broadest of the three - honoured by Windows itself and by well-behaved third-party
    /// managers.
    /// </summary>
    public const string ExcludeFromMonitorProcessing = "ExcludeClipboardContentFromMonitorProcessing";

    /// <summary>Keeps the entry out of the Win+V history. Payload: a single zero byte.</summary>
    public const string CanIncludeInClipboardHistory = "CanIncludeInClipboardHistory";

    /// <summary>Keeps the entry off the person's other machines. Payload: a single zero byte.</summary>
    public const string CanUploadToCloudClipboard = "CanUploadToCloudClipboard";

    /// <summary>Every restriction format, in the order a write registers them.</summary>
    public static IReadOnlyList<string> RequiredFormats { get; } =
    [
        ExcludeFromMonitorProcessing,
        CanIncludeInClipboardHistory,
        CanUploadToCloudClipboard,
    ];

    /// <summary>
    /// The payload each format must carry.
    ///
    /// The two <c>Can…</c> formats are opt-outs read as a DWORD: zero means no. The exclusion
    /// format is a marker whose presence is the whole message, so it carries an empty payload -
    /// but it must be <b>present</b>, and a present-but-wrong value is what a bug would look
    /// like.
    /// </summary>
    public static byte[] PayloadFor(string format) => format switch
    {
        ExcludeFromMonitorProcessing => [],
        CanIncludeInClipboardHistory => [0, 0, 0, 0],
        CanUploadToCloudClipboard => [0, 0, 0, 0],
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "not a restriction format"),
    };

    /// <summary>
    /// Whether a write that carried exactly these formats is allowed to reach the clipboard.
    ///
    /// The check exists so that "we forgot one" is a failing test rather than a leak found by a
    /// person seeing their dictation in Win+V.
    /// </summary>
    public static bool IsRestricted(IEnumerable<string> formats)
    {
        var present = new HashSet<string>(formats, StringComparer.Ordinal);
        return RequiredFormats.All(present.Contains);
    }
}
