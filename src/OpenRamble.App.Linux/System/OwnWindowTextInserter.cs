using Avalonia.Controls;
using Avalonia.Threading;
using DictationCore;
using OpenRamble.AppCore;

namespace OpenRamble.App.Linux;

/// <summary>
/// A window of this application that dictation can land in without going through the desktop.
/// </summary>
/// <remarks>
/// <para>
/// Every window this shell opens is drawn through XWayland, and the insertion path for other
/// people's windows is <c>wtype</c>: a virtual keyboard whose keymap names one keycode per
/// character. XWayland does not read a client's own keymap - it interprets the keycodes under the
/// seat's real layout - so text typed into one of our own windows arrives as whatever those
/// keycodes mean on the person's layout, which is digits and symbols. Not garbled text: a
/// different text.
/// </para>
/// <para>
/// Our windows are ours, so they do not need a keyboard at all: the text goes straight into the
/// field. This is what a sink does. <see cref="OwnWindowTextInserter"/> asks the active window
/// first and hands everything else to the real inserter.
/// </para>
/// </remarks>
public interface IDictationSink
{
    /// <summary>Put the text at the caret of the field dictation goes into. <c>false</c> - no field is open for it.</summary>
    bool TryInsert(string text);
}

/// <summary>What a sink does with a <see cref="TextBox"/>: the text goes at the caret, the caret moves past it.</summary>
public static class DictationSinkTextBox
{
    public static void Insert(TextBox box, string text)
    {
        string current = box.Text ?? string.Empty;
        int caret = Math.Clamp(box.CaretIndex, 0, current.Length);
        box.Text = current.Insert(caret, text);
        box.CaretIndex = caret + text.Length;
    }
}

/// <summary>
/// The inserter for a machine where some of the windows are this application's own.
/// </summary>
/// <remarks>
/// Decides at insertion time, not at key-down: on Wayland nothing says which window has focus
/// (see <c>LinuxTextInserter.FrontmostApplication</c>), but a window of ours knows whether it is
/// active, and that is the one fact this type needs. The on-screen "Hold to dictate" button and a
/// hotkey pressed with the setup window in front both end up here; a hotkey pressed over a
/// browser goes to the inner inserter exactly as before.
/// </remarks>
public sealed class OwnWindowTextInserter : ITextInserting
{
    private readonly ITextInserting inner;
    private readonly Func<IDictationSink?> activeSink;
    private readonly Func<Func<bool>, Task<bool>> onUiThread;

    /// <param name="inner">The inserter for every other window.</param>
    /// <param name="activeSink">The active window of ours, or <c>null</c>. Called on the UI thread.</param>
    /// <param name="onUiThread">Where sinks are asked; the dispatcher in production, a plain call in tests.</param>
    public OwnWindowTextInserter(
        ITextInserting inner,
        Func<IDictationSink?> activeSink,
        Func<Func<bool>, Task<bool>>? onUiThread = null)
    {
        this.inner = inner;
        this.activeSink = activeSink;
        this.onUiThread = onUiThread ?? (static async work => await Dispatcher.UIThread.InvokeAsync(work));
    }

    public TargetApplication? FrontmostApplication() => inner.FrontmostApplication();

    public Task Insert(string text, TargetApplication? target) => InsertReportingMarks(text, target);

    public async Task<InsertionMarks> InsertReportingMarks(string text, TargetApplication? target)
    {
        if (await TryOwnWindow(text).ConfigureAwait(false))
        {
            // Length, never content, like every other insertion line.
            DiagnosticLog.Emit("insert.own-window", ("chars", text.Length));
            return new InsertionMarks(MonotonicInstant.Now, null);
        }

        return await inner.InsertReportingMarks(text, target).ConfigureAwait(false);
    }

    public async Task PressReturn()
    {
        // A "send" into one of our own fields is a line break; there is nothing to send.
        if (await TryOwnWindow("\n").ConfigureAwait(false)) { return; }

        await inner.PressReturn().ConfigureAwait(false);
    }

    public Task PressReturn(TargetApplication? target) => PressReturn();

    private Task<bool> TryOwnWindow(string text) =>
        onUiThread(() => activeSink()?.TryInsert(text) ?? false);
}
