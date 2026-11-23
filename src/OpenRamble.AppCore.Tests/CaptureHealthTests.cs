using System.Diagnostics;
using DictationCore.Audio;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// A monitor fails by succeeding, so its health is an inference - and this is the table the
/// inference is made from. Ported from upstream CaptureHealthTests.swift; the probe-tone and
/// "relaunch after granting" wording is macOS-only (no probe on Linux), so the unheard case checks
/// the Linux detail instead.
/// </summary>
public sealed class CaptureHealthTests
{
    private readonly long now = Stopwatch.GetTimestamp();

    private long Ago(double seconds) => now - (long)(seconds * Stopwatch.Frequency);

    private MeetingCapture.ChannelHealth Health(bool buffers = true, double? audibleSecondsAgo = null) =>
        new(buffers, audibleSecondsAgo is not null, buffers ? now : null, audibleSecondsAgo is { } ago ? Ago(ago) : null);

    private CaptureHealth Make(
        double elapsed, MeetingCapture.ChannelHealth health, bool supported = true, bool requested = true, string? failure = null) =>
        CaptureHealth.Make(supported, requested, failure, elapsed, health, now);

    private static void Near<T>(double expected, T actual, Func<T, double> value) where T : class =>
        Assert.Equal(expected, value(actual), 3);

    [Fact]
    public void AnUnsupportedBuildAndAVoiceNoteAreNotFailures()
    {
        Assert.IsType<CaptureHealth.Unsupported>(Make(100, Health(buffers: false), supported: false));
        Assert.IsType<CaptureHealth.NotRequested>(Make(100, Health(buffers: false), requested: false));
        Assert.Null(new CaptureHealth.NotRequested().Title);
        Assert.False(new CaptureHealth.NotRequested().MarksRecordingDegraded);
    }

    [Fact]
    public void TheFirstThreeSecondsMeanNothingYet()
    {
        Assert.IsType<CaptureHealth.Verifying>(Make(1, Health(buffers: false)));
        Assert.IsType<CaptureHealth.Verifying>(Make(2.9, Health(buffers: true)));
        Assert.Null(new CaptureHealth.Verifying().Title);
    }

    [Fact]
    public void NothingAudibleAfterTheGraceIsUnheardWhateverTheBuffersSay()
    {
        // Buffers of silence are what an idle monitor delivers: still unheard, but said as silence
        // rather than as a side that never arrived.
        Assert.Equal(new CaptureHealth.Unheard(3.5, HeardSilence: true), Make(3.5, Health(buffers: true)));
        Assert.Equal(new CaptureHealth.Unheard(3.5), Make(3.5, Health(buffers: false)));
        var silence = new CaptureHealth.Unheard(3.5, HeardSilence: true);
        Assert.Equal("Only silence is arriving from the other side", silence.Title);
        Assert.Equal("Only silence from the other side.", silence.Announcement);
        Assert.True(silence.MarksRecordingDegraded);
        var state = new CaptureHealth.Unheard(3.5);
        Assert.Equal("Only your microphone is being recorded", state.Title);
        Assert.Equal(StatusColorRole.Attention, state.Role);
        Assert.True(state.MarksRecordingDegraded);
        Assert.Equal("The other side is not being captured.", state.Announcement);
        Assert.Contains("PipeWire", state.Detail);
    }

    [Fact]
    public void SoundHeardMeansCapturingAndSilenceHasToAge()
    {
        Near(0.1, Assert.IsType<CaptureHealth.Capturing>(Make(2, Health(audibleSecondsAgo: 0.1))), c => c.SecondsSinceSound);
        CaptureHealth quiet = Make(600, Health(audibleSecondsAgo: 45));
        Near(45, Assert.IsType<CaptureHealth.Capturing>(quiet), c => c.SecondsSinceSound);
        Assert.Null(quiet.Title);
        Assert.False(quiet.MarksRecordingDegraded);
    }

    [Fact]
    public void AMinuteOfSilenceAfterSoundIsWorthSaying()
    {
        CaptureHealth state = Make(600, Health(audibleSecondsAgo: 125));
        Near(125, Assert.IsType<CaptureHealth.WentSilent>(state), s => s.SecondsSinceSound);
        Assert.Equal("The other side went quiet 2 min ago", state.Title);
        Assert.Equal(StatusColorRole.Attention, state.Role);
        Assert.True(state.MarksRecordingDegraded);
    }

    [Fact]
    public void ResumingDoesNotCountTheRequestedPauseAsMissingAudio()
    {
        MeetingCapture.ChannelHealth beforePause = Health(audibleSecondsAgo: 125);
        Assert.Equal(new CaptureHealth.Capturing(2), Make(2, beforePause));
        Assert.Equal(new MicrophoneHealth.Capturing(2), MicrophoneHealth.Make(null, 2, beforePause, now));
        Assert.True(Make(61, beforePause).MarksRecordingDegraded, "continued silence after resume still needs attention");
    }

    [Fact]
    public void AMonitorThatCouldNotStartSaysWhy()
    {
        CaptureHealth state = Make(0, Health(buffers: false), failure: "pw-record is not installed");
        Assert.Equal(new CaptureHealth.Unavailable("pw-record is not installed"), state);
        Assert.Equal("pw-record is not installed", state.Detail);
        Assert.True(state.MarksRecordingDegraded);
    }

    [Fact]
    public void NothingHereIsEverRed()
    {
        CaptureHealth[] states =
        [
            new CaptureHealth.Unsupported(), new CaptureHealth.NotRequested(), new CaptureHealth.Verifying(),
            new CaptureHealth.Capturing(1), new CaptureHealth.Unheard(10), new CaptureHealth.WentSilent(100),
            new CaptureHealth.Unavailable("x"),
        ];
        foreach (CaptureHealth state in states) { Assert.NotEqual(StatusColorRole.Recording, state.Role); }
    }

    [Fact]
    public void ASilentMicrophoneAfterTheGraceIsUnheard()
    {
        var silent = new MeetingCapture.ChannelHealth(true, false, LastBlockAt: now);
        Assert.IsType<MicrophoneHealth.Verifying>(MicrophoneHealth.Make(null, 1, silent, now));
        MicrophoneHealth unheard = MicrophoneHealth.Make(null, 3.5, silent, now);
        Assert.Equal(new MicrophoneHealth.Unheard(3.5, HeardSilence: true), unheard);
        Assert.Equal("Only silence is arriving from your microphone", unheard.Title);
        Assert.True(unheard.MarksRecordingDegraded);
        Assert.Equal("Only silence from your microphone.", unheard.Announcement);
        Assert.Contains("only silence", unheard.Detail);

        MicrophoneHealth nothing = MicrophoneHealth.Make(null, 3.5, MeetingCapture.ChannelHealth.None, now);
        Assert.Equal(new MicrophoneHealth.Unheard(3.5), nothing);
        Assert.Equal("Your microphone isn't being captured", nothing.Title);
        Assert.Equal("Your microphone is not being captured.", nothing.Announcement);
        Assert.Contains("exclusive", nothing.Detail);
    }

    [Fact]
    public void AHeardMicrophoneIsCapturingAndSilenceHasToAge()
    {
        var heard = new MeetingCapture.ChannelHealth(true, true, now, Ago(0.1));
        Near(0.1, Assert.IsType<MicrophoneHealth.Capturing>(MicrophoneHealth.Make(null, 2, heard, now)), c => c.SecondsSinceSound);
        Assert.Null(new MicrophoneHealth.Capturing(0.1).Title);
        Assert.Null(new MicrophoneHealth.Capturing(0.1).Announcement);
        var quiet = new MeetingCapture.ChannelHealth(true, true, now, Ago(45));
        Near(45, Assert.IsType<MicrophoneHealth.Capturing>(MicrophoneHealth.Make(null, 600, quiet, now)), c => c.SecondsSinceSound);
    }

    [Fact]
    public void AMicrophoneStartFailureIsUnavailableImmediately()
    {
        MicrophoneHealth state = MicrophoneHealth.Make(
            "another app has exclusive access to the microphone", 0, MeetingCapture.ChannelHealth.None, now);
        Assert.Equal(new MicrophoneHealth.Unavailable("another app has exclusive access to the microphone"), state);
        Assert.True(state.MarksRecordingDegraded);
        Assert.Equal("another app has exclusive access to the microphone", state.Detail);
    }
}
