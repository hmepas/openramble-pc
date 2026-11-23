using System.Diagnostics;
using DictationCore;
using DictationCore.Audio;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The microphone can fail the way the monitor does: start succeeds, zeros arrive, the file
/// grows. The strip, the announcement and the filed note have to say so. Ported from upstream
/// AppStateMicrophoneHealthTests.swift.
/// </summary>
[Collection(DiagnosticLogCollection.Name)]
public sealed class AppStateMicrophoneHealthTests : IDisposable
{
    private readonly AppHarness harness = new();

    public void Dispose() => harness.Dispose();

    private static long SecondsAgo(double seconds) =>
        Stopwatch.GetTimestamp() - (long)(seconds * Stopwatch.Frequency);

    [Fact]
    public async Task ASilentMicrophoneIsSaidOutLoudMarkedAndRecovered()
    {
        long now = Stopwatch.GetTimestamp();
        harness.MeetingCapture.MicrophoneHealth = new MeetingCapture.ChannelHealth(true, false, LastBlockAt: now);
        harness.MeetingCapture.SystemHealth = new MeetingCapture.ChannelHealth(true, true, now, now);
        AppState state = harness.MakeState();

        await state.StartRecording();
        Assert.IsType<MicrophoneHealth.Verifying>(state.LiveMicrophoneHealth);
        await TestWait.For("a degraded microphone", () => state.LiveMicrophoneHealth.MarksRecordingDegraded, 6_000);
        Assert.IsType<MicrophoneHealth.Unheard>(state.LiveMicrophoneHealth);
        // Buffers arrived but nothing audible: said as silence, not as a microphone never captured.
        Assert.Contains("Only silence from your microphone.", harness.Announcer.Messages);
        Assert.True(harness.MeetingCapture.RecoverCount >= 1);

        await state.StopRecording();
        MeetingRecordingMetadata filed = Assert.Single(state.Recordings);
        Assert.False(filed.MicrophoneEverDeliveredAudio);
        Assert.True(filed.MicrophoneEverDeliveredBuffers);
        Assert.Equal(
            "The other side of this call was recorded. Only silence was heard from your microphone.",
            RecordingsPlaceholder.DegradedNote(filed));
        Assert.IsType<MicrophoneHealth.Idle>(state.LiveMicrophoneHealth);
    }

    [Fact]
    public async Task PauseDoesNotPollStoppedSourcesOrAnnounceExpectedSilence()
    {
        long now = Stopwatch.GetTimestamp();
        var healthy = new MeetingCapture.ChannelHealth(true, true, now, now);
        harness.MeetingCapture.MicrophoneHealth = healthy;
        harness.MeetingCapture.SystemHealth = healthy;
        AppState state = harness.MakeState();
        await state.StartRecording();
        await TestWait.For(
            "a tick",
            () => state.LiveDuration > TimeSpan.Zero && harness.MeetingCapture.HealthReadCount >= 2);
        await state.PauseRecording();
        int healthReads = harness.MeetingCapture.HealthReadCount;
        IReadOnlyList<string> announcements = harness.Announcer.Messages;
        var stale = new MeetingCapture.ChannelHealth(true, true, SecondsAgo(90), SecondsAgo(90));
        harness.MeetingCapture.MicrophoneHealth = stale;
        harness.MeetingCapture.SystemHealth = stale;
        await Task.Delay(1_200);
        Assert.Equal(healthReads, harness.MeetingCapture.HealthReadCount);
        Assert.False(state.LiveMicrophoneHealth.MarksRecordingDegraded);
        Assert.False(state.LiveCaptureHealth.MarksRecordingDegraded);
        Assert.Equal(announcements, harness.Announcer.Messages);

        long resumedAt = Stopwatch.GetTimestamp();
        var resumed = new MeetingCapture.ChannelHealth(true, true, resumedAt, resumedAt);
        harness.MeetingCapture.MicrophoneHealth = resumed;
        harness.MeetingCapture.SystemHealth = resumed;
        harness.MeetingCapture.Frames += 16_000;
        await state.ResumeRecording();
        await TestWait.For(
            "a tick after the resume",
            () => state.LiveDuration == TimeSpan.FromSeconds(3) && harness.MeetingCapture.HealthReadCount >= healthReads + 2);
        await state.StopRecording();
        Assert.Equal(MeetingRecordingState.Idle, state.RecordingState);
    }
}
