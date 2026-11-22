using DictationCore;

namespace LocalASR;

/// <summary>
/// Recognizes with whichever engine reads the model in the directory it is loaded from: a GGUF
/// file goes to transcribe.cpp, anything else to sherpa-onnx.
/// </summary>
/// <remarks>
/// <para>
/// Exists for one transition only. The Linux build recognizes with Parakeet Ultra on
/// transcribe.cpp, but a machine updated from an earlier build holds Parakeet v3 as ONNX files,
/// and <see cref="ModelLineup"/> keeps that model dictating until Ultra is downloaded, verified
/// and loaded. Both engines stay behind one <see cref="IASREngineAdapting"/> so that
/// <see cref="LocalTranscriber"/> - its single-flight load, its generations, its pieces - is the
/// same object whichever model serves.
/// </para>
/// <para>
/// One engine holds weights at a time: loading a directory of the other kind unloads the first.
/// The taught words go to both, so the choice of model never decides whether they apply.
/// </para>
/// </remarks>
public sealed class EngineByModelFiles : IASREngineAdapting
{
    private readonly Lock gate = new();
    private readonly IASREngineAdapting gguf;
    private readonly IASREngineAdapting onnx;
    private IASREngineAdapting? active;

    public EngineByModelFiles(IASREngineAdapting gguf, IASREngineAdapting onnx)
    {
        this.gguf = gguf;
        this.onnx = onnx;
    }

    public async Task LoadModels(string directory)
    {
        IASREngineAdapting chosen = HoldsGguf(directory) ? gguf : onnx;
        IASREngineAdapting? replaced;
        lock (gate)
        {
            replaced = active is not null && !ReferenceEquals(active, chosen) ? active : null;
            active = chosen;
        }

        if (replaced is not null) { await replaced.Unload().ConfigureAwait(false); }
        await chosen.LoadModels(directory).ConfigureAwait(false);
    }

    public Task<ASRResult> Transcribe(float[] samples)
    {
        IASREngineAdapting? engine;
        lock (gate) { engine = active; }
        return engine is null
            ? Task.FromException<ASRResult>(new ASREngineError.ModelsNotLoaded())
            : engine.Transcribe(samples);
    }

    public async Task Unload()
    {
        await gguf.Unload().ConfigureAwait(false);
        await onnx.Unload().ConfigureAwait(false);
    }

    public async Task SetVocabulary(IReadOnlyList<string> terms)
    {
        await gguf.SetVocabulary(terms).ConfigureAwait(false);
        await onnx.SetVocabulary(terms).ConfigureAwait(false);
    }

    /// <summary>Matched exactly as <see cref="TranscribeCppAdapter.ModelFile"/> matches it, whatever the case.</summary>
    private static bool HoldsGguf(string directory) =>
        Directory.Exists(directory)
        && Directory.EnumerateFiles(directory)
            .Any(path => string.Equals(Path.GetExtension(path), ".gguf", StringComparison.OrdinalIgnoreCase));
}
