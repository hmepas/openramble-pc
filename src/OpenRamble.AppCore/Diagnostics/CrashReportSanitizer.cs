using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OpenRamble.AppCore;

/// <summary>
/// A crash, rebuilt from an allowlist: exception types, numeric codes and the methods on the
/// stack. Exception messages, data, file names and anything a later runtime adds can carry user
/// content - a path, a dictated word - and are never copied.
/// </summary>
/// <remarks>
/// <para>
/// The Mac original sanitizes the system's own <c>.ips</c> reports. Neither Linux nor Windows
/// leaves anything comparable for a managed process, so here the record is made by the app
/// itself, from the unhandled exception, and checked again on export: the file on disk is only
/// ever trusted after it has passed through the same allowlist.
/// </para>
/// <para>
/// A native crash inside the speech engine ends the process without an exception and leaves no
/// record here. No core dump is collected.
/// </para>
/// </remarks>
public static class CrashReportSanitizer
{
    /// <summary>The one application whose records are accepted.</summary>
    public const string AppName = "OpenRamble";

    private const int MaxExceptions = 8;
    private const int MaxFrames = 256;

    private static readonly Regex VersionPattern = new(@"^[0-9A-Za-z.-]{1,32}\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Type and method names are code identifiers, not exception text. Paths, URLs, e-mail
    /// addresses, spaces and control characters cannot match, and are replaced rather than copied.
    /// </summary>
    private static readonly Regex IdentifierPattern = new(@"^[A-Za-z_<][A-Za-z0-9_.+`<>\[\],|$-]{0,1023}\z", RegexOptions.CultureInvariant);

    private const string Redacted = "redacted";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        WriteIndented = true,
    };

    public sealed record CrashFrame
    {
        public int? IlOffset { get; init; }

        public required string Method { get; init; }
    }

    public sealed record CrashException
    {
        public required IReadOnlyList<CrashFrame> Frames { get; init; }

        public required int HResult { get; init; }

        public required string Type { get; init; }
    }

    public sealed record CrashRecord
    {
        public required string App { get; init; }

        public required string Architecture { get; init; }

        public required string Build { get; init; }

        /// <summary>The exception that ended the process first, then what it wrapped.</summary>
        public required IReadOnlyList<CrashException> Exceptions { get; init; }

        public required string Time { get; init; }

        public required string Version { get; init; }
    }

    /// <summary>The record of a live exception, already sanitized.</summary>
    public static byte[] Record(Exception error, string version, string build, DateTimeOffset time)
    {
        var exceptions = new List<CrashException>();
        var pending = new Queue<Exception>([error]);
        while (pending.Count > 0 && exceptions.Count < MaxExceptions)
        {
            Exception current = pending.Dequeue();
            var frames = new List<CrashFrame>();
            foreach (StackFrame frame in new StackTrace(current, fNeedFileInfo: false).GetFrames().Take(MaxFrames))
            {
                System.Reflection.MethodBase? method = frame.GetMethod();
                int offset = frame.GetILOffset();
                frames.Add(new CrashFrame
                {
                    Method = method is null ? "unknown" : $"{method.DeclaringType}.{method.Name}",
                    IlOffset = offset == StackFrame.OFFSET_UNKNOWN ? null : offset,
                });
            }

            exceptions.Add(new CrashException
            {
                Type = current.GetType().ToString(),
                HResult = current.HResult,
                Frames = frames,
            });

            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions) { pending.Enqueue(inner); }
            }
            else if (current.InnerException is { } inner)
            {
                pending.Enqueue(inner);
            }
        }

        return JsonSerializer.SerializeToUtf8Bytes(
            Clean(new CrashRecord
            {
                App = AppName,
                Version = version,
                Build = build,
                Time = time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                Exceptions = exceptions,
            }),
            Options);
    }

    /// <summary>
    /// A record read back from disk, passed through the allowlist again. <c>null</c> when it is
    /// not this app's; throws <see cref="JsonException"/> when it is not a record at all, so the
    /// caller counts it instead of copying it raw.
    /// </summary>
    public static byte[]? Sanitize(byte[] data)
    {
        CrashRecord? record = JsonSerializer.Deserialize<CrashRecord>(data, Options);
        if (record is null || record.App != AppName) { return null; }
        return JsonSerializer.SerializeToUtf8Bytes(Clean(record), Options);
    }

    private static CrashRecord Clean(CrashRecord record) =>
        new()
        {
            App = AppName,
            Version = Matching(record.Version, VersionPattern, "unknown"),
            Build = Matching(record.Build, VersionPattern, "unknown"),
            Time = DateTimeOffset.TryParseExact(
                record.Time,
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out _)
                ? record.Time
                : "unknown",
            Architecture = Enum.TryParse(record.Architecture, ignoreCase: false, out System.Runtime.InteropServices.Architecture architecture)
                && Enum.IsDefined(architecture)
                && architecture.ToString() == record.Architecture
                ? record.Architecture
                : "unknown",
            Exceptions = record.Exceptions.Take(MaxExceptions).Select(exception => new CrashException
            {
                Type = Matching(exception.Type, IdentifierPattern, Redacted),
                HResult = exception.HResult,
                // Positions are preserved: a redacted frame still says where the stack went.
                Frames = exception.Frames.Take(MaxFrames).Select(frame => new CrashFrame
                {
                    Method = Matching(frame.Method, IdentifierPattern, Redacted),
                    IlOffset = frame.IlOffset,
                }).ToList(),
            }).ToList(),
        };

    private static string Matching(string value, Regex pattern, string otherwise) =>
        pattern.IsMatch(value) ? value : otherwise;
}
