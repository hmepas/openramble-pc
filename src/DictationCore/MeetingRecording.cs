using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DictationCore;

/// <summary>
/// The two sides of a conversation, as the two channels of one file.
/// </summary>
/// <remarks>
/// A meeting is recorded from two physically separate streams - the microphone hears the person
/// at the computer, the system output carries everyone else - and that separation is the whole
/// speaker story. Nothing has to guess who spoke: the channel is the label. A voice note is a
/// meeting with one participant, and it uses the same file with the second channel silent, so the
/// format never changes underneath a recording.
/// </remarks>
public enum MeetingChannel
{
    Microphone,
    System,
}

/// <summary>
/// The media captured by a recording. Audio is the historical recording format; screen adds a
/// local video alongside the same WAV.
/// </summary>
public enum RecordingCaptureKind
{
    Audio,
    Screen,
}

/// <summary>
/// A point expressed as a fraction of the captured display (0...1 after clamping by the UI).
/// Independent of pixels, so a camera bubble can be restored on a display of another scale.
/// </summary>
public readonly record struct NormalizedPoint(double X, double Y);

/// <summary>
/// Settings captured with a screen recording. Recording metadata, not a persistent setting.
/// Every field decodes with a default.
/// </summary>
public sealed record ScreenRecordingOptions
{
    [JsonPropertyName("displayID")]
    public uint? DisplayId { get; set; }

    public bool CameraEnabled { get; set; }

    public bool MicrophoneEnabled { get; set; } = true;

    public bool SystemAudioEnabled { get; set; }

    public double BubbleScale { get; set; } = 0.20;

    public NormalizedPoint BubblePosition { get; set; } = new(0.15, 0.82);
}

/// <summary>A span on the recording's own timeline, in seconds.</summary>
public readonly record struct MeetingInterval(double Start, double End);

/// <summary>
/// A stretch where one channel carried no audio and was filled with silence.
/// </summary>
/// <remarks>
/// Recorded rather than hidden: a transcript with a hole in it should be able to say why, and
/// "the other side went quiet at 12:04 because the output device changed" is an answer, where an
/// unexplained gap is a bug report.
/// </remarks>
public readonly record struct MeetingGap(MeetingChannel Channel, double Start, double End, MeetingGap.GapReason Reason)
{
    public enum GapReason
    {
        SystemAudioStalled,
        SystemAudioRouteChange,
        MicrophoneUnavailable,
        ClockUnavailable,
    }
}

/// <summary>How a recording came to an end. <c>null</c> on the metadata means it has not.</summary>
public enum MeetingEndReason
{
    StoppedByUser,
    DiskFull,
    WriteFailed,
    ApplicationQuit,

    /// <summary>The app died mid-recording and the file was repaired on the next launch.</summary>
    CrashRecovered,
}

public enum MeetingTranscriptionState
{
    /// <summary>Never attempted.</summary>
    None,
    Live,
    Complete,
    Partial,
    Failed,
    WaitingForModel,
}

/// <summary>
/// Which speech model reads an imported file. A value a later version adds reads as
/// <see cref="Automatic"/> rather than failing the whole recording.
/// </summary>
public enum RecordingImportLanguage
{
    Automatic,
    Hebrew,
}

/// <summary>
/// What happened on the system-audio side, kept so a one-sided recording can explain itself
/// later. <see cref="OutputTransport"/> (built-in, bluetooth, usb...) is not user content.
/// </summary>
public sealed record SystemAudioSummary
{
    public SystemAudioSummary()
    {
    }

    public SystemAudioSummary(
        bool wasRequested,
        bool everDeliveredBuffers = false,
        bool everDeliveredAudio = false,
        string? outputTransport = null)
    {
        WasRequested = wasRequested;
        EverDeliveredBuffers = everDeliveredBuffers;
        EverDeliveredAudio = everDeliveredAudio;
        OutputTransport = outputTransport;
    }

    public bool WasRequested { get; set; }

    public bool EverDeliveredBuffers { get; set; }

    public bool EverDeliveredAudio { get; set; }

    public string? OutputTransport { get; set; }
}

/// <summary>One recording, as <c>meta.json</c> beside its audio.</summary>
/// <remarks>
/// Every field but <see cref="Id"/> and <see cref="StartedAt"/> decodes with a default, on
/// purpose: a store that refuses its own older files loses exactly the recordings it exists to
/// keep. Unknown fields are ignored, and an explicit <c>null</c> reads as the default too, as
/// Swift's <c>decodeIfPresent</c> does.
/// </remarks>
public sealed record MeetingRecordingMetadata : IJsonOnDeserialized
{
    public const int CurrentSchemaVersion = 2;

    /// <summary>Absent from the file reads as 1, the version before the field existed.</summary>
    public int SchemaVersion { get; set; } = 1;

    [JsonRequired]
    public Guid Id { get; set; }

    [JsonRequired]
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>Recorded seconds - pauses excluded - so it matches the audio file.</summary>
    public double Duration { get; set; }

    /// <summary>
    /// Given by the person. Absent, the date stands in; a title is never invented from the first
    /// words.
    /// </summary>
    public string? Title { get; set; }

    public int SampleRate { get; set; } = 16_000;

    /// <summary>Index equals the channel's index in the WAV.</summary>
    public List<MeetingChannel> ChannelLayout { get; set; } = [MeetingChannel.Microphone, MeetingChannel.System];

    public string? MicrophoneDeviceName { get; set; }

    /// <summary>
    /// <c>null</c> on recordings filed before this was stored: unknown, not missing.
    /// <c>false</c> means the microphone channel was silence for the whole take.
    /// </summary>
    public bool? MicrophoneEverDeliveredAudio { get; set; }

    /// <summary>
    /// <c>null</c> on recordings filed before this was stored. With
    /// <see cref="MicrophoneEverDeliveredAudio"/> <c>false</c>, <c>true</c> here means the
    /// microphone was there and heard only silence, rather than never delivering anything.
    /// </summary>
    public bool? MicrophoneEverDeliveredBuffers { get; set; }

    public SystemAudioSummary SystemAudio { get; set; } = new();

    public List<MeetingInterval> Pauses { get; set; } = [];

    public List<MeetingGap> Gaps { get; set; } = [];

    /// <summary><c>null</c> while the recording is still running.</summary>
    public MeetingEndReason? EndReason { get; set; }

    public MeetingTranscriptionState TranscriptionState { get; set; } = MeetingTranscriptionState.None;

    /// <summary>Absent from old files, which are audio recordings.</summary>
    public RecordingCaptureKind CaptureKind { get; set; } = RecordingCaptureKind.Audio;

    public string? VideoFileName { get; set; }

    public ScreenRecordingOptions? ScreenOptions { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>
    /// Set on a recording that came from a file, and absent on one the app recorded. It also says
    /// which model reads the file, so an import cut short by a quit is taken up again the same way.
    /// </summary>
    public RecordingImportLanguage? ImportLanguage { get; set; }

    /// <summary>Whether this came from a file rather than from the microphone.</summary>
    [JsonIgnore]
    public bool IsImported => ImportLanguage is not null;

    /// <summary>
    /// Whether the other side was ever listened to. A voice note has one channel by choice; a
    /// meeting recorded with a silent tap has two by accident, and the two must never look the same.
    /// </summary>
    [JsonIgnore]
    public bool IsMeeting => SystemAudio.WasRequested;

    /// <summary>A new recording, stamped with the current schema version.</summary>
    public static MeetingRecordingMetadata Create(
        DateTimeOffset startedAt,
        SystemAudioSummary systemAudio,
        Guid? id = null,
        double duration = 0,
        string? title = null,
        int sampleRate = 16_000,
        string? microphoneDeviceName = null,
        bool? microphoneEverDeliveredAudio = null,
        IEnumerable<MeetingInterval>? pauses = null,
        IEnumerable<MeetingGap>? gaps = null,
        MeetingEndReason? endReason = null,
        MeetingTranscriptionState transcriptionState = MeetingTranscriptionState.None,
        RecordingCaptureKind captureKind = RecordingCaptureKind.Audio,
        string? videoFileName = null,
        ScreenRecordingOptions? screenOptions = null,
        string? displayName = null,
        RecordingImportLanguage? importLanguage = null,
        bool? microphoneEverDeliveredBuffers = null) => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        Id = id ?? Guid.NewGuid(),
        StartedAt = startedAt,
        Duration = duration,
        Title = title,
        SampleRate = sampleRate,
        MicrophoneDeviceName = microphoneDeviceName,
        MicrophoneEverDeliveredAudio = microphoneEverDeliveredAudio,
        MicrophoneEverDeliveredBuffers = microphoneEverDeliveredBuffers,
        SystemAudio = systemAudio,
        Pauses = pauses?.ToList() ?? [],
        Gaps = gaps?.ToList() ?? [],
        EndReason = endReason,
        TranscriptionState = transcriptionState,
        CaptureKind = captureKind,
        VideoFileName = videoFileName,
        ScreenOptions = screenOptions,
        DisplayName = displayName,
        ImportLanguage = importLanguage,
    };

    public bool Equals(MeetingRecordingMetadata? other) =>
        other is not null
        && SchemaVersion == other.SchemaVersion
        && Id == other.Id
        && StartedAt == other.StartedAt
        && Duration.Equals(other.Duration)
        && Title == other.Title
        && SampleRate == other.SampleRate
        && ChannelLayout.SequenceEqual(other.ChannelLayout)
        && MicrophoneDeviceName == other.MicrophoneDeviceName
        && MicrophoneEverDeliveredAudio == other.MicrophoneEverDeliveredAudio
        && MicrophoneEverDeliveredBuffers == other.MicrophoneEverDeliveredBuffers
        && Equals(SystemAudio, other.SystemAudio)
        && Pauses.SequenceEqual(other.Pauses)
        && Gaps.SequenceEqual(other.Gaps)
        && EndReason == other.EndReason
        && TranscriptionState == other.TranscriptionState
        && CaptureKind == other.CaptureKind
        && VideoFileName == other.VideoFileName
        && Equals(ScreenOptions, other.ScreenOptions)
        && DisplayName == other.DisplayName
        && ImportLanguage == other.ImportLanguage;

    public override int GetHashCode() => HashCode.Combine(Id, StartedAt, Duration, Title);

    void IJsonOnDeserialized.OnDeserialized()
    {
        ChannelLayout ??= [MeetingChannel.Microphone, MeetingChannel.System];
        SystemAudio ??= new();
        Pauses ??= [];
        Gaps ??= [];
    }
}

/// <summary>
/// A stretch of one channel, by position in the file - never a copy of the audio. The frames are
/// already on disk, and whoever decodes this reads them back when its turn comes.
/// </summary>
public readonly record struct MeetingSegmentRef(MeetingChannel Channel, int StartFrame, int FrameCount)
{
    public int EndFrame => StartFrame + FrameCount;
}

/// <summary>
/// One paragraph of transcript, attributed by channel. Start and end come from where the audio
/// was cut, on the recording's own timeline, so a line can be clicked to play from.
/// </summary>
public sealed record MeetingUtterance : IJsonOnDeserialized
{
    public MeetingUtterance()
    {
    }

    public MeetingUtterance(MeetingChannel channel, double start, double end, string text, bool isFailed = false, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Channel = channel;
        Start = start;
        End = end;
        Text = text;
        IsFailed = isFailed;
    }

    [JsonRequired]
    public Guid Id { get; set; }

    [JsonRequired]
    public MeetingChannel Channel { get; set; }

    [JsonRequired]
    public double Start { get; set; }

    [JsonRequired]
    public double End { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// The decode of this stretch failed. The audio is intact; it can be tried again, and until
    /// then the transcript says so rather than skipping it.
    /// </summary>
    public bool IsFailed { get; set; }

    void IJsonOnDeserialized.OnDeserialized() => Text ??= string.Empty;
}

/// <summary>
/// <c>transcript.json</c>: the utterances so far, and how far each channel has been decoded - the
/// resumption point after a crash or a relaunch.
/// </summary>
public sealed record MeetingTranscript : IJsonOnDeserialized
{
    public const int CurrentSchemaVersion = 1;

    public MeetingTranscript()
    {
    }

    public MeetingTranscript(IEnumerable<MeetingUtterance>? utterances = null, IDictionary<MeetingChannel, int>? decodedFrames = null)
    {
        Utterances = utterances?.ToList() ?? [];
        DecodedFrames = decodedFrames is null ? [] : new Dictionary<MeetingChannel, int>(decodedFrames);
    }

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<MeetingUtterance> Utterances { get; set; } = [];

    /// <summary>
    /// On disk as upstream's Swift <c>Codable</c> writes a dictionary keyed by an enum that is not
    /// <c>CodingKeyRepresentable</c>: a flat array <c>["microphone", 67200, "system", 96000]</c>.
    /// </summary>
    [JsonConverter(typeof(DecodedFramesConverter))]
    public Dictionary<MeetingChannel, int> DecodedFrames { get; set; } = [];

    public bool Equals(MeetingTranscript? other) =>
        other is not null
        && SchemaVersion == other.SchemaVersion
        && Utterances.SequenceEqual(other.Utterances)
        && DecodedFrames.Count == other.DecodedFrames.Count
        && DecodedFrames.All(pair => other.DecodedFrames.TryGetValue(pair.Key, out int value) && value == pair.Value);

    public override int GetHashCode() => HashCode.Combine(SchemaVersion, Utterances.Count);

    void IJsonOnDeserialized.OnDeserialized()
    {
        Utterances ??= [];
        DecodedFrames ??= [];
    }
}

/// <summary>
/// The one way these files are written and read.
/// </summary>
/// <remarks>
/// The date format is pinned on both sides: an encoder configured for one format against a
/// decoder expecting another once read a whole history back as empty, with no error anywhere.
/// ISO-8601 in UTC, whole seconds, because a person opening <c>meta.json</c> can read it.
/// </remarks>
public static class MeetingRecordingCoding
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    /// <exception cref="JsonException">The data is not a readable file of this kind.</exception>
    public static T Decode<T>(ReadOnlySpan<byte> data) =>
        JsonSerializer.Deserialize<T>(data, Options) ?? throw new JsonException("null document");

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        // Ahead of the general enum converter: the first converter that takes a type wins, and a
        // converter in the options outranks one named on the type.
        options.Converters.Add(new RecordingImportLanguageConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new Iso8601DateConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private sealed class Iso8601DateConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? text = reader.GetString();
            if (text is not null
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset value))
            {
                return value;
            }

            throw new JsonException($"not an ISO-8601 date: {text}");
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    }
}

internal sealed class RecordingImportLanguageConverter : JsonConverter<RecordingImportLanguage>
{
    public override RecordingImportLanguage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() == "hebrew" ? RecordingImportLanguage.Hebrew : RecordingImportLanguage.Automatic;

    public override void Write(Utf8JsonWriter writer, RecordingImportLanguage value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value == RecordingImportLanguage.Hebrew ? "hebrew" : "automatic");
}

/// <summary>A per-channel frame count as a flat <c>[key, value, ...]</c> array, as Swift encodes it.</summary>
internal sealed class DecodedFramesConverter : JsonConverter<Dictionary<MeetingChannel, int>>
{
    public override Dictionary<MeetingChannel, int> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) { throw new JsonException("decodedFrames is not an array"); }

        var frames = new Dictionary<MeetingChannel, int>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            MeetingChannel channel = JsonSerializer.Deserialize<MeetingChannel>(ref reader, options);
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number) { throw new JsonException("decodedFrames has a key without a value"); }
            frames[channel] = reader.GetInt32();
        }

        return frames;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<MeetingChannel, int> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach ((MeetingChannel channel, int frames) in value.OrderBy(pair => pair.Key))
        {
            JsonSerializer.Serialize(writer, channel, options);
            writer.WriteNumberValue(frames);
        }

        writer.WriteEndArray();
    }
}
