using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using OpenRamble.AppCore;

namespace OpenRamble.App.Linux;

/// <summary>
/// Click it, press the keys you want.
/// </summary>
/// <remarks>
/// <para>
/// The port of the macOS <c>ShortcutRecorder</c>. What a key press means - commit, clear,
/// cancel, reject with a reason - is <see cref="ShortcutRecording.Decide"/>'s decision and is
/// tested without a keyboard; this control only listens while it is recording and stops the
/// moment it has an answer. A key catcher that outlives its field would eat the next shortcut
/// someone pressed for a different reason.
/// </para>
/// <para>
/// The key arrives as Avalonia names it and is stored as the kernel names it, through
/// <see cref="EvdevKeyCodes"/>: the monitor that will fire this chord reads <c>/dev/input</c>,
/// so the number kept has to be the one it will see. A key with no kernel number cannot be
/// recorded and says so, rather than being stored as a chord that never fires.
/// </para>
/// <para>
/// This is the one place in the app that swallows a key - <c>Handled = true</c> while
/// recording - because the whole point is that Ctrl+W records rather than closes the window.
/// </para>
/// </remarks>
public sealed class ShortcutRecorderButton : Button
{
    private KeyCombination? shortcut;
    private bool isRecording;
    private string? problem;

    /// <summary>
    /// Avalonia looks a control's theme up by its own type; a subclass with no theme of its own
    /// gets no template and draws nothing at all. It is a Button, so it wears the Button theme.
    /// </summary>
    protected override Type StyleKeyOverride => typeof(Button);

    public ShortcutRecorderButton()
    {
        MinWidth = 120;
        Padding = new Thickness(12, 4, 12, 4);
        AutomationProperties.SetName(this, "Shortcut");
        Render();
    }

    /// <summary>How keys are named on this platform: <c>Ctrl+Alt+C</c>, <c>Super</c>.</summary>
    public IShortcutKeymap Keymap { get; set; } = LinuxShortcutKeymap.Instance;

    /// <summary>Raised when the person recorded a chord, or cleared it with Delete.</summary>
    public event Action<KeyCombination?>? ShortcutChanged;

    /// <summary>
    /// Why the last press was refused, for the caption under the field. <c>null</c> - nothing
    /// to say. Raised through <see cref="ProblemChanged"/> so the window can draw it.
    /// </summary>
    public string? Problem
    {
        get => problem;
        private set
        {
            if (problem == value) { return; }
            problem = value;
            ProblemChanged?.Invoke(value);
        }
    }

    public event Action<string?>? ProblemChanged;

    /// <summary>The recorded chord, written by the window on every render.</summary>
    public KeyCombination? Shortcut
    {
        get => shortcut;
        set
        {
            if (shortcut == value) { return; }
            shortcut = value;
            Render();
        }
    }

    protected override void OnClick()
    {
        base.OnClick();
        isRecording = !isRecording;
        Problem = null;
        Render();
        if (isRecording) { Focus(); }
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        // The catcher lives exactly as long as the field is the thing being looked at.
        if (!isRecording) { return; }
        isRecording = false;
        Render();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!isRecording)
        {
            base.OnKeyDown(e);
            return;
        }

        // Half-way through a chord: the modifier went down, the key has not come yet.
        e.Handled = true;
        if (EvdevKeyCodes.IsModifier(e.PhysicalKey)) { return; }

        int? code = EvdevKeyCodes.Of(e.PhysicalKey);
        if (code is null)
        {
            Problem = "That key can't be used for a shortcut here.";
            return;
        }

        Handle(ShortcutRecording.Decide(code.Value, EvdevKeyCodes.Modifiers(e.KeyModifiers), Keymap));
    }

    private void Handle(ShortcutRecording.Outcome outcome)
    {
        switch (outcome)
        {
            case ShortcutRecording.Outcome.Commit commit:
                isRecording = false;
                Problem = null;
                Shortcut = commit.Combination;
                ShortcutChanged?.Invoke(commit.Combination);
                break;

            case ShortcutRecording.Outcome.Clear:
                isRecording = false;
                Problem = null;
                Shortcut = null;
                ShortcutChanged?.Invoke(null);
                break;

            case ShortcutRecording.Outcome.Cancel:
                isRecording = false;
                Problem = null;
                break;

            case ShortcutRecording.Outcome.Reject reject:
                Problem = reject.Reason;
                break;
        }

        Render();
    }

    private void Render()
    {
        Content = isRecording ? "Press keys…" : shortcut?.DisplayString(Keymap) ?? "Off";
        Foreground = isRecording
            ? StatusBrushes.Processing
            : shortcut is null ? Brushes.Gray : null;
        AutomationProperties.SetHelpText(
            this,
            isRecording
                ? "Press the keys you want. Escape cancels, Delete removes it."
                : "Activate, then press the keys you want");
    }
}
