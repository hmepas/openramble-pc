using System.Diagnostics;
using System.IO;
using DictationCore;
using DictationCore.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace OpenRamble.App;

/// <summary>
/// Record from the default microphone to a 16 kHz mono file.
///
/// This is the driver half of <c>DictationAudio/MicrophoneCapture.swift</c> - the half that
/// needed AVFoundation there and needs WASAPI here. Everything the driver does not need a
/// microphone for was already ported into <c>DictationCore.Audio</c> and is reused rather than
/// rewritten: the frame queue that guarantees ordering, the in-memory recognition cap, and the
/// conversion policy that decides whether a resampler is needed at all.
///
/// <para>
/// The promises this type has to keep come from <see cref="IAudioCapturing"/>, and each one
/// exists because breaking it costs a word:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="StartRecording"/> brings the engine up synchronously. Any delay
/// here cuts off the first word.</description></item>
/// <item><description><see cref="WaitForFirstFrame"/> waits for real audio, not for the engine.
/// Between the two lies about a tenth of a second, and a person who starts speaking at the signal
/// loses their first word in it.</description></item>
/// <item><description>The WAV on disk is the durable copy and is finished before
/// <see cref="StopRecording"/> returns. <see cref="TakeBufferedSamples"/> is an optimisation on
/// top of it, never a replacement: past the cap it returns <c>null</c> and recognition reads the
/// file.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <b>Resampling.</b> <c>WdlResamplingSampleProvider</c>, not <c>MediaFoundationResampler</c>.
/// Media Foundation is a COM subsystem that has to be started per process and per thread, is
/// absent on N/KN editions of Windows until the Media Feature Pack is installed, and answers a
/// missing codec with an <c>HRESULT</c> in the middle of a dictation. The WDL resampler is
/// managed, deterministic, has no install-time dependency, and its quality is far beyond what a
/// 16 kHz speech model can tell apart. A dictation product cannot afford an audio path that works
/// on most Windows installs.
/// </remarks>
public sealed class WasapiCapture : IAudioCapturing, IDisposable
{
    /// <summary>What recognition waits for. Fixed by the model, not by the device.</summary>
    private static readonly AudioStreamFormat RecordingFormat = new(16_000, 1);

    /// <summary>
    /// The in-memory cap, in samples: ten minutes of 16 kHz mono float. Past it the WAV stays
    /// complete and recognition falls back to reading it.
    /// </summary>
    private const int MaximumBufferedSamples = 16_000 * 60 * 10;

    private readonly Lock gate = new();
    private readonly string takesDirectory;

    private NAudio.CoreAudioApi.WasapiCapture? device;
    private WAVWriter? writer;
    private FrameSink? sink;
    private RecognitionSampleBuffer? buffered;
    private BufferedWaveProvider? incoming;
    private ISampleProvider? pipeline;
    private Action<float[]>? deliver;
    private TaskCompletionSource<bool>? firstFrame;
    private long startedAt;
    private TimeSpan? startupLatency;
    private FailureOnce? failure;

    /// <param name="takesDirectory">
    /// Where the take is written. The file lives exactly until the end of recognition - a
    /// person's voice has no business staying on disk.
    /// </param>
    public WasapiCapture(string takesDirectory)
    {
        this.takesDirectory = takesDirectory;
    }

    /// <summary>
    /// Raised for every delivered frame with its peak level in 0…1, for the waveform.
    /// </summary>
    /// <remarks>
    /// A number, never samples. The overlay draws the shape of the signal and must not be handed
    /// anything from which speech could be reconstructed.
    /// </remarks>
    public Action<float>? OnInputLevel { get; set; }

    public Task<string> StartRecording()
    {
        lock (gate)
        {
            if (device is not null)
            {
                throw new AudioCaptureError.EngineUnavailable("already recording");
            }

            MMDevice endpoint;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            }
            catch (Exception error)
            {
                // No microphone, or the audio service is down. Both are "the engine is not
                // available", and neither may be reported as an empty recording.
                throw new AudioCaptureError.EngineUnavailable(error.Message);
            }

            var capture = new NAudio.CoreAudioApi.WasapiCapture(endpoint)
            {
                // Shared mode: exclusive mode would take the microphone away from every other
                // application, and a dictation tool is not entitled to that.
                ShareMode = AudioClientShareMode.Shared,
            };

            WaveFormat deviceFormat = capture.WaveFormat;
            var source = new AudioStreamFormat(deviceFormat.SampleRate, deviceFormat.Channels);

            Directory.CreateDirectory(takesDirectory);
            string path = Path.Combine(takesDirectory, $"take-{Guid.NewGuid():N}.wav");
            var takeWriter = new WAVWriter(path, (int)RecordingFormat.SampleRate, RecordingFormat.Channels);
            takeWriter.Open();

            var queue = new FrameSink();
            var memory = new RecognitionSampleBuffer(MaximumBufferedSamples);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var once = new FailureOnce();

            // The audio thread hands bytes over and returns; one task in a row works the queue
            // through, and that is the ordering guarantee. Writing each frame from the callback
            // would mix the recording with itself, and closing the file without draining would
            // lose the last word - the one the person releases the key on.
            var buffer = new BufferedWaveProvider(deviceFormat)
            {
                DiscardOnBufferOverflow = false,
                BufferDuration = TimeSpan.FromSeconds(5),
            };

            ISampleProvider samples = BuildPipeline(buffer, source);

            Action<float[]> push = queue.Start(async frame =>
            {
                takeWriter.Append(frame);
                memory.Append(frame);
                await Task.CompletedTask.ConfigureAwait(false);
            });

            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;

            device = capture;
            writer = takeWriter;
            sink = queue;
            buffered = memory;
            incoming = buffer;
            pipeline = samples;
            deliver = push;
            firstFrame = completion;
            failure = once;
            startupLatency = null;
            startedAt = Stopwatch.GetTimestamp();

            try
            {
                capture.StartRecording();
            }
            catch (Exception error)
            {
                TearDown();
                takeWriter.Discard();
                throw new AudioCaptureError.EngineUnavailable(error.Message);
            }

            return Task.FromResult(path);
        }
    }

    public Task<bool> WaitForFirstFrame()
    {
        lock (gate)
        {
            return firstFrame?.Task ?? Task.FromResult(false);
        }
    }

    public async Task<(string Url, TimeSpan Duration)> StopRecording()
    {
        NAudio.CoreAudioApi.WasapiCapture? capture;
        FrameSink? queue;
        WAVWriter? takeWriter;
        lock (gate)
        {
            capture = device;
            queue = sink;
            takeWriter = writer;
        }

        if (capture is null || queue is null || takeWriter is null)
        {
            throw new AudioCaptureError.NotRecording();
        }

        capture.StopRecording();
        DrainIntoQueue();
        // Wait for the queue: closing the file ahead of it drops the last word.
        await queue.Finish().ConfigureAwait(false);

        TimeSpan duration = takeWriter.Duration;
        string path = takeWriter.Close();
        TearDown();
        return (path, duration);
    }

    public Task<float[]?> TakeBufferedSamples()
    {
        lock (gate)
        {
            return Task.FromResult(buffered?.Take());
        }
    }

    public Task AbortRecording()
    {
        NAudio.CoreAudioApi.WasapiCapture? capture;
        FrameSink? queue;
        WAVWriter? takeWriter;
        lock (gate)
        {
            capture = device;
            queue = sink;
            takeWriter = writer;
            buffered?.Discard();
        }

        capture?.StopRecording();
        queue?.Cancel();
        takeWriter?.Discard();
        TearDown();
        return Task.CompletedTask;
    }

    public Task<TimeSpan?> StartupLatency()
    {
        lock (gate)
        {
            return Task.FromResult(startupLatency);
        }
    }

    public void Dispose()
    {
        try
        {
            AbortRecording().GetAwaiter().GetResult();
        }
        catch (AudioCaptureError)
        {
            // Nothing was running. Disposing an idle capture is not a failure.
        }
    }

    /// <summary>
    /// Device format in, 16 kHz mono float out.
    /// </summary>
    /// <remarks>
    /// The fork is <see cref="MicrophoneCaptureFormat.Converter"/>'s: a resampler exists only when
    /// one is genuinely needed, and a conversion that cannot be performed throws rather than
    /// dropping frames. A frame quietly dropped here is a word missing from the recording with
    /// nothing to show for it.
    /// </remarks>
    private static ISampleProvider BuildPipeline(BufferedWaveProvider buffer, AudioStreamFormat source)
    {
        ISampleProvider samples = buffer.ToSampleProvider();
        if (source.Channels > 1)
        {
            // Averaged, not picked: taking one channel of a stereo microphone throws away half
            // the voice, and on some laptops that half is the one facing the person.
            samples = new StereoToMonoSampleProvider(samples) { LeftVolume = 0.5f, RightVolume = 0.5f };
        }

        IAudioSampleConverter? converter = MicrophoneCaptureFormat.Converter(
            source,
            RecordingFormat,
            (_, target) => new WdlConverter(samples, (int)target.SampleRate));

        return converter is WdlConverter wdl ? wdl.Provider : samples;
    }

    /// <summary>
    /// Adapts the port's conversion policy to NAudio's provider chain.
    /// </summary>
    /// <remarks>
    /// The port's <see cref="IAudioSampleConverter"/> is frame-in / frame-out because
    /// <c>AVAudioConverter</c> is; NAudio pulls instead of pushing. The policy that matters -
    /// "a conversion that cannot be performed must be visible" - is kept by constructing the
    /// resampler here and letting a failure throw out of the factory.
    /// </remarks>
    private sealed class WdlConverter : IAudioSampleConverter
    {
        public WdlConverter(ISampleProvider source, int targetSampleRate)
        {
            Provider = new WdlResamplingSampleProvider(source, targetSampleRate);
        }

        public ISampleProvider Provider { get; }

        public float[] Convert(float[] samples) => samples;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        BufferedWaveProvider? buffer;
        lock (gate)
        {
            buffer = incoming;
        }

        if (buffer is null || args.BytesRecorded == 0)
        {
            return;
        }

        try
        {
            buffer.AddSamples(args.Buffer, 0, args.BytesRecorded);
        }
        catch (InvalidOperationException)
        {
            // The queue backed up beyond five seconds, which means the drain has stopped. Losing
            // audio silently is not an option; report it once and end the session.
            ReportFailure(new AudioCaptureError.WriteFailed("the capture buffer overflowed"));
            return;
        }

        DrainIntoQueue();
    }

    /// <summary>Pull everything the resampler can produce and hand it to the queue.</summary>
    private void DrainIntoQueue()
    {
        ISampleProvider? provider;
        BufferedWaveProvider? buffer;
        Action<float[]>? push;
        lock (gate)
        {
            provider = pipeline;
            buffer = incoming;
            push = deliver;
        }

        if (provider is null || buffer is null || push is null)
        {
            return;
        }

        // 100 ms at the recording rate: small enough that the first frame arrives promptly, large
        // enough that a dictation is not thousands of allocations.
        const int chunk = 1_600;
        var scratch = new float[chunk];

        while (true)
        {
            int read;
            try
            {
                read = provider.Read(scratch, 0, chunk);
            }
            catch (Exception error)
            {
                ReportFailure(new AudioCaptureError.UnsupportedAudioFormat(error.Message));
                return;
            }

            if (read <= 0)
            {
                return;
            }

            var frame = new float[read];
            Array.Copy(scratch, frame, read);
            MarkFirstFrame();
            OnInputLevel?.Invoke(Peak(frame));
            push(frame);

            if (read < chunk)
            {
                return;
            }
        }
    }

    private void MarkFirstFrame()
    {
        TaskCompletionSource<bool>? completion;
        lock (gate)
        {
            if (startupLatency is null)
            {
                startupLatency = Stopwatch.GetElapsedTime(startedAt);
            }

            completion = firstFrame;
        }

        completion?.TrySetResult(true);
    }

    private static float Peak(float[] frame)
    {
        float peak = 0;
        foreach (float sample in frame)
        {
            float magnitude = Math.Abs(sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        return Math.Clamp(peak, 0f, 1f);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null)
        {
            ReportFailure(new AudioCaptureError.EngineUnavailable(args.Exception.Message));
        }
    }

    /// <summary>
    /// A stream can send several frames before it stops. One cause must not raise a dozen alarms,
    /// and it must not be swallowed either: whoever is waiting for the first frame is told that
    /// there will not be one.
    /// </summary>
    private void ReportFailure(AudioCaptureError error)
    {
        FailureOnce? once;
        TaskCompletionSource<bool>? completion;
        lock (gate)
        {
            once = failure;
            completion = firstFrame;
        }

        once?.Run(() =>
        {
            // False, not an exception: "there is no recording any more" is what the port promises
            // here, and promising "speak" where nothing is being written is the one thing this
            // must never do.
            completion?.TrySetResult(false);
            Failed?.Invoke(error);
        });
    }

    /// <summary>The stream died mid-dictation. Never silent: the session ends and says why.</summary>
    public Action<AudioCaptureError>? Failed { get; set; }

    private void TearDown()
    {
        NAudio.CoreAudioApi.WasapiCapture? capture;
        lock (gate)
        {
            capture = device;
            device = null;
            writer = null;
            sink = null;
            incoming = null;
            pipeline = null;
            deliver = null;
            firstFrame = null;
            failure = null;
        }

        if (capture is null)
        {
            return;
        }

        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnRecordingStopped;
        capture.Dispose();
    }
}
