using DictationCore;
using OpenRamble.AppCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The tray icon is the only persistent presence of the application.
///
/// A picture without a description does not exist at all for a blind person: a screen reader
/// must still describe the current state even though the branded picture stays stable.
/// </summary>
/// <remarks>
/// Ported from <c>MenuBarStatusTests</c>. Two groups are not ported: the asset/rendering
/// scenarios (they render an <c>NSImage</c> and assert on its bytes - the Windows equivalent
/// belongs to the tray-icon work, not to policy) and <c>SetupAttentionTests</c>, which switches
/// on <c>LocalASR.ModelState</c>.
/// </remarks>
public sealed class TrayStatusTests
{
    private static readonly DictationState[] AllStates =
    [
        DictationState.Idle,
        DictationState.Preparing,
        DictationState.Listening,
        DictationState.Transcribing,
        DictationState.Inserting,
    ];

    [Fact]
    public void Scenario003()
    {
        foreach (DictationState state in AllStates)
        {
            foreach (bool ready in new[] { true, false })
            {
                string label = TrayStatus.AccessibilityLabel(state, ready);
                Assert.False(string.IsNullOrEmpty(label));
                // There are a lot of icons in the tray: "recording in progress" without an owner
                // says nothing.
                Assert.StartsWith("OpenRamble", label, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Scenario004()
    {
        Assert.Equal(
            "OpenRamble: recording",
            TrayStatus.AccessibilityLabel(DictationState.Listening, isDictationReady: true));
        Assert.Equal(
            "OpenRamble: setup needed",
            TrayStatus.AccessibilityLabel(DictationState.Idle, isDictationReady: false));
        Assert.Equal(
            "OpenRamble: ready to dictate",
            TrayStatus.AccessibilityLabel(DictationState.Idle, isDictationReady: true));
    }

    /// <summary>The idle line teaches the one gesture the app has.</summary>
    [Fact]
    public void Scenario005()
    {
        Assert.Equal(
            "Ready — hold Right Ctrl and speak",
            TrayStatus.StatusLine(DictationState.Idle, true, false, "Right Ctrl"));
        Assert.Equal(
            "Setup needed",
            TrayStatus.StatusLine(DictationState.Idle, false, false, "Right Ctrl"));
        Assert.Equal(
            "Listening",
            TrayStatus.StatusLine(DictationState.Listening, true, false, "Caps Lock"));
        Assert.Equal(
            "Inserting…",
            TrayStatus.StatusLine(DictationState.Inserting, true, false, "Caps Lock"));
        Assert.Equal(
            "Turning on the microphone…",
            TrayStatus.StatusLine(DictationState.Preparing, true, false, "Caps Lock"));
    }

    /// <summary>Hands-free mode in the tray.</summary>
    [Fact]
    public void Scenario011()
    {
        string line = TrayStatus.StatusLine(DictationState.Listening, true, true, "Right Ctrl");

        Assert.Contains("Right Ctrl", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Scenario012()
    {
        Assert.Equal(
            "Listening",
            TrayStatus.StatusLine(DictationState.Listening, true, false, "Right Ctrl"));
    }

    /// <summary>
    /// Unfinished work in the first menu line.
    ///
    /// The dictation panel is a toast that lives four seconds. A person who turned away used to
    /// lose both the explanation and the knowledge that the recognized text was still alive. The
    /// menu holds it for as long as needed.
    /// </summary>
    [Fact]
    public void Scenario013()
    {
        Assert.Equal(
            "Last dictation wasn't inserted",
            TrayStatus.StatusLine(DictationState.Idle, true, false, "Right Ctrl", hasRecoveredText: true));
    }

    [Fact]
    public void Scenario016()
    {
        Assert.Equal(
            "Ready — hold Right Ctrl and speak",
            TrayStatus.StatusLine(DictationState.Idle, true, false, "Right Ctrl"));
    }

    /// <summary>
    /// While the dictation runs, the first line is about it: saved text waits until things are
    /// quiet, but it may not interrupt a live recording.
    /// </summary>
    [Fact]
    public void Scenario017()
    {
        Assert.Equal(
            "Listening",
            TrayStatus.StatusLine(DictationState.Listening, true, false, "Right Ctrl", hasRecoveredText: true));
    }

    [Fact]
    public void Scenario021()
    {
        string label = TrayStatus.AccessibilityLabel(
            DictationState.Idle,
            isDictationReady: true,
            hasRecoveredWork: true);

        Assert.Contains("needs attention", label, StringComparison.Ordinal);
    }

    /// <summary>The dot never lies: no signal until audio is actually captured.</summary>
    [Fact]
    public void Scenario032()
    {
        Assert.Equal(TrayActivity.Hidden, TrayStatus.Activity(DictationState.Idle));
        Assert.Equal(TrayActivity.Hidden, TrayStatus.Activity(DictationState.Preparing));
        Assert.Equal(TrayActivity.Recording, TrayStatus.Activity(DictationState.Listening));
        Assert.Equal(TrayActivity.Working, TrayStatus.Activity(DictationState.Transcribing));
        Assert.Equal(TrayActivity.Working, TrayStatus.Activity(DictationState.Inserting));
    }

    [Fact]
    public void Scenario033()
    {
        Assert.Equal(TrayBadge.Recording, TrayStatus.Badge(TrayActivity.Recording));
        Assert.Equal(TrayBadge.Working, TrayStatus.Badge(TrayActivity.Working));
        Assert.Equal(TrayBadge.Hidden, TrayStatus.Badge(TrayActivity.Hidden));
        Assert.Equal(TrayBadge.Attention, TrayStatus.Badge(TrayActivity.Hidden, needsAttention: true));
        // Attention waits its turn: a live session outranks it, and two signals at once would
        // mean neither is readable.
        Assert.Equal(TrayBadge.Recording, TrayStatus.Badge(TrayActivity.Recording, needsAttention: true));
        Assert.Equal(TrayBadge.Working, TrayStatus.Badge(TrayActivity.Working, needsAttention: true));
    }
}
