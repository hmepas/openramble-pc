namespace OpenRamble.AppCore;

/// <summary>
/// What a clipboard write can and cannot promise <b>on Linux</b>.
///
/// The Windows answer is three registered formats every write must carry, see
/// <see cref="WindowsClipboardRestrictions"/>. Linux has no such list, and the honest form of
/// that is a policy that says what is guaranteed rather than one that hands out markers nobody
/// reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no clipboard-history service to opt out of.</b> Wayland's data-device protocol has
/// no history, no cloud, and no notion of a private entry; X11 has no such notion either. So the
/// hole Windows has to plug with <c>CanIncludeInClipboardHistory</c> does not exist at the
/// protocol level - which is not the same as saying nothing keeps the text.
/// </para>
/// <para>
/// <b>What does keep it is a clipboard manager.</b> Klipper, GPaste, clipman, cliphist and the
/// rest are ordinary applications that watch the selection and write it to their own store, and
/// they are installed by default on most desktops. Only KDE's Klipper honours an opt-out, and it
/// honours exactly one: an offer carrying the <see cref="KdePasswordManagerHint"/> MIME type with
/// the body <see cref="KdePasswordManagerHintSecret"/> is not recorded. It is listed here because
/// a best-effort marker that costs one extra offer is worth setting; it is not listed as
/// <i>required</i>, because on GNOME, on Sway and on anything using GPaste it does nothing at all,
/// and a promise that only holds on one desktop is not a promise.
/// </para>
/// <para>
/// <b>The guarantee is the clearing.</b> What the insertion path can actually promise everywhere
/// is that the clipboard does not keep the dictation after the paste: the text is offered, the
/// paste consumes it, and the previous contents are put back - the same restore
/// <see cref="ClipboardSnapshotLedger"/> already governs. With <c>wl-clipboard</c> this is exact,
/// because <c>wl-copy</c> owns the selection from a process we started and stopping that process
/// withdraws the offer; a manager that copied it in the meantime keeps its copy, and no protocol
/// on this platform can take it back.
/// </para>
/// <para>
/// Policy and data only, like its Windows counterpart: running <c>wl-copy</c> belongs to the
/// Linux insertion adapter and is not this wave's business.
/// </para>
/// </remarks>
public static class LinuxClipboardRestrictions
{
    /// <summary>
    /// The one opt-out any Linux clipboard manager honours - and only Klipper does. Offered
    /// alongside the text, with <see cref="KdePasswordManagerHintSecret"/> as its body.
    /// </summary>
    public const string KdePasswordManagerHint = "x-kde-passwordManagerHint";

    /// <summary>The body <see cref="KdePasswordManagerHint"/> must carry to mean "do not record".</summary>
    public const string KdePasswordManagerHintSecret = "secret";

    /// <summary>
    /// Nothing is mandatory, and the empty list is the statement.
    ///
    /// The Windows policy fails a write that omits a format because omitting one is a leak. Here
    /// there is no format whose absence a test could catch, so pretending otherwise would turn a
    /// platform limitation into a green check mark.
    /// </summary>
    public static IReadOnlyList<string> RequiredFormats { get; } = [];

    /// <summary>
    /// Marks worth setting even though nothing must honour them. One today; the list exists so
    /// that finding a second is an addition rather than a redesign.
    /// </summary>
    public static IReadOnlyList<string> BestEffortFormats { get; } = [KdePasswordManagerHint];

    /// <summary>
    /// The promise that is actually kept: the dictation does not survive the paste in our own
    /// offer. Constant rather than configurable - a build that cleared the clipboard only
    /// sometimes would be the bug.
    /// </summary>
    public const bool ClearsAfterPaste = true;
}
