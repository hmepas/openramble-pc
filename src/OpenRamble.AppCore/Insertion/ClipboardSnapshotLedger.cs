namespace OpenRamble.AppCore;

/// <summary>
/// What was on the clipboard before the dictation took it over, format by format.
/// </summary>
/// <remarks>
/// Flatter than the macOS original by one level: <c>NSPasteboard</c> holds a list of items,
/// each with its own type map, while the Windows clipboard holds exactly one set of formats.
/// Everything else - keep every representation byte for byte, never let an unfamiliar format
/// block a dictation - carries over. A screenshot copied as CF_DIB + PNG must come back as
/// CF_DIB + PNG.
/// </remarks>
public sealed record ClipboardSnapshot(IReadOnlyDictionary<uint, byte[]> Formats)
{
    public static ClipboardSnapshot Empty { get; } = new(new Dictionary<uint, byte[]>());

    public bool IsEmpty => Formats.Count == 0;
}

/// <summary>What is left of a clipboard transaction by the time restore runs.</summary>
public abstract record ClipboardClaim
{
    private ClipboardClaim()
    {
    }

    /// <summary>The snapshot is still in memory - restoration is possible.</summary>
    public sealed record Snapshot(ClipboardSnapshot Contents, uint OwnedSequence) : ClipboardClaim;

    /// <summary>
    /// The privacy budget has already erased the contents; only the ownership counter is left.
    /// By it, restore tells "the person has since copied their own" apart from "the previous
    /// contents are gone for good".
    /// </summary>
    public sealed record Expired(uint OwnedSequence) : ClipboardClaim;

    /// <summary>There was no such transaction, or it has already been restored.</summary>
    public sealed record Missing : ClipboardClaim
    {
        public static Missing Instance { get; } = new();
    }
}

/// <summary>
/// The bookkeeping behind "borrow the clipboard, then give it back".
///
/// Restoration is deferred by a second after Ctrl+V, and a second dictation inside that second
/// is ordinary life - two short replies in a chat. Three things follow, and all three are what
/// this type exists for:
///
/// <list type="bullet">
/// <item><description>A write that lands on <b>our own</b> dictation must inherit the person's
/// contents from the previous transaction. Snapshotting the board again would capture the
/// dictated text instead, and the person's clipboard would be lost forever.</description></item>
/// <item><description>The snapshot must not outlive its purpose. Even if the calling task died
/// between write and restore, the person's clipboard bytes leave memory on a timer.</description></item>
/// <item><description>Losing the bytes must not lose the <b>fact of ownership</b>. Without it,
/// restore would decide the clipboard was somebody else's and silently leave the dictation
/// sitting in it.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// The sequence number is <c>GetClipboardSequenceNumber</c>, the Windows counterpart of
/// <c>NSPasteboard.changeCount</c>: it belongs to the system, and that is exactly why ownership
/// can be proven with it.
/// </remarks>
public sealed class ClipboardSnapshotLedger
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, (ClipboardSnapshot Contents, uint OwnedSequence)> snapshots = [];

    /// <summary>
    /// Tombstones for expired snapshots. There is no content here - only a number - so the
    /// privacy budget is not violated.
    /// </summary>
    private readonly Dictionary<Guid, uint> expiredOwnership = [];

    private readonly TimeSpan lifetime;

    /// <param name="lifetime">
    /// How long the person's bytes may stay in memory. Three seconds by default: seconds for a
    /// slow application to consume Ctrl+V, up to half a second for the modifiers to be
    /// released, and reserve for the scheduler.
    /// </param>
    public ClipboardSnapshotLedger(TimeSpan? lifetime = null)
    {
        this.lifetime = lifetime ?? TimeSpan.FromSeconds(3);
    }

    public Guid Insert(ClipboardSnapshot contents, uint ownedSequence)
    {
        var id = Guid.NewGuid();
        lock (gate)
        {
            // The old tombstone is no longer needed: a write that lands on our own dictation
            // takes it over through Inherit, and every other write lands on someone else's
            // contents, where the past ownership counter means nothing. The dictionary does not
            // grow without bound.
            expiredOwnership.Clear();
            snapshots[id] = (contents, ownedSequence);
        }

        _ = ExpireAfterLifetime(id);
        return id;
    }

    /// <summary>
    /// Remember only the ownership: there is nothing left to restore.
    ///
    /// The fact itself cannot be lost. Losing it would mean restore treating the clipboard as
    /// somebody else's and silently leaving the dictation in it.
    /// </summary>
    public Guid InsertExpired(uint ownedSequence)
    {
        var id = Guid.NewGuid();
        lock (gate)
        {
            expiredOwnership.Clear();
            expiredOwnership[id] = ownedSequence;
        }

        return id;
    }

    /// <summary>
    /// Hand the person's pending contents to the write that is about to replace our own
    /// dictation on the clipboard.
    ///
    /// The transaction is taken away along with them: its own restore would be applied to a
    /// clipboard that is no longer its.
    /// </summary>
    public ClipboardClaim Inherit(uint ownedSequence)
    {
        lock (gate)
        {
            foreach ((Guid id, (ClipboardSnapshot contents, uint owned)) in snapshots.ToArray())
            {
                if (owned != ownedSequence)
                {
                    continue;
                }

                snapshots.Remove(id);
                return new ClipboardClaim.Snapshot(contents, owned);
            }

            foreach ((Guid id, uint owned) in expiredOwnership.ToArray())
            {
                if (owned != ownedSequence)
                {
                    continue;
                }

                expiredOwnership.Remove(id);
                return new ClipboardClaim.Expired(owned);
            }

            return ClipboardClaim.Missing.Instance;
        }
    }

    /// <summary>
    /// Claim a transaction, clearing the sensitive snapshot from memory in the same breath -
    /// even when ownership is already lost and nothing can be restored.
    /// </summary>
    public ClipboardClaim Take(Guid id)
    {
        lock (gate)
        {
            if (snapshots.Remove(id, out (ClipboardSnapshot Contents, uint OwnedSequence) stored))
            {
                return new ClipboardClaim.Snapshot(stored.Contents, stored.OwnedSequence);
            }

            if (expiredOwnership.Remove(id, out uint owned))
            {
                return new ClipboardClaim.Expired(owned);
            }

            return ClipboardClaim.Missing.Instance;
        }
    }

    private async Task ExpireAfterLifetime(Guid id)
    {
        await Task.Delay(lifetime).ConfigureAwait(false);
        lock (gate)
        {
            if (snapshots.Remove(id, out (ClipboardSnapshot Contents, uint OwnedSequence) stored))
            {
                expiredOwnership[id] = stored.OwnedSequence;
            }
        }
    }
}
