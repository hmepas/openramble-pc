namespace LocalASR;

public sealed partial record ModelManifest
{
    /// <summary>
    /// Manifest compiled into the application.
    ///
    /// This is the root of trust for the entire installation: file names, their sizes and the
    /// checksums come from here, and the application signature excludes substitution.
    /// </summary>
    /// <remarks>
    /// Swift reads them through <c>Bundle.module</c> plus <c>LocalFile.read</c>. Here they
    /// are assembly resources, so there is no file path involved at all: nothing can point
    /// this read at an http address, which is what <c>LocalFile</c> guards on the Swift side.
    /// </remarks>
    public static ModelManifest Bundled() => DecodeResource("LocalASR.Resources.model-manifest.json");

    /// <summary>
    /// Parakeet Ultra, one GGUF file for the transcribe.cpp engine (<see cref="TranscribeCppAdapter"/>):
    /// the model upstream's desktop builds ship, at the quantization they ship it in (Q8_0 - on a
    /// processor Q4_K_M is the slower of the two).
    /// </summary>
    /// <remarks>
    /// <see cref="Bundled"/> stays what it was - Parakeet TDT v3, weight-only int4 ONNX, for
    /// sherpa-onnx - because a build without the transcribe.cpp runtime (Windows, for now) still
    /// installs it, and a Linux machine that already holds it keeps dictating with it until this
    /// one is downloaded, verified and loaded.
    /// </remarks>
    public static ModelManifest BundledUltra() => DecodeResource("LocalASR.Resources.ultra-model-manifest.json");

    /// <summary>
    /// Manifest of the acoustic term hinter (Parakeet CTC 110M).
    ///
    /// A separate file, not a section of the main one: the model has its own repository and
    /// its own revision, and it has its own life cycle - installation, verification and
    /// restoration go through the same ModelStore as the main one.
    /// </summary>
    /// <exception cref="ModelManifestError.EngineUnsupported">
    /// Always, on this port. The embedded resource still describes the macOS Core ML bundles
    /// (<c>MelSpectrogram.mlmodelc</c>, <c>AudioEncoder.mlmodelc</c>), and the sherpa-onnx
    /// engine cannot load a single one of them. Installing them would download about 98 MB
    /// that nothing can read, so the door is closed here rather than one layer down, where
    /// the failure would look like a damaged model and invite the user to download it again.
    /// The resource is kept, unchanged, because the moment there is a Parakeet CTC 110M ONNX
    /// export this becomes a data change and not a code change. See
    /// <see cref="SherpaOnnxAdapter"/> for why the engine does not offer the hint at all.
    /// </exception>
    public static ModelManifest BundledVocabulary() => throw new ModelManifestError.EngineUnsupported(
        "the acoustic term hinter is Core ML only; no ONNX export of Parakeet CTC 110M exists yet");

    private static ModelManifest DecodeResource(string name)
    {
        using Stream? resource = typeof(ModelManifest).Assembly.GetManifestResourceStream(name);
        if (resource is null)
        {
            throw new ModelManifestError.ResourceMissing();
        }

        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        return Decode(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }
}
