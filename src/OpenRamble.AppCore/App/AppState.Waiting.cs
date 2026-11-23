using System.Runtime.InteropServices;
using DictationCore;

namespace OpenRamble.AppCore;

/// <summary>
/// Words that could not land wait where the person works, and the dictation key acts on them
/// (upstream 0.37.0, <c>runner.rs</c> / <c>dictation.rs</c> of the desktop app).
/// </summary>
/// <remarks>
/// Over waiting words a tap does what the panel says - pastes again, or copies while typing is
/// protected - and the microphone does not open for it. A hold is a new take: the waiting words
/// are in History already, and the new take is the news. Which of the two a press is, its release
/// decides, or <see cref="waitingTapLimit"/> does.
///
/// Only with a panel that shows them (<see cref="IWaitingWordsPresenting"/>): without one the key
/// stays a dictation key and a failed paste is a notice, as before.
/// </remarks>
public sealed partial class AppState
{
    /// <summary>Where the waiting words are, and what the dictation key is doing over them.</summary>
    private abstract record Wait
    {
        private Wait()
        {
        }

        /// <summary>Nothing waits: the key dictates.</summary>
        public sealed record Nothing : Wait;

        /// <summary>The words wait and the key is up.</summary>
        public sealed record Words(WaitingWords Value) : Wait;

        /// <summary>
        /// The key went down over the words and has not yet said whether it is a tap or a hold.
        /// <paramref name="Press"/> tells one press from the next, so a timer from an earlier
        /// press cannot decide a later one.
        /// </summary>
        public sealed record KeyUndecided(WaitingWords Value, long Press) : Wait;

        /// <summary>A tap is pasting them again. Presses and Later wait for its answer.</summary>
        public sealed record Retrying(WaitingWords Value) : Wait;

        public static readonly Nothing None = new();
    }

    private static readonly TimeSpan DefaultWaitingTapLimit = TimeSpan.FromMilliseconds(600);

    /// <summary>Under <see cref="gate"/>.</summary>
    private Wait wait = Wait.None;
    private long waitingPresses;

    /// <summary>
    /// Held by whoever decides what a press over waiting words was and acts on it. A hold starts its
    /// take from a timer, so a release can arrive while that take is starting; one at a time keeps
    /// the release from stopping a take that does not exist yet and leaving the one that starts a
    /// moment later running with nothing to stop it.
    /// </summary>
    private readonly object waitingKeyGate = new();

    private IWaitingWordsPresenting? WaitingPanel => overlay as IWaitingWordsPresenting;

    /// <summary>The words that wait, or <c>null</c>.</summary>
    public WaitingWords? WaitingWordsValue
    {
        get
        {
            lock (gate)
            {
                return wait switch
                {
                    Wait.Words w => w.Value,
                    Wait.KeyUndecided k => k.Value,
                    Wait.Retrying r => r.Value,
                    _ => null,
                };
            }
        }
    }

    private void WireWaitingPanel()
    {
        if (WaitingPanel is not { } panel) { return; }
        panel.OnWaitingAction = ActOnWaitingWords;
        panel.OnWaitingLater = () => ReleaseWaitingWords();
        panel.SetWaiting(null, Hotkey);
    }

    private void UnwireWaitingPanel()
    {
        if (WaitingPanel is not { } panel) { return; }
        panel.OnWaitingAction = null;
        panel.OnWaitingLater = null;
    }

    /// <summary>The take's words did not land: they wait, in full, with the reason.</summary>
    /// <returns>Whether they wait; <c>false</c> without a panel to show them.</returns>
    private bool WordsWait(string words, TextInsertionError? failure)
    {
        if (WaitingPanel is not { } panel) { return false; }
        var waiting = new WaitingWords(words, WaitingWords.CauseOf(failure));
        lock (gate) { wait = new Wait.Words(waiting); }
        panel.SetWaiting(waiting, Hotkey);
        return true;
    }

    /// <summary>
    /// History could not keep the take whose words wait: the panel is the only place they are, and
    /// it says so. History is written after the paste failed, so this comes after
    /// <see cref="WordsWait"/>; a new take releases the words first, so any that wait are this take's.
    /// </summary>
    private void WordsKeptNowhereElse()
    {
        WaitingWords marked;
        lock (gate)
        {
            WaitingWords? current = wait switch
            {
                Wait.Words w => w.Value,
                Wait.KeyUndecided k => k.Value,
                Wait.Retrying r => r.Value,
                _ => null,
            };
            if (current is null || current.IsKeptNowhereElse) { return; }
            marked = current with { IsKeptNowhereElse = true };
            wait = wait switch
            {
                Wait.Words => new Wait.Words(marked),
                Wait.KeyUndecided k => new Wait.KeyUndecided(marked, k.Press),
                _ => new Wait.Retrying(marked),
            };
        }

        WaitingPanel?.SetWaiting(marked, Hotkey);
    }

    /// <summary>
    /// The dictation key went down. <c>true</c> when the waiting words took the press, so it must
    /// not start a take.
    /// </summary>
    private bool KeyDownOverWaitingWords()
    {
        lock (waitingKeyGate)
        {
            long press;
            bool releaseAndDictate = false;
            bool tapFirst = false;
            bool retrying = false;
            lock (gate)
            {
                switch (wait)
                {
                    case Wait.Nothing:
                        return false;

                    // The paste is in flight; its answer decides what the next press means. The
                    // person pressing again means nothing appeared: the paste stops waiting to be
                    // read, as a press does over a take's own paste.
                    case Wait.Retrying:
                        retrying = true;
                        press = 0;
                        break;

                    // Copied words have nothing left to press: the key dictates again.
                    case Wait.Words { Value.IsCopied: true }:
                        releaseAndDictate = true;
                        press = 0;
                        break;

                    // A second press while the first is still undecided: the double tap cancelled
                    // the first one's release. The first was a tap - decided here, so the first
                    // press's timer, which may fire before the second release, finds no hold.
                    case Wait.KeyUndecided undecided:
                        tapFirst = true;
                        press = 0;
                        wait = new Wait.Words(undecided.Value);
                        break;

                    case Wait.Words words:
                        waitingPresses += 1;
                        press = waitingPresses;
                        wait = new Wait.KeyUndecided(words.Value, press);
                        break;

                    default:
                        throw new InvalidOperationException("unknown waiting state");
                }
            }

            if (retrying)
            {
                inserter.EndTheReadWait();
                return true;
            }

            // The take is the news: no outcome to cover its waveform.
            if (releaseAndDictate)
            {
                return !ReleaseWaitingWords(showOutcome: false);
            }

            if (tapFirst)
            {
                WaitingPanel?.SetKeyDown(false);
                ActOnWaitingWords();
                return true;
            }

            WaitingPanel?.SetKeyDown(true);
            _ = DecideHoldAfterTapLimit(press);
            return true;
        }
    }

    private async Task DecideHoldAfterTapLimit(long press)
    {
        await Task.Delay(waitingTapLimit).ConfigureAwait(false);
        lock (waitingKeyGate)
        {
            lock (gate)
            {
                // Still down, and still the same press: a hold.
                if (wait is not Wait.KeyUndecided undecided || undecided.Press != press) { return; }
                wait = new Wait.Words(undecided.Value);
            }

            // A new take over words that wait: they leave the panel first.
            if (ReleaseWaitingWords(showOutcome: false)) { BeginHoldToDictateNow(); }
        }
    }

    /// <summary>The dictation key came up. <c>true</c> when it was a tap over waiting words.</summary>
    private bool KeyUpOverWaitingWords()
    {
        lock (waitingKeyGate)
        {
            lock (gate)
            {
                if (wait is not Wait.KeyUndecided undecided) { return false; }
                wait = new Wait.Words(undecided.Value);
            }

            WaitingPanel?.SetKeyDown(false);
            ActOnWaitingWords();
            return true;
        }
    }

    /// <summary>The hold was a shortcut after all: the words keep waiting, the key is up.</summary>
    private void ShortcutOverWaitingWords()
    {
        bool wasDown;
        lock (gate)
        {
            wasDown = wait is Wait.KeyUndecided;
            if (wait is Wait.KeyUndecided undecided) { wait = new Wait.Words(undecided.Value); }
        }

        if (wasDown) { WaitingPanel?.SetKeyDown(false); }
    }

    /// <summary>
    /// A tap of the dictation key over waiting words, or the panel's own button: paste them again
    /// into the app in front now, or copy them while typing is protected. While another paste
    /// holds the inserter the tap is ignored and the words keep waiting.
    /// </summary>
    public void ActOnWaitingWords()
    {
        WaitingWords waiting;
        lock (gate)
        {
            if (wait is not Wait.Words words || words.Value.IsCopied) { return; }
            waiting = words.Value;
            if (waiting.Action == WaitingAction.Paste)
            {
                if (!TryClaimInsertionLocked(InsertionOwner.WaitingRetry)) { return; }
                wait = new Wait.Retrying(waiting);
            }
        }

        if (waiting.Action == WaitingAction.Copy)
        {
            CopyWaitingWords(waiting);
            return;
        }

        _ = Task.Run(() => RetryWaitingPaste(waiting));
    }

    /// <summary>
    /// Copy the waiting words whatever their action - for a panel button that takes the keyboard
    /// when clicked (click-to-focus window managers on Linux), where a retried paste would land in
    /// the panel itself. The dictation key stays the way to paste.
    /// </summary>
    public void CopyWaitingWords()
    {
        WaitingWords waiting;
        lock (gate)
        {
            if (wait is not Wait.Words words || words.Value.IsCopied) { return; }
            waiting = words.Value;
        }

        CopyWaitingWords(waiting);
    }

    private void CopyWaitingWords(WaitingWords waiting)
    {
        try
        {
            clipboard?.Copy(waiting.Words);
        }
        catch (Exception error) when (error is InvalidOperationException or ExternalException)
        {
            DiagnosticLog.EmitException("waiting.copy.failed", error);
            return;
        }

        if (clipboard is null) { return; }
        WaitingWords copied = waiting with { IsCopied = true };
        lock (gate)
        {
            if (wait is not Wait.Words words || words.Value != waiting) { return; }
            wait = new Wait.Words(copied);
        }

        WaitingPanel?.SetWaiting(copied, Hotkey);
    }

    private async Task RetryWaitingPaste(WaitingWords waiting)
    {
        TargetApplication? target = null;
        TextInsertionError? failure = null;
        try
        {
            // Asked here, off the caller's thread: on Linux the answer is a round trip to the
            // compositor. The words were shaped for the app they were dictated into; the one in
            // front now may be a terminal.
            target = inserter.FrontmostApplication();
            string fitted = ParagraphBreaks.Fit(waiting.Words, new PromptModePolicy().Mode(target));
            await inserter.Insert(fitted, target).ConfigureAwait(false);
        }
        catch (TextInsertionError error)
        {
            failure = error;
        }
        finally
        {
            ReleaseInsertion(InsertionOwner.WaitingRetry);
        }

        if (failure is not null)
        {
            journal.Record(DiagnosticEvent.DictationWarning, failureReason: failure.FailureReason);
            var again = new WaitingWords(waiting.Words, WaitingWords.CauseOf(failure), IsKeptNowhereElse: waiting.IsKeptNowhereElse);
            lock (gate) { wait = new Wait.Words(again); }
            WaitingPanel?.SetWaiting(again, Hotkey);
            return;
        }

        lock (gate) { wait = Wait.None; }
        SuccessfulDictationCount += 1;
        RecoveredText = null;
        WaitingPanel?.ShowOutcome(new TakeOutcome.Pasted(target?.LocalizedName));
    }

    /// <summary>The same words landed another way, through the menu: they wait no more.</summary>
    private void WaitingWordsLanded(string words)
    {
        lock (gate)
        {
            if (wait is not Wait.Words w || w.Value.Words != words) { return; }
            wait = Wait.None;
        }

        WaitingPanel?.SetWaiting(null, Hotkey);
    }

    /// <summary>
    /// Later, Escape, or a new take: the words leave the panel and stay in History.
    /// </summary>
    /// <remarks>
    /// The menu's recovered words are the same failed words, and leave with them. Words History
    /// never had keep that slot: it is then the one place left that holds them - a new take's
    /// hold releases them quietly, so the waveform is not covered, but they do not vanish.
    /// </remarks>
    /// <returns><c>false</c> when nothing waited or the words cannot leave yet.</returns>
    private bool ReleaseWaitingWords(bool showOutcome = true)
    {
        WaitingWords released;
        lock (gate)
        {
            switch (wait)
            {
                case Wait.Words w: released = w.Value; break;
                case Wait.KeyUndecided k: released = k.Value; break;
                default: return false;
            }

            wait = Wait.None;
        }

        if (!released.IsKeptNowhereElse && RecoveredText == released.Words) { RecoveredText = null; }

        if (WaitingPanel is { } panel)
        {
            if (showOutcome)
            {
                // History never had the words: the outcome does not claim it does.
                panel.ShowOutcome(new TakeOutcome.Kept(
                    InHistory: !released.IsKeptNowhereElse,
                    LastTakeShortcut?.DisplayString(shortcutKeymap)));
            }
            else
            {
                panel.SetWaiting(null, Hotkey);
            }
        }

        return true;
    }
}
