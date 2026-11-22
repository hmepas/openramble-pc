using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DictationCore;

namespace OpenRamble.AppCore;

/// <summary>An allowlist, not a free-text logger. No user content can be passed here.</summary>
/// <remarks>
/// The upstream cases for meeting and screen recording, the camera and the setup sheet are not
/// ported: this port has none of those features, so nothing could record them.
/// </remarks>
public enum DiagnosticEvent
{
    AppLaunched,
    AppTerminating,
    AppTerminationReady,
    DictationIdle,
    DictationPreparing,
    DictationListening,
    DictationTranscribing,
    DictationInserting,
    DictationCompleted,
    DictationWarning,
    DictationFailed,
    TranscriptionStalled,
    RecognitionFailed,
    RecognitionModelUnavailable,
    RecognitionAudioInvalid,
    RecognitionEngineFailed,
    RecognitionCancelled,
    DictationStageFailed,
    RecognitionFallback,
    EnginePreparing,
    EngineReady,
    EngineUnavailable,
    EngineFailed,
    AudioConfigurationChanged,
    AudioCaptureFailed,
    SystemSleeping,
    SystemWoke,
}

/// <summary>
/// Local support history: at most seven UTC dates and 5 MB in total, one JSON object per line in
/// <c>events-YYYY-MM-DD.jsonl</c>.
/// </summary>
/// <remarks>
/// <para>
/// On by default; the existing "Keep local diagnostics" switch turns it off, and turning it off
/// clears it. Only <see cref="DiagnosticEvent"/> cases, numbers and allowlisted case names are
/// written - never dictated text, error descriptions, paths or names. That is not a policy this
/// class is free to relax: it is written down in PORTING.md and checked in review.
/// </para>
/// <para>
/// Its own thread keeps disk I/O off the audio and UI threads. Each append opens and closes the
/// file, so completed writes survive a process crash; a crash before a queued append reaches the
/// disk may lose the latest event. Export skips partial lines.
/// </para>
/// <para>
/// Where it lives differs from the Mac: <c>&lt;Support&gt;/SupportLogs</c>, see
/// <see cref="DirectoryUnder"/> and <see cref="AppPaths"/>. The same folder holds the sanitized
/// crash records of <see cref="RecordCrash"/>, which the Mac leaves to the system's own reporter.
/// </para>
/// </remarks>
public sealed class DictationLogFile : IDisposable
{
    public const string FolderName = "SupportLogs";

    private const int MaxCrashRecords = 5;

    private static readonly Regex SafeVersionPattern = new(@"^[0-9A-Za-z.-]{1,32}\z", RegexOptions.CultureInvariant);
    private static readonly Regex EventFilePattern = new(@"^events-\d{4}-\d{2}-\d{2}\.jsonl\z", RegexOptions.CultureInvariant);
    internal static readonly Regex CrashFilePattern = new(@"^crash-\d{8}T\d{9}Z\.json\z", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
            new SecondsTimeConverter(),
        },
    };

    private readonly string directory;
    private readonly int maxBytes;
    private readonly string version;
    private readonly string build;
    private readonly Func<DateTimeOffset> now;
    private readonly BlockingCollection<Action> queue = new();
    private readonly Thread writer;
    private bool enabled;
    private bool writeFailed;
    private bool disposed;

    /// <param name="directory">Where the daily files go. Created on the first write, never before.</param>
    /// <param name="enabled">The person's choice, read once here and then through <see cref="IsEnabled"/>.</param>
    /// <param name="maxBytes">The total budget across every day.</param>
    /// <param name="version">The app version written into every line; the running app's by default.</param>
    /// <param name="build">The build written into every line; the running app's by default.</param>
    /// <param name="now">The clock, injectable so the day and the bounds can be checked.</param>
    public DictationLogFile(
        string directory,
        bool enabled,
        int maxBytes = 5_000_000,
        string? version = null,
        string? build = null,
        Func<DateTimeOffset>? now = null)
    {
        this.directory = directory;
        this.enabled = enabled;
        this.maxBytes = maxBytes;
        this.version = SafeVersion(version ?? ErrorReport.SystemInfo.Current.AppVersion);
        this.build = SafeVersion(build ?? ErrorReport.SystemInfo.Current.AppBuild);
        this.now = now ?? (static () => DateTimeOffset.UtcNow);
        writer = new Thread(Drain)
        {
            IsBackground = true,
            Name = "openramble-support-log",
        };
        writer.Start();
        DateTimeOffset started = this.now();
        Enqueue(() => Maintain(started));
    }

    /// <summary>The folder under the application's support directory.</summary>
    public static string DirectoryUnder(AppPaths paths) => Path.Combine(paths.Support(), FolderName);

    public string Directory => directory;

    /// <summary>A finished take's timings, and nothing of the take itself.</summary>
    public sealed record Recognition
    {
        public int? AudioMilliseconds { get; init; }

        public int? DecodingMilliseconds { get; init; }

        /// <summary>The tail recognized after key-up.</summary>
        public int? EngineMilliseconds { get; init; }

        public required bool FileBacked { get; init; }

        public int? FreezeMilliseconds { get; init; }

        /// <summary>
        /// The capture accepting the start → its first frame. Not key-down → speech: the device's
        /// own warm-up comes after it.
        /// </summary>
        public int? MicrophoneStartupMilliseconds { get; init; }

        /// <summary>Key-up → the paste sent. Sent, not landed: the target may take it later, or never.</summary>
        public int? PasteMilliseconds { get; init; }

        public int? PreparationMilliseconds { get; init; }

        public int? ReadableMilliseconds { get; init; }

        public required int StreamedSegments { get; init; }

        /// <summary>
        /// A finished take's numbers. <c>null</c> for a report built without its breakdown, as the
        /// journal always did.
        /// </summary>
        /// <remarks>
        /// Upstream also records the total engine time, lost audio, the first read of the paste,
        /// disk reads and engine idle time; this port measures none of them yet.
        /// </remarks>
        public static Recognition? From(DictationSpeedReport report)
        {
            if (report.Phases is not { } phases) { return null; }
            return new Recognition
            {
                AudioMilliseconds = Milliseconds(phases.AudioDuration.TotalSeconds),
                EngineMilliseconds = Optional(phases.EngineProcessing),
                DecodingMilliseconds = Optional(phases.AudioDecoding),
                StreamedSegments = phases.StreamedSegments,
                FileBacked = phases.RecordingReadable is not null,
                FreezeMilliseconds = Milliseconds(phases.CaptureFreeze.TotalSeconds),
                ReadableMilliseconds = Optional(phases.RecordingReadable),
                PreparationMilliseconds = Optional(phases.EnginePreparation),
                MicrophoneStartupMilliseconds = Optional(report.MicrophoneStartup),
                PasteMilliseconds = Optional(report.ToPasteDispatched),
            };

            static int? Optional(TimeSpan? span) => span is { } value ? Milliseconds(value.TotalSeconds) : null;
        }
    }

    /// <summary>One line of the journal. Properties are declared in the order they are written.</summary>
    public sealed record Entry
    {
        public required string Build { get; init; }

        public int? ErrorCode { get; init; }

        public required DiagnosticEvent Event { get; init; }

        public DictationFallbackReason? FallbackReason { get; init; }

        public int? Milliseconds { get; init; }

        public Recognition? Recognition { get; init; }

        public DictationFailureStage? Stage { get; init; }

        public required DateTimeOffset Time { get; init; }

        public bool? TimedOut { get; init; }

        public required string Version { get; init; }
    }

    public static int? Milliseconds(double seconds)
    {
        double value = seconds * 1000;
        return value >= 0 && value < int.MaxValue ? (int)value : null;
    }

    /// <summary>
    /// Only the error's case and native numeric status enter the journal. Error descriptions may
    /// contain user paths or text and are never saved.
    /// </summary>
    public void RecordRecognitionFailure(Exception error)
    {
        switch (error)
        {
            case ASREngineError.ModelsNotLoaded or ASREngineError.ModelsUnavailable:
                Record(DiagnosticEvent.RecognitionModelUnavailable);
                break;
            case ASREngineError.UnsupportedAudioFormat:
                Record(DiagnosticEvent.RecognitionAudioInvalid);
                break;
            case ASREngineError.InferenceFailed failed:
                Record(DiagnosticEvent.RecognitionEngineFailed, errorCode: failed.Code);
                break;
            case ASREngineError.Cancelled or OperationCanceledException:
                Record(DiagnosticEvent.RecognitionCancelled);
                break;
            default:
                Record(DiagnosticEvent.RecognitionFailed);
                break;
        }
    }

    public void RecordFallback(DictationFallbackReason reason, Exception? error) =>
        Record(
            DiagnosticEvent.RecognitionFallback,
            errorCode: (error as ASREngineError.InferenceFailed)?.Code,
            fallbackReason: reason);

    /// <summary>Wait until everything queued so far has reached the disk.</summary>
    public void Flush() => Sync(static () => true);

    /// <summary>Turning the existing switch off also clears the local history.</summary>
    public bool IsEnabled
    {
        get => Volatile.Read(ref enabled);
        set => Sync(() =>
        {
            Volatile.Write(ref enabled, value);
            if (!value) { Clear(); }
            return true;
        });
    }

    public void Record(
        DiagnosticEvent @event,
        int? milliseconds = null,
        int? errorCode = null,
        Recognition? recognition = null,
        DictationFailureStage? stage = null,
        bool? timedOut = null,
        DictationFallbackReason? fallbackReason = null,
        DateTimeOffset? at = null)
    {
        DateTimeOffset time = at ?? now();
        Enqueue(() =>
        {
            if (!enabled) { return; }
            try
            {
                CreatePrivateDirectory(directory);
                var entry = new Entry
                {
                    Time = time,
                    Event = @event,
                    Version = version,
                    Build = build,
                    Milliseconds = milliseconds,
                    ErrorCode = errorCode,
                    Recognition = recognition,
                    Stage = stage,
                    TimedOut = timedOut,
                    FallbackReason = fallbackReason,
                };
                byte[] data = [.. JsonSerializer.SerializeToUtf8Bytes(entry, Options), (byte)'\n'];
                var file = new FileInfo(Path.Combine(directory, $"events-{Day(time)}.jsonl"));
                if (!file.Exists)
                {
                    using FileStream created = CreatePrivateFile(file.FullName);
                    created.Write(data);
                }
                else
                {
                    if (file.LinkTarget is not null)
                    {
                        writeFailed = true;
                        return;
                    }

                    using var appended = new FileStream(file.FullName, FileMode.Append, FileAccess.Write, FileShare.Read);
                    appended.Write(data);
                }

                Maintain(time);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                writeFailed = true;
            }
        });
    }

    /// <summary>
    /// A queue barrier for export and normal termination. Invalid or truncated lines are skipped
    /// and unknown JSON fields are never copied out.
    /// </summary>
    public (byte[] Data, bool Enabled, bool Incomplete) Snapshot(DateTimeOffset? at = null)
    {
        DateTimeOffset moment = at ?? now();
        return Sync(() =>
        {
            Maintain(moment);
            using var output = new MemoryStream();
            foreach (FileInfo file in Files(EventFilePattern))
            {
                byte[] data;
                try
                {
                    data = File.ReadAllBytes(file.FullName);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    writeFailed = true;
                    continue;
                }

                foreach (ReadOnlyMemory<byte> line in Lines(data))
                {
                    if (Reencoded(line.Span, moment) is { } safe)
                    {
                        output.Write(safe);
                        output.WriteByte((byte)'\n');
                    }
                    else
                    {
                        writeFailed = true;
                    }
                }
            }

            return (output.ToArray(), enabled, writeFailed);
        });
    }

    /// <summary>
    /// Write a sanitized record of an exception that is about to end the process.
    /// </summary>
    /// <remarks>
    /// Synchronous, on the caller's thread: the process is going down, and a queued write is a
    /// write that never happens. Respects the switch like every other line, and never throws -
    /// the crash being recorded is the only failure that may leave this method.
    /// </remarks>
    public void RecordCrash(Exception error, DateTimeOffset? at = null)
    {
        if (!Volatile.Read(ref enabled)) { return; }
        DateTimeOffset time = at ?? now();
        try
        {
            byte[] record = CrashReportSanitizer.Record(error, version, build, time);
            CreatePrivateDirectory(directory);
            string name = "crash-" + time.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture) + ".json";
            using FileStream file = CreatePrivateFile(Path.Combine(directory, name));
            file.Write(record);
        }
        catch (Exception)
        {
            // A crash handler that throws replaces the crash it was meant to describe.
        }
    }

    public void Dispose()
    {
        lock (queue)
        {
            if (disposed) { return; }
            disposed = true;
        }

        queue.CompleteAdding();
        writer.Join();
        queue.Dispose();
    }

    internal static string SafeVersion(string value) => SafeVersionPattern.IsMatch(value) ? value : "unknown";

    internal static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            System.IO.Directory.CreateDirectory(path);
        }
        else
        {
            System.IO.Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>A new file only the person can read. Fails rather than follow anything already there.</summary>
    internal static FileStream CreatePrivateFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
        return new FileStream(path, options);
    }

    /// <summary>Regular files only - never a symbolic link - whose name matches, oldest name first.</summary>
    internal static List<FileInfo> FilesIn(string folder, Regex pattern)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles()
                .Where(file => file.LinkTarget is null && pattern.IsMatch(file.Name))
                .OrderBy(file => file.Name, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string Day(DateTimeOffset date) =>
        date.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static IEnumerable<ReadOnlyMemory<byte>> Lines(byte[] data)
    {
        int start = 0;
        for (int i = 0; i <= data.Length; i += 1)
        {
            if (i < data.Length && data[i] != (byte)'\n') { continue; }
            if (i > start) { yield return data.AsMemory(start, i - start); }
            start = i + 1;
        }
    }

    /// <summary>The line decoded into the allowlist and written again, or <c>null</c> if it is not one.</summary>
    private static byte[]? Reencoded(ReadOnlySpan<byte> line, DateTimeOffset moment)
    {
        Entry? entry;
        try
        {
            entry = JsonSerializer.Deserialize<Entry>(line, Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (entry is null
            || entry.Time > moment
            || entry.Time <= moment.AddDays(-7)
            || entry.Version != SafeVersion(entry.Version)
            || entry.Build != SafeVersion(entry.Build)
            || !Enum.IsDefined(entry.Event)
            || (entry.Stage is { } stage && !Enum.IsDefined(stage))
            || (entry.FallbackReason is { } reason && !Enum.IsDefined(reason)))
        {
            return null;
        }

        return JsonSerializer.SerializeToUtf8Bytes(entry, Options);
    }

    private List<FileInfo> Files(Regex pattern) => FilesIn(directory, pattern);

    private void Clear()
    {
        foreach (FileInfo file in Files(EventFilePattern).Concat(Files(CrashFilePattern)))
        {
            try
            {
                file.Delete();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                writeFailed = true;
            }
        }
    }

    private void Maintain(DateTimeOffset at)
    {
        if (!enabled)
        {
            Clear();
            return;
        }

        string oldest = $"events-{Day(at.AddDays(-6))}.jsonl";
        try
        {
            foreach (FileInfo file in Files(EventFilePattern))
            {
                if (string.CompareOrdinal(file.Name, oldest) < 0) { file.Delete(); }
            }

            // Keep complete newest lines, even if a single day exceeds the limit.
            long remaining = maxBytes;
            foreach (FileInfo file in Enumerable.Reverse(Files(EventFilePattern)))
            {
                long size = file.Length;
                if (size <= remaining)
                {
                    remaining -= size;
                    continue;
                }

                if (remaining <= 0)
                {
                    file.Delete();
                    continue;
                }

                byte[] tail;
                using (var reading = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    reading.Seek(size - remaining, SeekOrigin.Begin);
                    tail = new byte[remaining];
                    reading.ReadExactly(tail);
                }

                int newline = Array.IndexOf(tail, (byte)'\n');
                byte[] kept = newline < 0 ? [] : tail[(newline + 1)..];
                string replacement = file.FullName + ".tmp";
                File.Delete(replacement);
                using (FileStream written = CreatePrivateFile(replacement))
                {
                    written.Write(kept);
                }

                File.Move(replacement, file.FullName, overwrite: true);
                remaining = 0;
            }

            // Crash records follow the same seven days, and only the newest few are worth keeping.
            List<FileInfo> crashes = Files(CrashFilePattern);
            for (int i = 0; i < crashes.Count; i += 1)
            {
                if (i < crashes.Count - MaxCrashRecords || crashes[i].LastWriteTimeUtc < at.UtcDateTime.AddDays(-7))
                {
                    crashes[i].Delete();
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            writeFailed = true;
        }
    }

    private T Sync<T>(Func<T> work)
    {
        T result = default!;
        using var done = new ManualResetEventSlim(false);
        if (!Enqueue(() =>
        {
            try
            {
                result = work();
            }
            finally
            {
                done.Set();
            }
        }))
        {
            // The writer has stopped; nothing else can touch the files now.
            lock (queue) { return work(); }
        }

        done.Wait();
        return result;
    }

    private bool Enqueue(Action work)
    {
        lock (queue)
        {
            if (disposed) { return false; }
            queue.Add(work);
            return true;
        }
    }

    private void Drain()
    {
        foreach (Action work in queue.GetConsumingEnumerable())
        {
            work();
        }
    }

    /// <summary>ISO 8601 in UTC to the second, the journal's only date format.</summary>
    private sealed class SecondsTimeConverter : JsonConverter<DateTimeOffset>
    {
        private const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";

        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String
                && DateTimeOffset.TryParseExact(
                    reader.GetString(),
                    Format,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset value)
                ? value
                : throw new JsonException("not a journal time");

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
    }
}
