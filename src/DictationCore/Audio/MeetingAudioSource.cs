namespace DictationCore.Audio;

/// <summary>
/// One block of audio from one side of the conversation.
///
/// Always 16 kHz mono float - the source owns the conversion from whatever the device speaks -
/// with the host clock reading at its first frame, so two sources with two device clocks can be
/// placed on one timeline.
/// </summary>
/// <param name="HostNanoseconds">
/// Monotonic host time of the block's first frame (<see cref="System.Diagnostics.Stopwatch"/>
/// converted to nanoseconds). <c>null</c> when the source had no valid timestamp for this block.
/// </param>
/// <param name="Samples">16 kHz mono samples.</param>
public sealed record MeetingAudioBlock(ulong? HostNanoseconds, float[] Samples)
{
    /// <summary>
    /// The host clock every source stamps its blocks with, in nanoseconds: the monotonic
    /// <see cref="System.Diagnostics.Stopwatch"/> timestamp. The capture anchors its timeline on
    /// the same reading, so a source and the capture always agree on "now".
    /// </summary>
    public static ulong HostNanosecondsNow() =>
        (ulong)((Int128)System.Diagnostics.Stopwatch.GetTimestamp() * 1_000_000_000
            / System.Diagnostics.Stopwatch.Frequency);
}

/// <summary>
/// Receives the same aligned PCM that has just been appended to the meeting WAV.
/// <paramref name="startFrame"/> is the first frame in that file stretch, so another writer can
/// share exactly the recording clock without touching either source's device timestamps.
/// </summary>
public interface IMeetingAudioBlockSink
{
    void Receive(float[] microphone, float[] system, long startFrame);

    void Anchor(ulong hostNanoseconds, long frame);
}

/// <summary>Why a meeting source stopped delivering.</summary>
public abstract record MeetingSourceFailure
{
    private MeetingSourceFailure() { }

    public sealed record Unavailable(string Detail) : MeetingSourceFailure;

    public sealed record StartFailed(string Detail) : MeetingSourceFailure;

    /// <summary>
    /// The device changed under the source - unplugged, or the default moved. The source has
    /// stopped; it can be started again.
    /// </summary>
    public sealed record ConfigurationChanged : MeetingSourceFailure;

    public sealed record ConversionFailed(string Detail) : MeetingSourceFailure;
}

/// <summary>
/// What <see cref="IMeetingAudioSource.Start"/> throws: the Swift original throws the
/// <see cref="MeetingSourceFailure"/> value itself, which C# cannot do with a record.
/// </summary>
public sealed class MeetingSourceException(MeetingSourceFailure failure) : Exception(failure.ToString())
{
    public MeetingSourceFailure Failure { get; } = failure;
}

/// <summary>
/// A microphone, a system-audio monitor, or a test script - anything that can deliver timed
/// 16 kHz mono blocks.
///
/// This is the seam that lets the whole capture layer be tested with no microphone and no
/// system audio, the way <c>IAudioCapturing</c> does it for dictation. Health is not part of the
/// interface on purpose: "is audio arriving?" is an inference from a clock and a peak, and the
/// consumer that has both is the right place to draw it.
/// </summary>
public interface IMeetingAudioSource
{
    /// <summary>A short name for the device, for <c>meta.json</c>. Not user content.</summary>
    string? DeviceName { get; }

    /// <summary>
    /// Begin delivering blocks. Throws <see cref="MeetingSourceException"/> rather than
    /// delivering nothing.
    /// </summary>
    void Start(Action<MeetingAudioBlock> onBlock, Action<MeetingSourceFailure> onFailure);

    /// <summary>Stop delivering. Safe to call twice; safe to <see cref="Start"/> again after.</summary>
    void Stop();

    /// <summary>
    /// Drop a pinned device and any other start-time choice that might have produced silence, so
    /// the next <see cref="Start"/> uses whatever is default now.
    ///
    /// Called when the microphone has been delivering nothing we can hear while the other side
    /// of a call is clearly arriving - the usual cause is a preferred input that is not the one
    /// the meeting app is using.
    /// </summary>
    void PrepareForRecovery() { }
}
