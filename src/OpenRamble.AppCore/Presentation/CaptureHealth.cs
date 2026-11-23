using System.Diagnostics;
using DictationCore.Audio;

namespace OpenRamble.AppCore;

/// <summary>
/// Whether the other side of the call is actually being recorded (upstream CaptureHealth.swift).
/// </summary>
/// <remarks>
/// A type and not a boolean, because a monitor fails by succeeding: it can start and deliver
/// silence, or nothing, for as long as anyone cares to record. The only evidence is sound
/// arriving, so "are we capturing?" is an inference from a clock and a peak - and it belongs in
/// a table with tests, not in a view. Linux has no permission that silences a monitor, so there is
/// no probe tone: "nothing audible within the grace" is the whole evidence (wave 4b decision).
/// Silence has to age before it means anything: "no sound yet" and "no sound for two minutes" are
/// the same measurement and opposite meanings.
/// </remarks>
public abstract record CaptureHealth
{
    private CaptureHealth() { }

    /// <summary>This build cannot record what the computer plays; the microphone is all there is.</summary>
    public sealed record Unsupported : CaptureHealth;

    /// <summary>A voice note. Not asked for, not a failure.</summary>
    public sealed record NotRequested : CaptureHealth;

    /// <summary>The monitor just started and has not had its three seconds.</summary>
    public sealed record Verifying : CaptureHealth;

    /// <summary>Sound was arriving <paramref name="SecondsSinceSound"/> ago.</summary>
    public sealed record Capturing(double SecondsSinceSound) : CaptureHealth;

    /// <summary>Nothing audible arrived since the start.</summary>
    public sealed record Unheard(double Elapsed) : CaptureHealth;

    /// <summary>Sound arrived, then stopped for longer than a pause.</summary>
    public sealed record WentSilent(double SecondsSinceSound) : CaptureHealth;

    /// <summary>The monitor could not be started at all.</summary>
    public sealed record Unavailable(string Reason) : CaptureHealth;

    public const double ProbeGraceSeconds = 3;
    public const double WorrySeconds = 60;

    /// <param name="elapsed">Seconds since the source started or resumed.</param>
    /// <param name="health">What the recorder has received on the system channel.</param>
    /// <param name="now">A <see cref="Stopwatch.GetTimestamp"/> reading, the clock of <paramref name="health"/>.</param>
    public static CaptureHealth Make(
        bool isSupported,
        bool requested,
        string? startFailure,
        double elapsed,
        MeetingCapture.ChannelHealth health,
        long now)
    {
        if (!isSupported) { return new Unsupported(); }
        if (!requested) { return new NotRequested(); }
        if (startFailure is not null) { return new Unavailable(startFailure); }
        if (health.LastAudibleAt is { } lastAudible)
        {
            // The sources are deliberately stopped during a pause. That wall-clock gap must not
            // become a missing-audio warning on resume.
            double since = Math.Min(elapsed, Seconds(lastAudible, now));
            return since >= WorrySeconds ? new WentSilent(since) : new Capturing(since);
        }

        return elapsed < ProbeGraceSeconds ? new Verifying() : new Unheard(elapsed);
    }

    internal static double Seconds(long from, long to) =>
        (double)(to - from) / Stopwatch.Frequency;

    /// <summary>Never <see cref="StatusColorRole.Recording"/>: red is the microphone.</summary>
    public StatusColorRole? Role => this switch
    {
        Unsupported or NotRequested => null,
        Verifying or Capturing => StatusColorRole.Processing,
        _ => StatusColorRole.Attention,
    };

    /// <summary>Does the finished recording carry this mark forever?</summary>
    public bool MarksRecordingDegraded => this is Unheard or Unavailable or WentSilent;

    /// <summary>Nothing to say, or the one line worth saying.</summary>
    public string? Title => this switch
    {
        Unsupported => "This computer can't record other apps' audio",
        Unheard or Unavailable => "Only your microphone is being recorded",
        WentSilent silent => $"The other side went quiet {RecordingTime.Brief(TimeSpan.FromSeconds(silent.SecondsSinceSound))} ago",
        _ => null,
    };

    public string? Detail => this switch
    {
        Unsupported => "Recording what you hear isn't available on this system yet. Everything else works.",
        Unheard => "Nothing is arriving from what this computer plays. The output may be one PipeWire can't monitor, or nothing has played yet.",
        WentSilent => "Sound was arriving earlier. Your audio output may have changed.",
        Unavailable unavailable => unavailable.Reason,
        _ => null,
    };

    /// <summary>What a screen reader says when this state is first reached, if anything.</summary>
    public string? Announcement => this switch
    {
        Unheard => "The other side is not being captured.",
        WentSilent => "The other side went quiet.",
        Capturing => "The other side is being captured.",
        _ => null,
    };
}

/// <summary>
/// Whether the microphone is actually being recorded: the same inference as
/// <see cref="CaptureHealth"/>, for the other channel. A source can start, deliver zeros, and
/// grow the file with no error.
/// </summary>
public abstract record MicrophoneHealth
{
    private MicrophoneHealth() { }

    /// <summary>Not recording.</summary>
    public sealed record Idle : MicrophoneHealth;

    /// <summary>Just started; silence for a moment is not yet a failure.</summary>
    public sealed record Verifying : MicrophoneHealth;

    public sealed record Capturing(double SecondsSinceSound) : MicrophoneHealth;

    /// <summary>Never a sample above the audible floor.</summary>
    public sealed record Unheard(double Elapsed) : MicrophoneHealth;

    public sealed record WentSilent(double SecondsSinceSound) : MicrophoneHealth;

    /// <summary>The source could not be started at all.</summary>
    public sealed record Unavailable(string Reason) : MicrophoneHealth;

    public const double GraceSeconds = CaptureHealth.ProbeGraceSeconds;
    public const double WorrySeconds = CaptureHealth.WorrySeconds;

    public static MicrophoneHealth Make(
        string? startFailure,
        double elapsed,
        MeetingCapture.ChannelHealth health,
        long now)
    {
        if (startFailure is not null) { return new Unavailable(startFailure); }
        if (health.LastAudibleAt is { } lastAudible)
        {
            double since = Math.Min(elapsed, CaptureHealth.Seconds(lastAudible, now));
            return since >= WorrySeconds ? new WentSilent(since) : new Capturing(since);
        }

        return elapsed < GraceSeconds ? new Verifying() : new Unheard(elapsed);
    }

    public bool MarksRecordingDegraded => this is Unheard or Unavailable or WentSilent;

    public string? Title => this switch
    {
        Unheard or Unavailable => "Your microphone isn't being captured",
        WentSilent silent => $"Your microphone went quiet {RecordingTime.Brief(TimeSpan.FromSeconds(silent.SecondsSinceSound))} ago",
        _ => null,
    };

    public string? Detail => this switch
    {
        Unheard => "Nothing is arriving from your microphone. Another app may have exclusive access, or this input is silent. The other side is still being recorded.",
        WentSilent => "Sound was arriving earlier. The selected input may have changed, or another app may have taken the microphone.",
        Unavailable unavailable => unavailable.Reason,
        _ => null,
    };

    /// <summary>
    /// Capturing is the expected state - not announced. The missing microphone is the thing a
    /// blind person cannot see on a meter.
    /// </summary>
    public string? Announcement => this switch
    {
        Unheard => "Your microphone is not being captured.",
        WentSilent => "Your microphone went quiet.",
        _ => null,
    };
}
