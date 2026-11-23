using DictationCore;

namespace OpenRamble.AppCore;

/// <summary>One paste at a time: who is pasting into someone else's window right now.</summary>
/// <remarks>
/// Every paste claims the inserter before it calls it and gives it back when that call truly
/// returns - not when a deadline gave up on it: a take's paste that outlived its deadline is still
/// in someone else's window, and a second paste on top of it would interleave two clipboards.
/// <para>
/// A take's paste is never refused - the kernel has already committed to it, and a take only
/// starts while none of the app's own pastes is in flight - so takes are counted, not queued. The
/// app's own pastes (a menu take, the recovered words, waiting words tapped again) are refused
/// while anything holds the inserter: the menu says so, a tap over waiting words is ignored and
/// the words keep waiting.
/// </para>
/// </remarks>
public sealed partial class AppState
{
    private enum InsertionOwner
    {
        None,

        /// <summary>A take's paste, through <see cref="TakeInserter"/>.</summary>
        Take,

        /// <summary>One of the menu's takes, or the last-take shortcut.</summary>
        MenuTake,

        /// <summary>The menu's recovered words.</summary>
        Recovery,

        /// <summary>A tap over waiting words, or the panel's button.</summary>
        WaitingRetry,
    }

    /// <summary>Under <see cref="gate"/>.</summary>
    private InsertionOwner insertionOwner;

    /// <summary>
    /// Takes' pastes still inside the inserter, under <see cref="gate"/>: one that outlived its
    /// deadline and the next take's can overlap.
    /// </summary>
    private int takeInsertions;

    /// <summary>Under <see cref="gate"/>: one of the app's own pastes takes the inserter, if it is free.</summary>
    private bool TryClaimInsertionLocked(InsertionOwner owner)
    {
        if (insertionOwner != InsertionOwner.None) { return false; }
        insertionOwner = owner;
        return true;
    }

    /// <summary>The app's own paste returned from the inserter.</summary>
    private void ReleaseInsertion(InsertionOwner owner)
    {
        lock (gate)
        {
            if (insertionOwner != owner) { return; }
            insertionOwner = takeInsertions > 0 ? InsertionOwner.Take : InsertionOwner.None;
        }
    }

    private void TakeInsertionStarted()
    {
        lock (gate)
        {
            takeInsertions += 1;
            if (insertionOwner == InsertionOwner.None) { insertionOwner = InsertionOwner.Take; }
        }
    }

    private void TakeInsertionReturned()
    {
        lock (gate)
        {
            takeInsertions -= 1;
            if (takeInsertions == 0 && insertionOwner == InsertionOwner.Take) { insertionOwner = InsertionOwner.None; }
        }
    }

    private void NotifyStillPasting() =>
        Notify(new DictationNotice(DictationNoticeKind.Info, "OpenRamble is still pasting. Try again in a moment."));

    /// <summary>The kernel's inserter: a take's paste holds the inserter until it truly returns.</summary>
    private sealed class TakeInserter(ITextInserting inner, AppState owner) : ITextInserting
    {
        public async Task Insert(string text, TargetApplication? target)
        {
            owner.TakeInsertionStarted();
            try
            {
                await inner.Insert(text, target).ConfigureAwait(false);
            }
            finally
            {
                owner.TakeInsertionReturned();
            }
        }

        public async Task<InsertionMarks> InsertReportingMarks(string text, TargetApplication? target)
        {
            owner.TakeInsertionStarted();
            try
            {
                return await inner.InsertReportingMarks(text, target).ConfigureAwait(false);
            }
            finally
            {
                owner.TakeInsertionReturned();
            }
        }

        public Task PressReturn() => inner.PressReturn();

        public Task PressReturn(TargetApplication? target) => inner.PressReturn(target);

        public TargetApplication? FrontmostApplication() => inner.FrontmostApplication();

        public void EndTheReadWait() => inner.EndTheReadWait();
    }
}
