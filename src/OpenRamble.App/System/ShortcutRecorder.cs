using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using OpenRamble.AppCore;

namespace OpenRamble.App;

/// <summary>
/// Click it, press the keys you want.
/// </summary>
/// <remarks>
/// <para>
/// A button that turns into a key catcher for exactly as long as it is recording. Local rather
/// than global: recording happens in a window the person is looking at, so the keys are already
/// coming to this app. It is also the one place in this app that swallows a key - the whole
/// point is that Ctrl+W records rather than closes the window.
/// </para>
/// <para>
/// What a press means is <see cref="ShortcutRecording.Decide"/>'s call, checked without a
/// keyboard: Escape leaves the field alone, Delete empties it, a plain key is refused with a
/// reason, and a bare modifier - which is what the dictation key is - is never a shortcut at all,
/// so the dictation key cannot be recorded here by construction. Losing focus cancels, for the
/// same reason the macOS catcher dies with its field: a catcher that outlived it would eat the
/// next shortcut someone pressed for a different reason.
/// </para>
/// </remarks>
public sealed class ShortcutRecorder : Button
{
    private readonly IShortcutKeymap keymap;
    private KeyCombination? shortcut;
    private bool isRecording;

    public ShortcutRecorder(IShortcutKeymap keymap)
    {
        this.keymap = keymap;
        MinWidth = 110;
        Padding = new Thickness(10, 3, 10, 3);
        HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(this, "Shortcut");
        Click += (_, _) => ToggleRecording();
        PreviewKeyDown += OnKeyDown;
        LostKeyboardFocus += (_, _) => { if (isRecording) { Cancel(); } };
        RenderLabel();
    }

    /// <summary>The recorded chord, or nothing. Setting it from outside stops any recording.</summary>
    public KeyCombination? Shortcut
    {
        get => shortcut;
        set
        {
            shortcut = value;
            isRecording = false;
            Problem = null;
            RenderLabel();
        }
    }

    /// <summary>Why the last press was refused, while the recorder keeps listening.</summary>
    public string? Problem { get; private set; }

    /// <summary>The person chose (or cleared) a shortcut.</summary>
    public event Action<KeyCombination?>? ShortcutChanged;

    /// <summary>The label or the problem changed; whoever shows the problem should re-read it.</summary>
    public event Action? Changed;

    private void ToggleRecording()
    {
        isRecording = !isRecording;
        Problem = null;
        if (isRecording) { Focus(); }
        RenderLabel();
    }

    private void Cancel()
    {
        isRecording = false;
        Problem = null;
        RenderLabel();
    }

    private void OnKeyDown(object sender, KeyEventArgs args)
    {
        if (!isRecording) { return; }
        // Every key is ours while recording: none of them may reach the window's own bindings.
        args.Handled = true;

        Key key = args.Key == Key.System ? args.SystemKey : args.Key;
        if (IsModifier(key))
        {
            // A modifier going down is the start of a chord, not a chord. Keep listening.
            return;
        }

        int keyCode = KeyInterop.VirtualKeyFromKey(key);
        switch (ShortcutRecording.Decide(keyCode, Held(Keyboard.Modifiers), keymap))
        {
            case ShortcutRecording.Outcome.Commit commit:
                shortcut = commit.Combination;
                isRecording = false;
                Problem = null;
                ShortcutChanged?.Invoke(shortcut);
                break;
            case ShortcutRecording.Outcome.Clear:
                shortcut = null;
                isRecording = false;
                Problem = null;
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

        RenderLabel();
    }

    private void RenderLabel()
    {
        Content = isRecording ? "Press keys…" : shortcut?.DisplayString(keymap) ?? "Off";
        FontWeight = isRecording ? FontWeights.SemiBold : FontWeights.Normal;
        AutomationProperties.SetHelpText(
            this,
            isRecording
                ? "Press the keys you want. Escape cancels, Delete removes it."
                : "Activate, then press the keys you want");
        Changed?.Invoke();
    }

    private static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin;

    private static HotkeyModifiers Held(ModifierKeys modifiers)
    {
        HotkeyModifiers held = HotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control)) { held |= HotkeyModifiers.Control; }
        if (modifiers.HasFlag(ModifierKeys.Alt)) { held |= HotkeyModifiers.Alt; }
        if (modifiers.HasFlag(ModifierKeys.Shift)) { held |= HotkeyModifiers.Shift; }
        if (modifiers.HasFlag(ModifierKeys.Windows)) { held |= HotkeyModifiers.Windows; }
        return held;
    }
}
