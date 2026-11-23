using System.Text;
using System.Text.Json;
using DictationCore;

namespace DictationCore.Tests;

/// <summary>
/// <c>meta.json</c> and <c>transcript.json</c> have to read back what was written, and - the part
/// that has bitten this project before - read back older files after fields are added.
/// </summary>
public sealed class MeetingRecordingCodableTests
{
    private static MeetingRecordingMetadata Sample() => MeetingRecordingMetadata.Create(
        id: Guid.Parse("6F9619FF-8B86-D011-B42D-00C04FC964FF"),
        startedAt: DateTimeOffset.FromUnixTimeSeconds(1_756_900_000),
        duration: 1234.5,
        title: "Weekly sync",
        microphoneDeviceName: "MacBook Pro Microphone",
        systemAudio: new SystemAudioSummary(
            wasRequested: true,
            everDeliveredBuffers: true,
            everDeliveredAudio: true,
            outputTransport: "built-in"),
        pauses: [new MeetingInterval(10, 20)],
        gaps: [new MeetingGap(MeetingChannel.System, 30, 31, MeetingGap.GapReason.SystemAudioRouteChange)],
        endReason: MeetingEndReason.StoppedByUser,
        transcriptionState: MeetingTranscriptionState.Complete,
        captureKind: RecordingCaptureKind.Screen,
        videoFileName: "video.mp4",
        screenOptions: new ScreenRecordingOptions
        {
            DisplayId = 42,
            CameraEnabled = true,
            MicrophoneEnabled = true,
            SystemAudioEnabled = true,
            BubbleScale = 0.24,
            BubblePosition = new NormalizedPoint(0.8, 0.1),
        },
        displayName: "Built-in Retina Display");

    private static T Decode<T>(string json) => MeetingRecordingCoding.Decode<T>(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void MetadataRoundTrips()
    {
        MeetingRecordingMetadata original = Sample();
        byte[] data = MeetingRecordingCoding.Encode(original);
        MeetingRecordingMetadata decoded = MeetingRecordingCoding.Decode<MeetingRecordingMetadata>(data);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void DatesAreWrittenAsIso8601ForAPersonToRead()
    {
        string text = Encoding.UTF8.GetString(MeetingRecordingCoding.Encode(Sample()));
        Assert.Contains("\"startedAt\": \"2025-09-03T11:46:40Z\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMinimalFileFromAnEarlierVersionStillLoads()
    {
        MeetingRecordingMetadata decoded = Decode<MeetingRecordingMetadata>(
            """{"id":"6F9619FF-8B86-D011-B42D-00C04FC964FF","startedAt":"2025-09-03T11:46:40Z"}""");
        Assert.Equal(0, decoded.Duration);
        Assert.Null(decoded.Title);
        Assert.Equal(16_000, decoded.SampleRate);
        Assert.Equal([MeetingChannel.Microphone, MeetingChannel.System], decoded.ChannelLayout);
        Assert.False(decoded.SystemAudio.WasRequested);
        Assert.Null(decoded.MicrophoneEverDeliveredAudio);
        Assert.Equal(RecordingCaptureKind.Audio, decoded.CaptureKind);
        Assert.Null(decoded.VideoFileName);
        Assert.Null(decoded.ScreenOptions);
        Assert.Null(decoded.DisplayName);
        Assert.Empty(decoded.Pauses);
        Assert.Empty(decoded.Gaps);
        Assert.Null(decoded.EndReason);
        Assert.Equal(MeetingTranscriptionState.None, decoded.TranscriptionState);
        Assert.False(decoded.IsMeeting);
        Assert.Null(decoded.ImportLanguage);
        // Every recording made before files could be imported was recorded here.
        Assert.False(decoded.IsImported);
    }

    /// <summary>
    /// An imported file says so on disk, and says which model reads it, so an import a quit
    /// interrupted is taken up again the same way.
    /// </summary>
    [Fact]
    public void AnImportedRecordingRemembersItsLanguage()
    {
        MeetingRecordingMetadata imported = Sample();
        imported.ImportLanguage = RecordingImportLanguage.Hebrew;
        MeetingRecordingMetadata decoded =
            MeetingRecordingCoding.Decode<MeetingRecordingMetadata>(MeetingRecordingCoding.Encode(imported));
        Assert.Equal(RecordingImportLanguage.Hebrew, decoded.ImportLanguage);
        Assert.True(decoded.IsImported);

        // A language a later version adds must not cost the recording.
        MeetingRecordingMetadata fromLater = Decode<MeetingRecordingMetadata>(
            """{"id":"6F9619FF-8B86-D011-B42D-00C04FC964FF","startedAt":"2025-09-03T11:46:40Z","importLanguage":"yiddish"}""");
        Assert.Equal(RecordingImportLanguage.Automatic, fromLater.ImportLanguage);
        Assert.True(fromLater.IsImported);
    }

    [Fact]
    public void AnUnknownFieldFromALaterVersionIsIgnored()
    {
        MeetingRecordingMetadata decoded = Decode<MeetingRecordingMetadata>(
            """{"id":"6F9619FF-8B86-D011-B42D-00C04FC964FF","startedAt":"2025-09-03T11:46:40Z","futureField":42}""");
        Assert.Equal(Guid.Parse("6F9619FF-8B86-D011-B42D-00C04FC964FF"), decoded.Id);
    }

    [Fact]
    public void TranscriptRoundTripsIncludingThePerChannelResumptionPoint()
    {
        var original = new MeetingTranscript(
            utterances:
            [
                new MeetingUtterance(MeetingChannel.Microphone, 0.5, 4.2, "Right, can everyone hear me?"),
                new MeetingUtterance(MeetingChannel.System, 4.9, 6.0, string.Empty, isFailed: true),
            ],
            decodedFrames: new Dictionary<MeetingChannel, int>
            {
                [MeetingChannel.Microphone] = 67_200,
                [MeetingChannel.System] = 96_000,
            });
        byte[] data = MeetingRecordingCoding.Encode(original);
        Assert.Equal(original, MeetingRecordingCoding.Decode<MeetingTranscript>(data));
    }

    [Fact]
    public void AnEmptyTranscriptFileLoadsAsEmpty()
    {
        MeetingTranscript decoded = Decode<MeetingTranscript>("{}");
        Assert.Empty(decoded.Utterances);
        Assert.Empty(decoded.DecodedFrames);
    }

    /// <summary>Not upstream: a transcript.json as the macOS app writes it reads back here.</summary>
    [Fact]
    public void AnUpstreamTranscriptFileReadsItsResumptionPoint()
    {
        MeetingTranscript decoded = Decode<MeetingTranscript>(
            """{"schemaVersion":1,"utterances":[],"decodedFrames":["microphone",67200,"system",96000]}""");
        Assert.Equal(67_200, decoded.DecodedFrames[MeetingChannel.Microphone]);
        Assert.Equal(96_000, decoded.DecodedFrames[MeetingChannel.System]);
    }

    /// <summary>Not upstream: pins the on-disk spelling the spec names (channel keys, enum values).</summary>
    [Fact]
    public void ChannelsAndStatesAreSpelledAsUpstreamWritesThem()
    {
        var transcript = new MeetingTranscript(decodedFrames: new Dictionary<MeetingChannel, int> { [MeetingChannel.System] = 1 });
        string text = Encoding.UTF8.GetString(MeetingRecordingCoding.Encode(transcript));
        using (JsonDocument document = JsonDocument.Parse(text))
        {
            // Swift encodes [MeetingChannel: Int] as a flat key/value array.
            Assert.Equal("[\"system\",1]", document.RootElement.GetProperty("decodedFrames").GetRawText().Replace(" ", "").Replace("\n", ""));
        }

        MeetingRecordingMetadata waiting = Sample();
        waiting.TranscriptionState = MeetingTranscriptionState.WaitingForModel;
        string meta = Encoding.UTF8.GetString(MeetingRecordingCoding.Encode(waiting));
        Assert.Contains("\"transcriptionState\": \"waitingForModel\"", meta, StringComparison.Ordinal);
        Assert.Contains("\"displayID\": 42", meta, StringComparison.Ordinal);
        Assert.DoesNotContain("isImported", meta, StringComparison.Ordinal);
    }

    /// <summary>Upstream throws on a missing id; so does this, and the store skips the file.</summary>
    [Fact]
    public void AFileWithoutAnIdIsRejected()
    {
        Assert.ThrowsAny<JsonException>(() => Decode<MeetingRecordingMetadata>("""{"startedAt":"2025-09-03T11:46:40Z"}"""));
    }
}
