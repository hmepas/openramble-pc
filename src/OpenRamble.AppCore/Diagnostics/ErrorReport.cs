using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace OpenRamble.AppCore;

/// <summary>A local, explicitly saved bundle. This type has no networking or UI.</summary>
/// <remarks>
/// One ZIP holding <c>OpenRamble-Error-Report/</c> with <c>system.json</c>, <c>events.jsonl</c>,
/// <c>README.txt</c> and at most five sanitized crash records from the last seven days. The person
/// shares it themselves; nothing is sent.
/// </remarks>
public static class ErrorReport
{
    private const int MaxCrashRecords = 5;
    private const long MaxSourceBytes = 5_000_000;

    /// <param name="AppVersion">The released version, without the commit.</param>
    /// <param name="AppBuild">The commit the build was made from, shortened; <c>unknown</c> when not stamped.</param>
    /// <param name="Os">The operating system's own description of itself: name, kernel or build.</param>
    /// <param name="Architecture">The process architecture.</param>
    public sealed record SystemInfo(string AppBuild, string AppVersion, string Architecture, string Os)
    {
        public static SystemInfo Current { get; } = Make();

        private static SystemInfo Make()
        {
            string informational = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
                ?? "unknown";
            // The informational version carries the commit after a '+'.
            int plus = informational.IndexOf('+', StringComparison.Ordinal);
            string version = plus > 0 ? informational[..plus] : informational;
            string build = plus > 0 ? informational[(plus + 1)..] : "unknown";
            return new SystemInfo(
                AppBuild: build.Length > 12 ? build[..12] : build,
                AppVersion: version,
                Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
                Os: RuntimeInformation.OSDescription);
        }
    }

    /// <summary>Write the report to <paramref name="destination"/>, replacing it only on success.</summary>
    /// <param name="directories">Where crash records may come from: the journal's own folder by default.</param>
    public static void Save(
        string destination,
        DictationLogFile journal,
        IReadOnlyList<string>? directories = null,
        SystemInfo? info = null,
        DateTimeOffset? now = null)
    {
        DateTimeOffset moment = now ?? DateTimeOffset.UtcNow;
        string temporary = Path.Combine(Path.GetTempPath(), "OpenRamble-report-" + Guid.NewGuid().ToString("N"));
        DictationLogFile.CreatePrivateDirectory(temporary);
        try
        {
            string folder = Path.Combine(temporary, "OpenRamble-Error-Report");
            DictationLogFile.CreatePrivateDirectory(folder);
            (byte[] data, bool enabled, bool incomplete) = journal.Snapshot(moment);
            File.WriteAllBytes(Path.Combine(folder, "events.jsonl"), data);
            File.WriteAllBytes(
                Path.Combine(folder, "system.json"),
                JsonSerializer.SerializeToUtf8Bytes(
                    info ?? SystemInfo.Current,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));

            int skipped = 0;
            var candidates = new List<FileInfo>();
            DateTime cutoff = moment.UtcDateTime.AddDays(-7);
            foreach (string directory in directories ?? [journal.Directory])
            {
                if (!Directory.Exists(directory)) { continue; }
                foreach (FileInfo file in DictationLogFile.FilesIn(directory, DictationLogFile.CrashFilePattern))
                {
                    DateTime modified = file.LastWriteTimeUtc;
                    if (modified < cutoff || modified > moment.UtcDateTime) { continue; }
                    if (file.Length > MaxSourceBytes)
                    {
                        skipped += 1;
                        continue;
                    }

                    candidates.Add(file);
                }
            }

            int included = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (FileInfo candidate in candidates.OrderByDescending(file => file.LastWriteTimeUtc))
            {
                if (included >= MaxCrashRecords) { break; }
                byte[]? sanitized;
                try
                {
                    byte[] raw = File.ReadAllBytes(candidate.FullName);
                    sanitized = raw.Length <= MaxSourceBytes ? CrashReportSanitizer.Sanitize(raw) : null;
                }
                catch (Exception)
                {
                    // Unreadable or malformed: counted, never copied raw.
                    sanitized = null;
                }

                if (sanitized is null)
                {
                    skipped += 1;
                    continue;
                }

                if (!seen.Add(Convert.ToBase64String(sanitized))) { continue; }
                included += 1;
                File.WriteAllBytes(Path.Combine(folder, $"crash-{included}.json"), sanitized);
            }

            string readme = string.Join(
                "\n",
                "OpenRamble error report",
                "",
                $"Saved locally at {moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}. Nothing was uploaded.",
                "Share this ZIP with the developer when asking for help.",
                "",
                "system.json: app version/build, operating system, process architecture.",
                "events.jsonl: predefined technical events, timings and numeric error codes.",
                $"Local journal: {(enabled ? "enabled" : "disabled")}, at most 7 days / 5 MB.",
                $"Journal incomplete or unavailable: {(incomplete ? "yes" : "no")}.",
                $"Crash records included: {included} (up to 5 from the last 7 days).",
                $"Unreadable, oversized or unsupported crash records skipped: {skipped}.",
                included == 0
                    ? "No crash record was found. A crash inside the speech engine itself ends the app without leaving one; this does not mean that no crash occurred."
                    : "Crash records have been redacted. Exception types, numeric codes and the methods on the stack are preserved; messages, file names and other free-form text are omitted.",
                "",
                "No audio, transcripts, clipboard, document contents, user paths, account or device identifiers are collected.",
                "No other applications' logs, system-wide log archive or core dump are included.",
                "");
            File.WriteAllText(Path.Combine(folder, "README.txt"), readme, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            string zip = Path.Combine(temporary, "report.zip");
            ZipFile.CreateFromDirectory(folder, zip, CompressionLevel.Optimal, includeBaseDirectory: true);

            // Replace the chosen file only once the whole report exists: an existing file the
            // person picked stays intact if generating or writing the replacement fails. The
            // staged copy is created private and beside the destination, so the move is a rename
            // that keeps that mode; setting it again afterwards would only add a step that throws
            // on file systems without Unix modes (vfat, SMB) and report a saved file as a failure.
            string staged = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(destination)) ?? ".",
                "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream target = DictationLogFile.CreatePrivateFile(staged))
                using (FileStream source = File.OpenRead(zip))
                {
                    source.CopyTo(target);
                }

                File.Move(staged, destination, overwrite: true);
            }
            finally
            {
                File.Delete(staged);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(temporary, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
