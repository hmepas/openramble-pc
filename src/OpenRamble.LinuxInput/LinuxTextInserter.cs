using System.Text;
using DictationCore;
using OpenRamble.AppCore;

namespace OpenRamble.LinuxInput;

/// <summary>
/// Insert recognized text into whatever the person is typing in, on Linux.
///
/// The peer of <c>OpenRamble.App</c>'s <c>TextInserter</c>, and deliberately the same shape: the
/// path is chosen by <see cref="InsertionPathPolicy"/>, the refusals come from
/// <see cref="TextInsertionPolicy"/>, the clipboard is borrowed under
/// <see cref="ClipboardSnapshotLedger"/>, and the diagnostics are the same <c>insert.*</c> events.
/// Only the two irreversible acts differ - synthesised typing is <c>wtype</c> or <c>ydotool</c>
/// rather than <c>SendInput</c>, and the clipboard is <c>wl-clipboard</c> rather than
/// <c>user32</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A paste knows whether it landed</b> (upstream 0.33.1). The dictation stays on the clipboard
/// until something reads it after Ctrl+V - see <see cref="PasteReadReceipts"/> for how a read is
/// seen at all - and the person's clipboard comes back 50 ms after that read, never sooner than
/// 250 ms after the keystroke, before the insertion returns. Nothing read within three seconds
/// means nothing was pasted: the clipboard comes back and <see cref="TextInsertionError.PasteNotRead"/>
/// is thrown, so the words wait instead of vanishing. A read cannot say who made it, so a clipboard
/// watcher that reads just after Ctrl+V makes the paste look landed, exactly as upstream says of
/// its own receipt; reads made before Ctrl+V (a watcher reacting to the offer) do not count. On a
/// machine where reads cannot be seen, the old fixed-delay restore stands and nothing is claimed.
/// </para>
/// <para>
/// <b>There is no foreground window here, and that is a protocol decision, not an omission.</b>
/// Wayland deliberately gives a client no way to learn which surface has focus, which application
/// owns it, or what its process is; there is no <c>GetForegroundWindow</c> and nothing to build one
/// out of. Three of the Windows inserter's behaviours therefore cannot exist:
/// </para>
/// <list type="bullet">
/// <item><description><b>Returning focus.</b> Nothing here can raise a window. It is also not
/// needed in the same way: the shell's overlay is a layer-shell surface that never takes focus, so
/// the application the person dictated into still has it.</description></item>
/// <item><description><b>Noticing that the target changed.</b>
/// <see cref="TextInsertionPolicy.Recheck"/> is still called, in the same two places, because the
/// order it enforces is the product - but it is asked about an environment that can only say "the
/// target is where it was". If the person changes window between speaking and the text landing, the
/// text lands in the new window. That is a real difference from the Windows build and it is written
/// down rather than papered over.</description></item>
/// <item><description><b>Detecting protected input.</b> There is no UIPI and no secure-input flag;
/// a password field on Wayland looks like any other. Synthesised keystrokes are accepted there,
/// which is the platform's answer and not ours to improve.</description></item>
/// </list>
/// <para>
/// <b>What replaces <c>GetAsyncKeyState</c>.</b> Ctrl+V must not be sent while the person still
/// holds a modifier, or it becomes somebody else's combination. Nothing on Wayland reports keyboard
/// state, so the answer comes from the one thing in this process that does see every key:
/// <see cref="EvdevKeyboardMonitor.HeldModifiers"/>, handed in as a delegate. Without it the wait
/// is skipped, which is the honest behaviour for a build with no key monitor rather than a pretence
/// that the modifiers are clear.
/// </para>
/// </remarks>
public sealed class LinuxTextInserter : ITextInserting
{
    /// <summary>
    /// The target, when there is nothing to identify it with.
    /// </summary>
    /// <remarks>
    /// Not <c>null</c>: a null target means "there is nowhere to insert", and the policy refuses it
    /// outright - which on this platform would refuse every dictation. What is true instead is that
    /// there IS somewhere to insert and its identity is unknowable, so the target is a placeholder
    /// with no process and no name. <c>PromptMode</c> reads the name to pick a prompt and gets
    /// <c>Unknown</c>, which is its own safe default.
    ///
    /// Used where the desktop will not say which window has focus. Where it will (Hyprland, plain
    /// X11), <see cref="FocusedWindow"/> names it by its class, so the text can be shaped for it.
    /// </remarks>
    private static readonly TargetApplication WhateverHasFocus = new(null, 0, null);

    /// <summary>
    /// The person's clipboard comes back this long after the first read. A paste's own reads (an
    /// application asking for several types) come within a few milliseconds of each other.
    /// </summary>
    public static readonly TimeSpan ReadQuiet = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// ...and never sooner than this after Ctrl+V. A read cannot say which application made it, so
    /// a clipboard watcher that reads first must not take the text away from a target about to.
    /// Upstream measured the slowest real first read at 149 ms; nobody has measured Linux's.
    /// </summary>
    public static readonly TimeSpan ReadFloor = TimeSpan.FromMilliseconds(250);

    /// <summary>No read by then means nothing was pasted.</summary>
    public static readonly TimeSpan ReadLimit = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long a caller may wait for an insertion: the read limit, plus room for the modifiers to
    /// come up and for the tools to run before and after Ctrl+V.
    /// </summary>
    public static readonly TimeSpan InsertionDeadline = ReadLimit + TimeSpan.FromSeconds(2);

    private static readonly TimeSpan ReadPollInterval = TimeSpan.FromMilliseconds(5);

    /// <summary>How long the offer's reads stay still before Ctrl+V. See <c>OfferSettled</c>.</summary>
    private static readonly TimeSpan OfferQuiet = TimeSpan.FromMilliseconds(15);

    /// <summary>The most the paste waits for clipboard watchers before Ctrl+V.</summary>
    private static readonly TimeSpan OfferSettleLimit = TimeSpan.FromMilliseconds(60);

    /// <summary>
    /// The snapshot outlives the read limit: three seconds of waiting for a read, then the restore,
    /// must still find the person's bytes. Upstream moved its own budget from 3 s to 5 s for the
    /// same reason.
    /// </summary>
    private readonly ClipboardSnapshotLedger ledger = new(TimeSpan.FromSeconds(5));
    private readonly SynthesizedKeyboard keyboard;
    private readonly WaylandClipboard clipboard;
    private readonly PasteReadReceipts? readReceipts;
    private readonly TimeSpan restoreDelay;
    private readonly TimeSpan readFloor;
    private readonly TimeSpan readLimit;
    private readonly Action<TextInsertionError>? reportRestoreFailure;
    private readonly Func<HotkeyModifiers> heldModifiers;
    private readonly Func<TargetApplication?> focusedWindow;
    private readonly Lock gate = new();

    /// <summary>Whether a read can be seen on this machine. Asked once, then known.</summary>
    private ReceiptSupport receiptSupport = ReceiptSupport.Untested;

    /// <summary>The paste under way, while there is one: what a press of the key can cut short.</summary>
    private ReadWait? openReadWait;

    private enum ReceiptSupport
    {
        /// <summary>Nothing has been pasted yet.</summary>
        Untested,

        /// <summary>A read of our own was seen: every paste waits for its read.</summary>
        Works,

        /// <summary>No shim, or a read that left no mark: the fixed-delay restore, claiming nothing.</summary>
        Unavailable,
    }

    /// <summary>One paste's wait for its read.</summary>
    private sealed class ReadWait
    {
        /// <summary>The person pressed the dictation key: wait only as long as the floor.</summary>
        public bool PressedAgain { get; set; }
    }

    /// <param name="restoreDelay">
    /// How long the dictation stays on the clipboard after Ctrl+V where a read cannot be seen.
    /// Slow applications fetch the offer a moment after the keystroke.
    /// </param>
    /// <param name="reportRestoreFailure">
    /// Where a restore that failed after a paste landed is reported. The words are in place, so it
    /// is no failure of the insertion and is not thrown - and without this the person silently
    /// loses whatever they had copied, with the dictated text sitting in its place.
    /// </param>
    /// <param name="heldModifiers">
    /// What is physically held right now, normally
    /// <see cref="EvdevKeyboardMonitor.HeldModifiers"/>.
    /// </param>
    public LinuxTextInserter(
        TimeSpan? restoreDelay = null,
        Action<TextInsertionError>? reportRestoreFailure = null,
        Func<HotkeyModifiers>? heldModifiers = null)
        : this(
            new SubprocessCommandRunner(),
            restoreDelay,
            reportRestoreFailure,
            heldModifiers,
            static () => FocusedWindow.Query(new SubprocessCommandRunner()),
            new PasteReadReceipts(PasteReadReceipts.DefaultDirectory()))
    {
    }

    /// <summary>The same inserter with the tools replaced - the seam the tests drive.</summary>
    /// <param name="readReceipts">
    /// Where reads are counted; <c>null</c> for a desktop on which they cannot be, which restores
    /// after <paramref name="restoreDelay"/> and claims nothing about the paste.
    /// </param>
    internal LinuxTextInserter(
        ICommandRunner runner,
        TimeSpan? restoreDelay = null,
        Action<TextInsertionError>? reportRestoreFailure = null,
        Func<HotkeyModifiers>? heldModifiers = null,
        Func<TargetApplication?>? focusedWindow = null,
        PasteReadReceipts? readReceipts = null,
        TimeSpan? readFloor = null,
        TimeSpan? readLimit = null)
    {
        keyboard = new SynthesizedKeyboard(runner);
        clipboard = new WaylandClipboard(runner);
        this.readReceipts = readReceipts;
        this.restoreDelay = restoreDelay ?? TimeSpan.FromSeconds(1);
        this.readFloor = readFloor ?? ReadFloor;
        this.readLimit = readLimit ?? ReadLimit;
        this.reportRestoreFailure = reportRestoreFailure;
        this.heldModifiers = heldModifiers ?? (static () => HotkeyModifiers.None);
        this.focusedWindow = focusedWindow ?? (static () => null);
    }

    /// <inheritdoc cref="WhateverHasFocus"/>
    public TargetApplication? FrontmostApplication() => focusedWindow() ?? WhateverHasFocus;

    /// <summary>
    /// A wrapper over a single implementation.
    ///
    /// There is exactly one body, for the reason the Windows one has exactly one: two would drift,
    /// and the speed readout would show a path the product does not take.
    /// </summary>
    public async Task Insert(string text, TargetApplication? target) =>
        await InsertReportingMarks(text, target).ConfigureAwait(false);

    public async Task<InsertionMarks> InsertReportingMarks(string text, TargetApplication? target)
    {
        // Length, never content: the log has to be able to say "the text was there and the
        // insertion still did not happen", and that takes a number, not the words.
        InsertionPath path = InsertionPathPolicy.Choose(text, target);
        int session = DiagnosticLog.Session;
        DiagnosticLog.Emit(
            "insert.begin",
            ("session", session),
            ("chars", text.Length),
            ("path", path),
            ("has_target", target is not null));

        if (TextInsertionPolicy.IsNoOp(text))
        {
            DiagnosticLog.Emit("insert.end", ("session", session), ("ok", true), ("path", "none"));
            return new InsertionMarks();
        }

        // The Windows path returns focus here and waits for it to settle. Nothing on Wayland can
        // raise a window, and the target it would raise is unidentifiable anyway - so the target is
        // alive if there is one, and the settle delay would be a tenth of a second added to every
        // dictation for nothing.
        bool alive = target is not null;

        if (TextInsertionPolicy.BeforeClipboard(target, alive, ReadEnvironment(target)) is InsertionRefusal refused)
        {
            DiagnosticLog.Emit(
                "insert.refused",
                ("session", session),
                ("path", path),
                ("reason", refused),
                ("activated", alive));
            throw TextInsertionPolicy.ToError(refused);
        }

        // Non-null: BeforeClipboard refuses a null target.
        TargetApplication destination = target!;

        await WaitForModifiersRelease().ConfigureAwait(false);

        try
        {
            InsertionMarks marks = path switch
            {
                InsertionPath.SynthesizedKeystrokes => TypeDirectly(text, destination),
                _ => await PasteThroughClipboard(text, destination).ConfigureAwait(false),
            };
            DiagnosticLog.Emit(
                "insert.end",
                ("session", session),
                ("ok", true),
                ("path", path),
                ("pasted", marks.PasteDispatchedAt is not null),
                ("restored", marks.ClipboardRestoredAt is not null),
                ("read_ms", marks.PasteDispatchedAt is { } sent && marks.FirstReadAt is { } read
                    ? sent.DurationTo(read)
                    : null));
            return marks;
        }
        catch (Exception error)
        {
            // The tool that last ran, where Windows logs the Win32 code: on this platform the whole
            // diagnosis is which of the two typing tools was in use, and its own complaint has
            // already been written by insert.tool-failed.
            DiagnosticLog.EmitException(
                "insert.failed",
                error,
                ("session", session),
                ("path", path),
                ("tool", keyboard.LastTool ?? "none"));
            throw;
        }
    }

    public Task PressReturn()
    {
        keyboard.PressReturn();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>A press with no paste under way belongs to nothing.</remarks>
    public void EndTheReadWait()
    {
        lock (gate)
        {
            openReadWait?.PressedAgain = true;
        }
    }

    /// <summary>
    /// Put text on the clipboard for the person, through the one writer that carries the
    /// restrictions.
    ///
    /// The peer of <c>TextInserter.CopyHostOnly</c>, and the same reason for existing: a second
    /// clipboard writer somewhere else in the application would be a second chance to forget
    /// <see cref="LinuxClipboardRestrictions"/> and hand a dictation to Klipper.
    /// </summary>
    public void CopyForPerson(string text) => clipboard.Write(text);

    /// <summary>
    /// The direct path: the tool types the characters, and the clipboard is never touched.
    /// </summary>
    private InsertionMarks TypeDirectly(string text, TargetApplication target)
    {
        // The last look, after the wait and immediately before the irreversible event. It can only
        // answer one way on this platform - see the class remarks - and it stays because the order
        // is the product, and because the day something can answer differently this is where it
        // plugs in.
        if (TextInsertionPolicy.Recheck(target, ReadEnvironment(target)) is InsertionRefusal refused)
        {
            throw TextInsertionPolicy.ToError(refused);
        }

        keyboard.Type(text);
        return new InsertionMarks(PasteDispatchedAt: MonotonicInstant.Now);
    }

    /// <summary>
    /// The clipboard path: borrow the clipboard, paste, give it back.
    ///
    /// <see cref="LinuxClipboardRestrictions"/> says what that is worth here. There is no history
    /// service to opt out of, so the promise is the clearing - after the restore the dictation is
    /// not on the clipboard - plus the one marker KDE honours, which
    /// <see cref="WaylandClipboard"/> sets when the tool is new enough to know it.
    /// </summary>
    private async Task<InsertionMarks> PasteThroughClipboard(string text, TargetApplication target)
    {
        // Open from the start: a press before Ctrl+V still leaves the target the floor to read.
        var wait = new ReadWait();
        lock (gate) { openReadWait = wait; }

        try
        {
            return await PasteThroughClipboard(text, target, wait).ConfigureAwait(false);
        }
        finally
        {
            lock (gate) { openReadWait = null; }
        }
    }

    private async Task<InsertionMarks> PasteThroughClipboard(string text, TargetApplication target, ReadWait wait)
    {
        ClipboardContents before = clipboard.Read();
        uint fingerprintBefore = ClipboardFingerprint.Of(before);

        // A second dictation inside the restore window of the first lands on OUR text. Taking a
        // fresh snapshot there would capture the dictation instead of the person's clipboard, and
        // the person's own content would be gone for good.
        ClipboardClaim inherited = ledger.Inherit(fingerprintBefore);
        ClipboardSnapshot snapshot;
        bool inheritsLostContents;
        switch (inherited)
        {
            case ClipboardClaim.Snapshot previous:
                snapshot = previous.Contents;
                inheritsLostContents = false;
                break;

            case ClipboardClaim.Expired:
                // The privacy budget already erased the person's contents; only the fact that the
                // clipboard is still ours survived. That fact is passed on: restore has to say it
                // out loud rather than leave the dictation sitting there.
                snapshot = ClipboardSnapshot.Empty;
                inheritsLostContents = true;
                break;

            default:
                snapshot = Snapshot(before);
                inheritsLostContents = false;
                break;
        }

        using Receipt? receipt = CurrentReceiptSupport() == ReceiptSupport.Unavailable ? null : readReceipts?.Open();
        clipboard.Write(text, receipt);
        uint owned = ClipboardFingerprint.Of(text);
        Guid transaction = inheritsLostContents
            ? ledger.InsertExpired(owned)
            : ledger.Insert(snapshot, owned);

        bool watched = receipt is not null && ReadsAreSeen(receipt);
        // Reads before Ctrl+V are a watcher reacting to the offer, not the paste.
        int readsBeforePaste = watched ? await OfferSettled(receipt!).ConfigureAwait(false) : 0;

        try
        {
            if (TextInsertionPolicy.Recheck(target, ReadEnvironment(target)) is InsertionRefusal refused)
            {
                throw TextInsertionPolicy.ToError(refused);
            }

            keyboard.Paste(terminal: PromptModePolicy.Category(target) == ApplicationCategory.Terminal);
        }
        catch
        {
            RestoreClipboard(transaction);
            throw;
        }

        // The headline mark: from here on the text is in someone else's window. Everything after is
        // clipboard protection, and that is waiting, not working.
        var dispatched = MonotonicInstant.Now;

        if (watched)
        {
            return await RestoreOnceRead(receipt!, readsBeforePaste, dispatched, transaction, wait)
                .ConfigureAwait(false);
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(restoreDelay).ConfigureAwait(false);
            try
            {
                RestoreClipboard(transaction);
            }
            catch (TextInsertionError)
            {
                // The insertion itself succeeded, so this cannot be thrown - the caller is long
                // gone. Reporting it is the only honest option: what failed is putting the person's
                // own clipboard back.
                reportRestoreFailure?.Invoke(new TextInsertionError.InsertedButClipboardRestoreFailed());
            }
        });

        return new InsertionMarks(PasteDispatchedAt: dispatched);
    }

    /// <summary>
    /// Wait for the target to read the paste, give the clipboard back, and say whether it landed.
    /// </summary>
    /// <exception cref="TextInsertionError.PasteNotRead">Nothing read the paste.</exception>
    /// <exception cref="TextInsertionError.ClipboardRestoreFailed">
    /// Nothing read the paste, and the person's clipboard could not be put back either.
    /// </exception>
    private async Task<InsertionMarks> RestoreOnceRead(
        Receipt receipt,
        int readsBeforePaste,
        MonotonicInstant pastedAt,
        Guid transaction,
        ReadWait wait)
    {
        MonotonicInstant? firstRead = await WaitForTheRead(receipt, readsBeforePaste, pastedAt, wait)
            .ConfigureAwait(false);
        // The last look comes before the restore, never after: the restore reads the clipboard to
        // see whether it is still ours, and that read of our own would count.
        firstRead ??= receipt.Reads > readsBeforePaste ? MonotonicInstant.Now : null;

        MonotonicInstant? restoredAt = null;
        try
        {
            RestoreClipboard(transaction);
            restoredAt = MonotonicInstant.Now;
        }
        catch (TextInsertionError) when (firstRead is not null)
        {
            // The words landed, so this is no failure of the insertion; what failed is putting the
            // person's own clipboard back, and that is theirs to know.
            reportRestoreFailure?.Invoke(new TextInsertionError.InsertedButClipboardRestoreFailed());
        }

        if (firstRead is null)
        {
            // Ctrl+V went out and nobody read the clipboard. There is no protected typing on this
            // platform to blame, so it is said as it is: nothing was pasted.
            throw new TextInsertionError.PasteNotRead();
        }

        return new InsertionMarks(pastedAt, restoredAt, firstRead);
    }

    /// <summary>
    /// The first read after Ctrl+V, or <c>null</c> when the limit says nobody will read.
    /// </summary>
    /// <remarks>
    /// One rule for every read: a quiet moment after it, and never before the floor. A read by a
    /// clipboard watcher just after Ctrl+V then costs at most the floor - never a false alarm, and
    /// never the text taken away from a target that reads within the floor. A press of the
    /// dictation key brings the limit down to the floor: nothing has appeared and the person is
    /// trying again. The read is timed where it is seen, within one poll of when it happened.
    /// </remarks>
    private async Task<MonotonicInstant?> WaitForTheRead(
        Receipt receipt,
        int readsBeforePaste,
        MonotonicInstant pastedAt,
        ReadWait wait)
    {
        MonotonicInstant? firstRead = null;
        while (true)
        {
            MonotonicInstant now = MonotonicInstant.Now;
            if (firstRead is null && receipt.Reads > readsBeforePaste) { firstRead = now; }

            if (firstRead is { } read)
            {
                if (read.DurationTo(now) >= ReadQuiet && pastedAt.DurationTo(now) >= readFloor) { return read; }
            }
            else
            {
                bool pressedAgain;
                lock (gate) { pressedAgain = wait.PressedAgain; }

                if (pastedAt.DurationTo(now) >= (pressedAgain ? readFloor : readLimit)) { return null; }
            }

            await Task.Delay(ReadPollInterval).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The offer's reads once clipboard watchers have had theirs, so that theirs are not taken for
    /// the paste's.
    /// </summary>
    /// <remarks>
    /// A watcher (<c>wl-paste --watch</c>, which is how cliphist and most clipboard histories run)
    /// reads every new offer the moment it appears: measured under a headless wlroots compositor,
    /// within 10 ms of <c>wl-copy</c> returning. Ctrl+V sent before that read lands would count it
    /// as the target's, and an unread paste would look landed. So Ctrl+V waits until the reads
    /// have been quiet for <see cref="OfferQuiet"/>, and never longer than
    /// <see cref="OfferSettleLimit"/> - a watcher slower than that is still taken for a reader.
    /// </remarks>
    private static async Task<int> OfferSettled(Receipt receipt)
    {
        MonotonicInstant began = MonotonicInstant.Now;
        MonotonicInstant lastChange = began;
        int reads = receipt.Reads;
        while (true)
        {
            MonotonicInstant now = MonotonicInstant.Now;
            if (lastChange.DurationTo(now) >= OfferQuiet || began.DurationTo(now) >= OfferSettleLimit) { return reads; }

            await Task.Delay(ReadPollInterval).ConfigureAwait(false);
            int seen = receipt.Reads;
            if (seen != reads)
            {
                reads = seen;
                lastChange = MonotonicInstant.Now;
            }
        }
    }

    private ReceiptSupport CurrentReceiptSupport()
    {
        lock (gate) { return receiptSupport; }
    }

    /// <summary>
    /// Whether this machine lets a read be seen, asked once with a read of our own of the offer
    /// just made.
    /// </summary>
    /// <remarks>
    /// Before Ctrl+V and not counted: the paste's reads are only those after the keystroke. A
    /// machine where our read leaves no mark keeps the fixed-delay restore for good, because a
    /// receipt that never comes would call every paste unread.
    /// </remarks>
    private bool ReadsAreSeen(Receipt receipt)
    {
        ReceiptSupport known = CurrentReceiptSupport();
        if (known != ReceiptSupport.Untested) { return known == ReceiptSupport.Works; }

        int before = receipt.Reads;
        _ = clipboard.Read();
        ReceiptSupport found = receipt.Reads > before ? ReceiptSupport.Works : ReceiptSupport.Unavailable;
        lock (gate) { receiptSupport = found; }

        DiagnosticLog.Emit("insert.read-receipts", ("seen", found == ReceiptSupport.Works));
        return found == ReceiptSupport.Works;
    }

    /// <summary>
    /// Give the clipboard back, if it is still ours to give.
    /// </summary>
    /// <exception cref="TextInsertionError.ClipboardRestoreFailed">
    /// The dictation had to be cleared because the person's own contents could not be put back -
    /// they had expired from memory, or they were never text.
    /// </exception>
    private void RestoreClipboard(Guid transaction)
    {
        switch (ledger.Take(transaction))
        {
            case ClipboardClaim.Snapshot claim:
                if (ClipboardFingerprint.Of(clipboard.Read()) != claim.OwnedSequence)
                {
                    // The person copied something of their own. Nothing to restore, and touching
                    // their write would be worse than doing nothing.
                    return;
                }

                Restore(claim.Contents);
                return;

            case ClipboardClaim.Expired expired:
                if (ClipboardFingerprint.Of(clipboard.Read()) != expired.OwnedSequence)
                {
                    return;
                }

                // The clipboard is still ours, which means it still holds the dictation. It cannot
                // be left there, and calling this a success would silently lose the person's
                // contents.
                clipboard.Clear();
                throw new TextInsertionError.ClipboardRestoreFailed();

            default:
                return;
        }
    }

    /// <summary>Put a snapshot back, or clear the clipboard when it cannot be put back.</summary>
    private void Restore(ClipboardSnapshot snapshot)
    {
        if (snapshot.Formats.TryGetValue(LinuxClipboardFormats.Utf8Text, out byte[]? text))
        {
            clipboard.Write(Encoding.UTF8.GetString(text));
            return;
        }

        clipboard.Clear();

        if (snapshot.Formats.ContainsKey(LinuxClipboardFormats.Unrestorable))
        {
            // An image, a file list, something a text tool cannot carry. The dictation is gone from
            // the clipboard, which is the promise that could be kept; the person's own content is
            // not coming back, and that is worth a notice rather than silence.
            throw new TextInsertionError.ClipboardRestoreFailed();
        }
    }

    /// <summary>What was there before, in the shape the shared ledger stores.</summary>
    private static ClipboardSnapshot Snapshot(ClipboardContents contents)
    {
        if (contents.Text is string text)
        {
            return new ClipboardSnapshot(new Dictionary<uint, byte[]>
            {
                [LinuxClipboardFormats.Utf8Text] = Encoding.UTF8.GetBytes(text),
            });
        }

        if (contents.HasUnrestorableContents)
        {
            return new ClipboardSnapshot(new Dictionary<uint, byte[]>
            {
                [LinuxClipboardFormats.Unrestorable] = [],
            });
        }

        return ClipboardSnapshot.Empty;
    }

    /// <summary>
    /// What the system says about the moment of insertion - which here is almost nothing.
    /// </summary>
    /// <remarks>
    /// The frontmost process is reported as the target's own, because that is the only statement
    /// this platform supports: no client may ask who has focus, so "it is still where it was" is
    /// not an assumption being made, it is the end of what can be known. Protected input is
    /// reported as absent for the same reason - there is no flag to read, and inventing a
    /// <c>true</c> would refuse every dictation.
    /// </remarks>
    private static InsertionEnvironment ReadEnvironment(TargetApplication? target) =>
        new(target?.ProcessIdentifier ?? 0, TargetIsProtected: false);

    private async Task WaitForModifiersRelease()
    {
        for (int attempt = 0; attempt < ModifierReleasePolicy.PollLimit; attempt++)
        {
            if (ModifierReleasePolicy.IsClear(heldModifiers()))
            {
                return;
            }

            await Task.Delay(ModifierReleasePolicy.PollInterval).ConfigureAwait(false);
        }

        throw TextInsertionPolicy.ToError(InsertionRefusal.ModifiersStillHeld);
    }
}
