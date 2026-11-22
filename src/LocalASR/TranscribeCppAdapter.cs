using System.Diagnostics;
using System.Runtime.InteropServices;
using DictationCore;

namespace LocalASR;

/// <summary>
/// The only place in the project that talks to the transcribe.cpp runtime.
///
/// Everything else - the dictation controller, the app, the tests - works through
/// <see cref="IASREngineAdapting"/>, exactly as it did for the sherpa-onnx engine this replaces
/// on Linux. Confining the C API to one file is what keeps the next replacement contained too.
/// </summary>
/// <remarks>
/// <para>
/// Ported from upstream's <c>TranscribeCppAdapter.swift</c> and the desktop core's
/// <c>ramble-engine</c>, against the same runtime: upstream's fork of transcribe.cpp at
/// <c>dacdba09</c> (release 0.4.1, the Parakeet long-form fix and phrase boosting), built by
/// <c>scripts/build-transcribe.sh</c>. The model is Parakeet Ultra, one GGUF file.
/// </para>
/// <para>
/// Three differences from the Swift adapter, each on purpose. Word times are always asked for,
/// because this port's dictation path marks paragraph breaks from them (Swift's dictation asks
/// for text only; the runtime builds the words either way and only drops them when no times
/// are requested). There is no native abort callback: <see cref="IASREngineAdapting"/> takes no
/// token, and <see cref="LocalTranscriber"/> already discards the answer of a call it gave up
/// on. And there is no batch entry point, because nothing here would call it.
/// </para>
/// <para>
/// The engine silently drops speech in a single run longer than thirty seconds (Parakeet Ultra
/// segments a long run itself, and on 0.4.1 its speech head can miss the start of a piece).
/// Nothing reaches this adapter that long: <see cref="LocalTranscriber"/> cuts every longer
/// buffer into pause-cut pieces of at most twenty seconds before the engine sees it.
/// </para>
/// </remarks>
public sealed partial class TranscribeCppAdapter : IASREngineAdapting
{
    /// <summary>
    /// The shortest clip the mel front-end accepts without producing NaNs: 1.25 seconds, the
    /// same constant as upstream's <c>MINIMUM_ENGINE_SAMPLES</c>.
    /// </summary>
    public const int MinimumEngineSamples = 16_000 * 5 / 4;

    /// <summary>
    /// Threads inside one recognition: the ggml CPU scheduler that runs the encoder, the mel
    /// front-end and the decoder all take this session count.
    /// </summary>
    /// <remarks>
    /// Half the logical processors - roughly the physical cores - clamped to [1, 8]. No GPU
    /// backend is compiled in, so the encoder runs on the CPU and scales with cores: on a
    /// 16-core desktop a 45 s clip took 1.34 s at four threads and 0.82 s at eight. Halving
    /// leaves the other hyperthreads to the app and to whatever the person is typing into;
    /// eight is the cap because past it the gain is small and the machine is shared. Upstream's
    /// own CPU-only build, the Intel Mac, fixes four; its desktop core passes one on Windows and
    /// Linux, a value chosen for an encoder on Metal or Vulkan that on the CPU serializes the
    /// whole encoder onto one core.
    /// </remarks>
    public static int DefaultThreadCount { get; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    private readonly Lock gate = new();
    private RuntimeLease<Runtime>? runtime;
    private string? loadedDirectory;
    private string[] vocabulary = [];
    private readonly int threadCount;

    public TranscribeCppAdapter(int? threadCount = null)
    {
        this.threadCount = Math.Max(1, threadCount ?? DefaultThreadCount);
    }

    /// <summary>For the test that guards the shipped thread count.</summary>
    internal int Threads => threadCount;

    /// <summary>Is the model loaded right now? Test seam for the load/unload cycle.</summary>
    internal bool IsLoaded
    {
        get { lock (gate) { return runtime is not null; } }
    }

    /// <summary>The runtime's own version string, for the bench and the diagnostics.</summary>
    public static string RuntimeVersion
    {
        get
        {
            Initialize();
            return Marshal.PtrToStringUTF8(Native.transcribe_version()) ?? string.Empty;
        }
    }

    /// <summary>
    /// Words the person taught, which the decoder leans toward: passed to the runtime as the
    /// run's vocabulary, in priority order, on every run.
    /// </summary>
    /// <remarks>
    /// The Parakeet decoder completes one of these spellings once the model has begun it on its
    /// own; it never starts one, and never decides whether anything was said. With no terms a
    /// run is bit for bit what it was. The weight is the runtime's own default (8.0), which is
    /// what upstream measured.
    /// </remarks>
    public Task SetVocabulary(IReadOnlyList<string> terms)
    {
        string[] cleaned = [.. terms.Select(term => term.Trim()).Where(term => term.Length > 0)];
        lock (gate) { vocabulary = cleaned; }
        return Task.CompletedTask;
    }

    /// <summary>For the test that guards what reaches the runtime.</summary>
    internal IReadOnlyList<string> Vocabulary
    {
        get { lock (gate) { return vocabulary; } }
    }

    /// <summary>
    /// Load the model from a prepared directory. Idempotent for the same directory; a different
    /// directory replaces the loaded model.
    /// </summary>
    /// <remarks>
    /// The directory holds exactly one GGUF file. Reading the directory rather than taking a file
    /// path keeps the installer's contract - "a verified revision lives here" - instead of
    /// spreading a filename through the app. Content integrity is <c>ModelStore</c>'s job: every
    /// byte was checked against the manifest's SHA-256 before this is called.
    /// </remarks>
    public async Task LoadModels(string directory)
    {
        lock (gate)
        {
            if (runtime is not null && loadedDirectory == directory) { return; }
        }

        string weights = ModelFile(directory);
        if (runtime is not null) { await Unload().ConfigureAwait(false); }

        Runtime built;
        try
        {
            // The weights are memory-mapped, so a load is a file read rather than a computation;
            // it still is not UI-thread work.
            built = await Task.Run(() => Runtime.Load(weights, threadCount)).ConfigureAwait(false);
        }
        catch (Exception error) when (IsRuntimeMissing(error))
        {
            throw new ASREngineError.RuntimeUnavailable(error.Message);
        }
        catch (Exception error) when (error is not ASREngineError)
        {
            throw new ASREngineError.ModelsUnavailable(error.Message);
        }

        bool superseded;
        lock (gate)
        {
            superseded = runtime is not null;
            if (!superseded)
            {
                runtime = new RuntimeLease<Runtime>(built);
                loadedDirectory = directory;
            }
        }

        // Two loads raced and the other one won. Idempotence is the contract; a second copy of
        // the session is not.
        if (superseded) { built.Dispose(); }
    }

    /// <summary>
    /// The native library could not be found, loaded or bound: missing from the installation,
    /// built for another processor, or from another version. The model files play no part in it.
    /// </summary>
    internal static bool IsRuntimeMissing(Exception error) =>
        error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
        || (error is TypeInitializationException { InnerException: { } inner } && IsRuntimeMissing(inner));

    /// <summary>
    /// The single GGUF in a prepared revision directory.
    ///
    /// Fails loudly on none and on more than one. A directory holding two models is an installer
    /// bug, and picking one of them arbitrarily would turn that bug into a mysterious quality
    /// complaint months later.
    /// </summary>
    internal static string ModelFile(string directory)
    {
        string[] candidates;
        try
        {
            candidates = [.. Directory.EnumerateFiles(directory)
                .Where(path => string.Equals(Path.GetExtension(path), ".gguf", StringComparison.OrdinalIgnoreCase))];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ASREngineError.ModelsUnavailable("the model directory is unreadable");
        }

        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new ASREngineError.ModelsUnavailable("no model file in the installed revision"),
            _ => throw new ASREngineError.ModelsUnavailable(
                $"the installed revision holds {candidates.Length} model files"),
        };
    }

    /// <summary>
    /// Recognize one fragment.
    /// </summary>
    /// <remarks>
    /// There is no language parameter. <c>transcribe_run_params.language</c> exists and, for this
    /// model family, changes nothing upstream could measure; an unknown code fails the run. The
    /// model chooses one language for the whole decode from the sound itself.
    /// </remarks>
    public async Task<ASRResult> Transcribe(float[] samples)
    {
        RuntimeLease<Runtime> lease;
        string[] terms;
        lock (gate)
        {
            if (runtime is null) { throw new ASREngineError.ModelsNotLoaded(); }
            if (samples.Length == 0) { throw new ASREngineError.UnsupportedAudioFormat("empty buffer"); }

            lease = runtime;
            lease.Acquire();
            terms = vocabulary;
        }

        Runtime engine = lease.Value;

        try
        {
            long started = Stopwatch.GetTimestamp();
            double audioSeconds = samples.Length / AudioFileReader.TargetSampleRate;
            // Padding is a demand of the front-end, not something the person said: it goes on the
            // end, so no time moves, and the reported duration stays the real one.
            float[] input = samples.Length >= MinimumEngineSamples
                ? samples
                : [.. samples, .. new float[MinimumEngineSamples - samples.Length]];

            Outcome outcome;
            try
            {
                // On a thread of its own, never the pool: the run is a synchronous native call
                // that occupies its thread for the whole inference. The result is read on the
                // same thread, inside the session lock - the next run on the session clears it.
                outcome = await DedicatedThread.Run(
                    () => engine.Run(input, terms, audioSeconds),
                    "OpenRamble recognition").ConfigureAwait(false);
            }
            catch (Exception error) when (error is not ASREngineError)
            {
                throw new ASREngineError.InferenceFailed(error.Message);
            }

            if (outcome.Status != Native.Ok)
            {
                throw new ASREngineError.InferenceFailed(Describe(outcome.Status), outcome.Status);
            }

            TimeSpan whole = Stopwatch.GetElapsedTime(started);
            // Empty is not an error: silence legitimately recognizes as nothing.
            return new ASRResult(
                outcome.Text.Trim(),
                outcome.Words,
                TimeSpan.FromSeconds(audioSeconds),
                outcome.Processing,
                engineDispatchDuration: whole > outcome.Processing ? whole - outcome.Processing : TimeSpan.Zero);
        }
        finally
        {
            lease.Release();
        }
    }

    /// <summary>
    /// Free the memory under the model.
    /// </summary>
    /// <remarks>
    /// Returns as soon as the engine is unreachable. If a recognition is still running on it, the
    /// session and model are freed by that recognition's own exit path - freeing them under a
    /// running decode would not be a nondeterministic state but a crash.
    /// </remarks>
    public Task Unload()
    {
        RuntimeLease<Runtime>? taken;
        lock (gate)
        {
            taken = runtime;
            runtime = null;
            loadedDirectory = null;
        }

        taken?.Retire();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The runtime's words, in this project's terms: trimmed, never empty, in order, and clamped
    /// into the clip so the padding behind a short one can never hold a word.
    /// </summary>
    /// <remarks>Ported from <c>recognized()</c> in upstream's <c>ramble-engine</c>.</remarks>
    internal static IReadOnlyList<ASRResult.Word> Words(
        IEnumerable<(string? Text, long StartMs, long EndMs)> raw,
        double clipSeconds)
    {
        long clipMs = (long)(clipSeconds * 1000);
        var words = new List<ASRResult.Word>();
        foreach ((string? text, long startMs, long endMs) in raw)
        {
            string trimmed = text?.Trim() ?? string.Empty;
            if (trimmed.Length == 0) { continue; }

            long start = Math.Clamp(startMs, 0, clipMs);
            long end = Math.Max(Math.Clamp(endMs, 0, clipMs), start);
            words.Add(new ASRResult.Word(
                trimmed,
                TimeSpan.FromMilliseconds(start),
                TimeSpan.FromMilliseconds(end)));
        }

        return words;
    }

    private static string Describe(int status) =>
        Marshal.PtrToStringUTF8(Native.transcribe_status_string(status)) ?? $"status {status}";

    // MARK: - The runtime

    private sealed record Outcome(int Status, string Text, IReadOnlyList<ASRResult.Word> Words, TimeSpan Processing);

    private static readonly Lazy<int> Initialization = new(InitializeOnce, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Once per process, before the first model: decide the logging, load the CPU backend
    /// modules, and check that the structs below are the ones the library was built with.
    /// </summary>
    private static void Initialize()
    {
        int status = Initialization.Value;
        if (status != Native.Ok)
        {
            throw new ASREngineError.RuntimeUnavailable($"the recognition runtime did not start: {Describe(status)}");
        }
    }

    private static int InitializeOnce()
    {
        // Silent unless asked: the runtime narrates every load and run to stderr, which on a
        // person's machine is noise in a terminal or a journal they never asked for. Nothing
        // dictated reaches that log either way. OPENRAMBLE_ENGINE_LOG=1 brings it back, the same
        // switch upstream's desktop core reads.
        if (Environment.GetEnvironmentVariable("OPENRAMBLE_ENGINE_LOG") is not ("1" or "true"))
        {
            Native.transcribe_log_set(IntPtr.Zero, IntPtr.Zero);
        }

        // The CPU backend is built once per x86 tier as a module next to the library; this picks
        // the best one the processor supports. A no-op for a build with the backend compiled in.
        int status = Native.transcribe_init_backends_default();
        if (status != Native.Ok) { return status; }

        // Every *_init() writes as many bytes as the library thinks the struct has. A layout
        // declared here that is smaller would be a write past the end of it, so the sizes are
        // compared before any struct is handed over - the check the header asks bindings to make.
        bool matches =
            Matches<Native.ModelLoadParams>(Native.AbiModelLoadParams)
            && Matches<Native.SessionParams>(Native.AbiSessionParams)
            && Matches<Native.RunParams>(Native.AbiRunParams)
            && Matches<Native.WordRow>(Native.AbiWord);
        return matches ? Native.Ok : Native.ErrorBadStructSize;
    }

    private static bool Matches<T>(int which) where T : struct =>
        (long)Native.transcribe_abi_struct_size(which) == Marshal.SizeOf<T>();

    /// <summary>
    /// One loaded model and its session, freed in the one correct order: session, then model.
    /// </summary>
    private sealed class Runtime : IDisposable
    {
        private readonly IntPtr model;
        private readonly IntPtr session;

        /// <summary>A session is single-threaded: runs on it take turns.</summary>
        private readonly Lock turn = new();

        private Runtime(IntPtr model, IntPtr session)
        {
            this.model = model;
            this.session = session;
        }

        public static Runtime Load(string path, int threads)
        {
            Initialize();

            Native.transcribe_model_load_params_init(out Native.ModelLoadParams load);
            load.Backend = Native.BackendCpu;
            int status = Native.transcribe_model_load_file(path, in load, out IntPtr model);
            if (status != Native.Ok || model == IntPtr.Zero)
            {
                throw new ASREngineError.ModelsUnavailable(Describe(status));
            }

            Native.transcribe_session_params_init(out Native.SessionParams parameters);
            parameters.Threads = threads;
            status = Native.transcribe_session_init(model, in parameters, out IntPtr session);
            if (status != Native.Ok || session == IntPtr.Zero)
            {
                Native.transcribe_model_free(model);
                throw new ASREngineError.ModelsUnavailable(Describe(status));
            }

            return new Runtime(model, session);
        }

        public Outcome Run(float[] samples, string[] terms, double clipSeconds)
        {
            IntPtr[] strings = [.. terms.Select(Marshal.StringToCoTaskMemUTF8)];
            GCHandle pinned = GCHandle.Alloc(strings, GCHandleType.Pinned);
            try
            {
                Native.transcribe_run_params_init(out Native.RunParams parameters);
                parameters.Timestamps = Native.TimestampsWord;
                if (strings.Length > 0)
                {
                    // The runtime copies what it needs before the call returns.
                    parameters.Vocabulary = pinned.AddrOfPinnedObject();
                    parameters.VocabularyCount = strings.Length;
                }

                lock (turn)
                {
                    long started = Stopwatch.GetTimestamp();
                    int status = Native.transcribe_run(session, samples, samples.Length, in parameters);
                    TimeSpan processing = Stopwatch.GetElapsedTime(started);
                    if (status != Native.Ok) { return new Outcome(status, string.Empty, [], processing); }

                    // Copied before the turn ends: the next run on this session clears it.
                    string text = Marshal.PtrToStringUTF8(Native.transcribe_full_text(session)) ?? string.Empty;
                    return new Outcome(Native.Ok, text, Words(ReadWords(), clipSeconds), processing);
                }
            }
            finally
            {
                pinned.Free();
                foreach (IntPtr text in strings) { Marshal.ZeroFreeCoTaskMemUTF8(text); }
            }
        }

        private List<(string?, long, long)> ReadWords()
        {
            int count = Native.transcribe_n_words(session);
            var rows = new List<(string?, long, long)>(Math.Max(0, count));
            for (int index = 0; index < count; index++)
            {
                Native.transcribe_word_init(out Native.WordRow row);
                if (Native.transcribe_get_word(session, index, ref row) != Native.Ok || row.Text == IntPtr.Zero) { continue; }

                rows.Add((Marshal.PtrToStringUTF8(row.Text), row.StartMs, row.EndMs));
            }

            return rows;
        }

        public void Dispose()
        {
            Native.transcribe_session_free(session);
            Native.transcribe_model_free(model);
        }
    }

    /// <summary>
    /// <c>include/transcribe.h</c> at the pinned commit, as much of it as this adapter calls.
    /// </summary>
    /// <remarks>
    /// Field order and types follow the header exactly; C <c>bool</c> is one byte. Sizes are
    /// checked against the library at start (<see cref="InitializeOnce"/>), so a header that
    /// moved fails the load instead of corrupting memory.
    /// </remarks>
    internal static partial class Native
    {
        private const string Library = "transcribe";

        public const int Ok = 0;
        public const int ErrorBadStructSize = 14;
        public const int BackendCpu = 1;
        public const int TimestampsWord = 3;

        public const int AbiModelLoadParams = 0;
        public const int AbiSessionParams = 1;
        public const int AbiRunParams = 2;
        public const int AbiWord = 7;

        [StructLayout(LayoutKind.Sequential)]
        public struct ModelLoadParams
        {
            public ulong StructSize;
            public int Backend;
            public IntPtr Device;
            public byte NoMmap;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SessionParams
        {
            public ulong StructSize;
            public int Threads;
            public int KvType;
            public int Context;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RunParams
        {
            public ulong StructSize;
            public int Task;
            public int Timestamps;
            public int Pnc;
            public int Itn;
            public int Diarize;
            public IntPtr Language;
            public IntPtr TargetLanguage;
            public byte KeepSpecialTags;
            public IntPtr Family;
            public int SpeculativeDrafts;
            public IntPtr Vocabulary;
            public int VocabularyCount;
            public IntPtr Prompt;
            public IntPtr Prefix;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WordRow
        {
            public ulong StructSize;
            public long StartMs;
            public long EndMs;
            public int SegmentIndex;
            public int FirstToken;
            public int TokenCount;
            public IntPtr Text;
        }

        [LibraryImport(Library)]
        public static partial IntPtr transcribe_version();

        [LibraryImport(Library)]
        public static partial IntPtr transcribe_status_string(int status);

        [LibraryImport(Library)]
        public static partial nuint transcribe_abi_struct_size(int which);

        [LibraryImport(Library)]
        public static partial void transcribe_log_set(IntPtr callback, IntPtr userData);

        [LibraryImport(Library)]
        public static partial int transcribe_init_backends_default();

        [LibraryImport(Library)]
        public static partial void transcribe_model_load_params_init(out ModelLoadParams parameters);

        [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
        public static partial int transcribe_model_load_file(string path, in ModelLoadParams parameters, out IntPtr model);

        [LibraryImport(Library)]
        public static partial void transcribe_model_free(IntPtr model);

        [LibraryImport(Library)]
        public static partial void transcribe_session_params_init(out SessionParams parameters);

        [LibraryImport(Library)]
        public static partial int transcribe_session_init(IntPtr model, in SessionParams parameters, out IntPtr session);

        [LibraryImport(Library)]
        public static partial void transcribe_session_free(IntPtr session);

        [LibraryImport(Library)]
        public static partial void transcribe_run_params_init(out RunParams parameters);

        [LibraryImport(Library)]
        public static partial int transcribe_run(IntPtr session, float[] pcm, int sampleCount, in RunParams parameters);

        [LibraryImport(Library)]
        public static partial IntPtr transcribe_full_text(IntPtr session);

        [LibraryImport(Library)]
        public static partial int transcribe_n_words(IntPtr session);

        [LibraryImport(Library)]
        public static partial void transcribe_word_init(out WordRow row);

        [LibraryImport(Library)]
        public static partial int transcribe_get_word(IntPtr session, int index, ref WordRow row);
    }
}
