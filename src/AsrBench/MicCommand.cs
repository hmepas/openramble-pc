#if WINDOWS
using System.Diagnostics;
using System.Globalization;
using DictationCore;
using LocalASR;
using OpenRamble.WindowsAudio;

namespace AsrBench;

/// <summary>
/// Record from the microphone and run the take through the installed model.
///
/// The one command that answers "does this machine hear me" without the application. The app's
/// answer to that question travels through a keyboard hook, a gesture machine, a tray icon and an
/// overlay before it reaches the microphone, and when a dictation produces nothing there is no way
/// to tell which of those is at fault. This is the same capture, the same conversion and the same
/// engine with none of the rest.
/// </summary>
/// <remarks>
/// <para>
/// <b>It has to be the product's capture, not a second one.</b> <c>WasapiCapture</c> lives in a
/// library of its own for exactly this: a bench command with its own recording code would confirm
/// that some path to the microphone works, which is not the question. Every number below - the
/// frame count, the peak, the stop duration - is the number the dictation panel is standing on.
/// </para>
/// <para>
/// <b>It prints the recognized text,</b> which nothing else in this repository does. That is not a
/// break in the privacy rule but the point of the rule: the person typed this command to see what
/// came back. The rule forbids a recording nobody asked for, in a file nobody reads.
/// </para>
/// </remarks>
internal static class MicCommand
{
    private const int DefaultSeconds = 5;

    public static async Task<int> Run(string[] operands)
    {
        int seconds = DefaultSeconds;
        string output = "take.wav";
        bool transcribe = true;

        for (int index = 0; index < operands.Length; index++)
        {
            switch (operands[index])
            {
                case "--seconds" when index + 1 < operands.Length:
                    if (!int.TryParse(operands[index + 1], CultureInfo.InvariantCulture, out seconds)
                        || seconds <= 0 || seconds > 600)
                    {
                        Console.WriteLine($"mic: --seconds expects 1…600, received '{operands[index + 1]}'");
                        return 64;
                    }

                    index += 1;
                    break;

                case "--out" when index + 1 < operands.Length:
                    output = operands[index + 1];
                    index += 1;
                    break;

                case "--no-transcribe":
                    transcribe = false;
                    break;

                default:
                    Console.WriteLine($"mic: unknown option '{operands[index]}'");
                    return 64;
            }
        }

        string takes = Path.Combine(Path.GetTempPath(), "openramble-bench-takes");
        using var capture = new WasapiCapture(takes);

        AudioCaptureError? failure = null;
        capture.Failed = error => failure ??= error;

        float peak = 0;
        capture.OnInputLevel = level => peak = Math.Max(peak, level);

        Console.WriteLine($"Recording {seconds} s from the default communications input…");
        string takePath;
        try
        {
            takePath = await capture.StartRecording().ConfigureAwait(false);
        }
        catch (AudioCaptureError error)
        {
            Console.WriteLine($"The microphone could not be opened: {Describe(error)}");
            return 70;
        }

        Console.WriteLine($"  take: {takePath}");

        // The same wait the session makes before it promises "speak". A device that never
        // delivers a frame is the failure this command exists to name.
        long started = Stopwatch.GetTimestamp();
        Task<bool> firstFrame = capture.WaitForFirstFrame();
        Task arrived = await Task.WhenAny(firstFrame, Task.Delay(TimeSpan.FromSeconds(3)))
            .ConfigureAwait(false);
        Console.WriteLine(ReferenceEquals(arrived, firstFrame) && firstFrame.Result
            ? $"  first frame after {Milliseconds(Stopwatch.GetElapsedTime(started))} ms"
            : "  no frame within 3 s — the device is open but delivering nothing");

        await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);

        long stopBegan = Stopwatch.GetTimestamp();
        (string url, TimeSpan duration) = await capture.StopRecording().ConfigureAwait(false);
        TimeSpan stopTook = Stopwatch.GetElapsedTime(stopBegan);

        (long frames, long samples) = capture.DeliveredCounts;
        TimeSpan? startup = await capture.StartupLatency().ConfigureAwait(false);

        Console.WriteLine("---");
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "recorded: {0:F2} s of audio, {1} frames, {2} samples",
            duration.TotalSeconds,
            frames,
            samples));
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "peak level: {0:F3} ({1})",
            peak,
            peak < 0.001f ? "silent — wrong device, or muted" : "signal present"));
        Console.WriteLine($"microphone startup: {(startup is { } wake ? Milliseconds(wake) : "n/a")} ms");
        Console.WriteLine($"stop took: {Milliseconds(stopTook)} ms");
        if (failure is { } reported)
        {
            Console.WriteLine($"capture reported: {Describe(reported)}");
        }

        string saved = Path.GetFullPath(output);
        File.Move(url, saved, overwrite: true);
        Console.WriteLine($"saved: {saved}");

        if (samples == 0)
        {
            Console.WriteLine();
            Console.WriteLine("Nothing was captured. Recognition is skipped - there is nothing to recognize.");
            return 70;
        }

        if (!transcribe) { return 0; }

        Console.WriteLine();
        LocalTranscriber transcriber = await Program.PrepareTranscriber().ConfigureAwait(false);
        string? language = Program.LanguageHint();

        long recognizeBegan = Stopwatch.GetTimestamp();
        ASRResult result;
        try
        {
            result = await transcriber.Transcribe(saved, language).ConfigureAwait(false);
        }
        catch (ASREngineError error)
        {
            Console.WriteLine($"Recognition error: {error.Message}");
            return 70;
        }

        TimeSpan wall = Stopwatch.GetElapsedTime(recognizeBegan);
        Console.WriteLine("=== recognized ===");
        Console.WriteLine(result.Text.Length == 0 ? "(nothing)" : result.Text);
        Console.WriteLine("---");
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "recognized in {0:F2} s ({1:F1}x real time), {2} words, {3} chars",
            wall.TotalSeconds,
            wall > TimeSpan.Zero ? result.AudioDuration / wall : 0,
            result.Words.Count,
            result.Text.Length));
        return 0;
    }

    private static string Milliseconds(TimeSpan span) =>
        ((long)Math.Round(span.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture);

    private static string Describe(AudioCaptureError error) => error switch
    {
        AudioCaptureError.UnsupportedAudioFormat format => $"unsupported format: {format.Reason}",
        AudioCaptureError.MicrophonePermissionDenied => "microphone access denied",
        AudioCaptureError.EngineUnavailable engine => $"engine unavailable: {engine.Reason}",
        AudioCaptureError.DiskFull => "no free disk space",
        AudioCaptureError.WriteFailed write => $"write failed: {write.Reason}",
        AudioCaptureError.NotRecording => "not recording",
        _ => error.Message,
    };
}
#endif
