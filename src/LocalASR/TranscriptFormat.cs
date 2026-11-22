using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DictationCore;

namespace LocalASR;

/// <summary>The four ways a file's transcript is written. The lower-case name is the file extension.</summary>
public enum TranscriptFormat
{
    Txt,
    Json,
    Srt,
    Vtt,
}

/// <summary>The Swift enum's methods, kept as extension members so the call sites read as they did.</summary>
public static class TranscriptFormats
{
    /// <summary>Swift's <c>TranscriptFormat(rawValue:)</c>: <c>txt</c>, <c>json</c>, <c>srt</c> or <c>vtt</c>, exactly.</summary>
    public static TranscriptFormat? Parse(string name) => name switch
    {
        "txt" => TranscriptFormat.Txt,
        "json" => TranscriptFormat.Json,
        "srt" => TranscriptFormat.Srt,
        "vtt" => TranscriptFormat.Vtt,
        _ => null,
    };

    extension(TranscriptFormat format)
    {
        /// <summary>Swift's <c>rawValue</c>: the name on the command line and the file extension.</summary>
        public string Name => format.ToString().ToLowerInvariant();

        /// <summary>The transcript as the bytes of a file in this format, UTF-8.</summary>
        /// <remarks>
        /// JSON is what Swift's <c>JSONSerialization</c> writes with sorted keys and unescaped
        /// slashes, as a value: the same keys, numbers and strings, pretty-printed with two
        /// spaces. The byte layout of the whitespace is System.Text.Json's (<c>"key": value</c>
        /// rather than Foundation's <c>"key" : value</c>), which no JSON reader sees.
        /// </remarks>
        public byte[] Render(FileTranscript transcript) => format switch
        {
            TranscriptFormat.Txt => Encoding.UTF8.GetBytes(transcript.Text + "\n"),
            TranscriptFormat.Json => RenderJson(transcript),
            _ => Encoding.UTF8.GetBytes(RenderSubtitles(format, transcript)),
        };

        /// <summary>
        /// Publish only a completed transcript. The move refuses an existing destination,
        /// including one created after the command line's preflight; the temporary file never
        /// survives, and what is written is readable by its owner alone.
        /// </summary>
        /// <remarks>
        /// Swift renames with <c>RENAME_EXCL</c>. <see cref="File.Move(string, string, bool)"/>
        /// without overwrite is the same promise: a hard link plus unlink on Linux, which fails if
        /// the name exists, and <c>MoveFileEx</c> without <c>REPLACE_EXISTING</c> on Windows.
        /// </remarks>
        public void Write(FileTranscript transcript, string destination)
        {
            byte[] data = format.Render(transcript);
            string folder = Path.GetDirectoryName(Path.GetFullPath(destination))!;
            string temporary = Path.Combine(folder, $".openramble-{Guid.NewGuid():D}.tmp");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            try
            {
                using (var handle = new FileStream(temporary, options))
                {
                    handle.Write(data);
                }

                File.Move(temporary, destination, overwrite: false);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }

    private static readonly JsonWriterOptions JsonOptions = new()
    {
        Indented = true,
        // Foundation leaves non-ASCII text and slashes as they are.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static byte[] RenderJson(FileTranscript transcript)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, JsonOptions))
        {
            // Keys in sorted order, as `.sortedKeys` writes them.
            writer.WriteStartObject();
            writer.WriteNumber("duration", transcript.Duration.TotalSeconds);
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("text", transcript.Text);
            writer.WriteStartArray("words");
            foreach (ASRResult.Word word in transcript.Words)
            {
                writer.WriteStartObject();
                if (word.Confidence is { } confidence) { writer.WriteNumber("confidence", confidence); }
                writer.WriteNumber("end", word.End.TotalSeconds);
                writer.WriteNumber("start", word.Start.TotalSeconds);
                writer.WriteString("text", word.Text);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    /// <remarks>Lengths count grapheme clusters, as Swift's <c>String.count</c> does.</remarks>
    private static string RenderSubtitles(TranscriptFormat format, FileTranscript transcript)
    {
        var groups = new List<List<ASRResult.Word>>();
        var current = new List<ASRResult.Word>();
        int characters = 0;
        foreach (ASRResult.Word word in transcript.Words)
        {
            int length = Graphemes(word.Text);
            if (current.Count > 0
                && (characters + length + 1 > 84
                    || (word.End - current[0].Start).TotalSeconds > 6
                    || (word.Start - current[^1].End).TotalSeconds > 0.8))
            {
                groups.Add(current);
                current = [];
                characters = 0;
            }

            current.Add(word);
            characters += length + 1;
        }

        if (current.Count > 0) { groups.Add(current); }

        double duration = transcript.Duration.TotalSeconds;
        var output = new StringBuilder(format == TranscriptFormat.Vtt ? "WEBVTT\n\n" : "");
        for (int index = 0; index < groups.Count; index++)
        {
            List<ASRResult.Word> group = groups[index];
            if (duration < 0.001) { continue; }

            ASRResult.Word first = group[0];
            ASRResult.Word last = group[^1];
            double start = Math.Min(first.Start.TotalSeconds, Math.Max(0, duration - 0.001));
            double next = index + 1 < groups.Count ? groups[index + 1][0].Start.TotalSeconds : duration;
            double end = Math.Min(
                duration,
                Math.Max(start + 0.001, Math.Min(next, Math.Max(last.End.TotalSeconds, last.Start.TotalSeconds + 0.08))));
            if (format == TranscriptFormat.Srt) { output.Append(CultureInfo.InvariantCulture, $"{index + 1}\n"); }
            output.Append(Stamp(format, start)).Append(" --> ").Append(Stamp(format, end)).Append('\n');

            var lines = new List<string>();
            var line = new StringBuilder();
            int lineLength = 0;
            foreach (ASRResult.Word word in group)
            {
                int length = Graphemes(word.Text);
                if (lineLength > 0 && lineLength + length + 1 > 42)
                {
                    lines.Add(line.ToString());
                    line.Clear();
                    lineLength = 0;
                }

                if (lineLength > 0)
                {
                    line.Append(' ');
                    lineLength++;
                }

                line.Append(word.Text);
                lineLength += length;
            }

            if (lineLength > 0) { lines.Add(line.ToString()); }
            output.Append(string.Join('\n', lines)).Append("\n\n");
        }

        return output.ToString();
    }

    private static string Stamp(TranscriptFormat format, double seconds)
    {
        long milliseconds = Math.Max(0, (long)Math.Floor(seconds * 1000));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{milliseconds / 3_600_000:00}:{milliseconds / 60_000 % 60:00}:{milliseconds / 1000 % 60:00}{(format == TranscriptFormat.Srt ? ',' : '.')}{milliseconds % 1000:000}");
    }

    private static int Graphemes(string text) => new StringInfo(text).LengthInTextElements;
}
