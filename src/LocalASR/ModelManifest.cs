using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalASR;

/// <summary>
/// Description of what exactly is downloaded for local recognition.
///
/// The manifest lists <b>specific files</b> rather than the entire folder: the model repository
/// on Hugging Face weighs about 3 GB (there are also MelEncoder, EncoderInt4 and
/// JointDecisionv2, which we don't need), while the working set is approximately 483 MB.
///
/// The manifest is compiled into the application as a resource. Trust in it is ensured
/// by the signature of the application itself: it is impossible to replace the manifest without
/// breaking the signature.
/// </summary>
public sealed partial record ModelManifest
{
    /// <remarks>
    /// The parameter is <c>mirrorSource</c> and not <c>mirror</c> for the same reason the
    /// property is: <see cref="Mirror"/> is already the nested type's name, and
    /// System.Text.Json matches constructor parameters to properties by name.
    /// </remarks>
    [JsonConstructor]
    public ModelManifest(
        string modelID,
        string repository,
        string revision,
        string fluidAudioVersion,
        string quantization,
        string license,
        IReadOnlyList<File> files,
        Mirror? mirrorSource = null)
    {
        ModelID = modelID;
        Repository = repository;
        Revision = revision;
        FluidAudioVersion = fluidAudioVersion;
        Quantization = quantization;
        License = license;
        Files = files;
        MirrorSource = mirrorSource;
    }

    /// <summary>One model file.</summary>
    public sealed record File
    {
        [JsonConstructor]
        public File(string path, long byteCount, string sha256)
        {
            Path = path;
            ByteCount = byteCount;
            Sha256 = sha256;
        }

        /// <summary>The path inside the model repository, also the path inside the installation directory.</summary>
        [JsonPropertyName("path")]
        public string Path { get; }

        /// <summary>Expected size. It is checked before the checksum is calculated - it is cheap.</summary>
        [JsonPropertyName("byteCount")]
        public long ByteCount { get; }

        /// <summary>SHA-256 in lower case. The only protection against damaged downloads.</summary>
        [JsonPropertyName("sha256")]
        public string Sha256 { get; }
    }

    /// <summary>
    /// Second address of the same files.
    ///
    /// Single source - single point of failure: the repository on Hugging Face may be
    /// deleted, renamed or closed for an entire region, and then the new user will not
    /// launch the application at all. For a project that is installed even a year after
    /// release, this is a common scenario.
    ///
    /// A spare address does not weaken anything: trust in files is not based on where they
    /// came from, but on SHA-256 from the same manifest - they are checked the same for any
    /// source.
    /// </summary>
    public sealed record Mirror
    {
        [JsonConstructor]
        public Mirror(string repository, string releaseTag)
        {
            Repository = repository;
            ReleaseTag = releaseTag;
        }

        /// <summary>"owner/repository" on GitHub.</summary>
        [JsonPropertyName("repository")]
        public string Repository { get; }

        /// <summary>Release tag to which the files are attached.</summary>
        [JsonPropertyName("releaseTag")]
        public string ReleaseTag { get; }

        /// <summary>
        /// The name of the release attachment for the model file.
        ///
        /// GitHub doesn't allow forward slashes in attachment names, so the path inside the
        /// model is flattened. The divider is doubled: single underscores appear in the file
        /// names themselves.
        ///
        /// What to put in a release with the <c>ReleaseTag</c> tag: each manifest file as a
        /// separate attachment with this name. For example
        /// <c>Encoder.mlmodelc/weights/weight.bin</c> becomes
        /// <c>Encoder.mlmodelc__weights__weight.bin</c>. No archives, no attachment folders -
        /// the application takes files one by one and checks their sums.
        /// </summary>
        public string AssetName(File file) => file.Path.Replace("/", "__", StringComparison.Ordinal);
    }

    /// <summary>Model identifier, also known as the name of the installation directory.</summary>
    [JsonPropertyName("modelID")]
    public string ModelID { get; }

    /// <summary>Hugging Face repository.</summary>
    [JsonPropertyName("repository")]
    public string Repository { get; }

    /// <summary>
    /// Immutable revision. Never <c>main</c>: the contents of the branch may change below us,
    /// and we promise reproducibility.
    /// </summary>
    [JsonPropertyName("revision")]
    public string Revision { get; }

    /// <summary>
    /// The version of the recognition engine library this set of files is tested against.
    /// </summary>
    /// <remarks>
    /// The JSON key is still <c>fluidAudioVersion</c>, and the property still carries that
    /// name, because the key is on-disk data: it is written into every installation's
    /// <c>.ready.json</c> marker, and renaming it would declare every installed model foreign
    /// and re-download it. What it holds on this port is the pinned sherpa-onnx version.
    /// </remarks>
    [JsonPropertyName("fluidAudioVersion")]
    public string FluidAudioVersion { get; }

    /// <summary>Quantization - for fair attribution according to CC BY 4.0.</summary>
    [JsonPropertyName("quantization")]
    public string Quantization { get; }

    /// <summary>Licensing scales.</summary>
    [JsonPropertyName("license")]
    public string License { get; }

    [JsonPropertyName("files")]
    public IReadOnlyList<File> Files { get; }

    /// <summary>
    /// Spare source. It may be missing - then there is only one path to the model.
    /// </summary>
    /// <remarks>
    /// Named <c>MirrorSource</c> and not <c>Mirror</c> only because the nested type already
    /// owns that name in C#; the JSON key stays <c>mirror</c>, as in the Swift original.
    /// </remarks>
    [JsonPropertyName("mirror")]
    public Mirror? MirrorSource { get; }

    /// <summary>Total download weight - shown to the user before pressing the button.</summary>
    [JsonIgnore]
    public long TotalByteCount => Files.Sum(file => file.ByteCount);

    /// <summary>File address on Hugging Face. The revision is pinned, so the link stays unchanged.</summary>
    public Uri? DownloadURL(File file) =>
        BuildURL("huggingface.co", $"/{Repository}/resolve/{Revision}/{file.Path}");

    /// <summary>Address of the same file in the release on GitHub.</summary>
    public Uri? MirrorURL(File file)
    {
        if (MirrorSource is not { } mirror)
        {
            return null;
        }

        return BuildURL(
            "github.com",
            $"/{mirror.Repository}/releases/download/{mirror.ReleaseTag}/{mirror.AssetName(file)}");
    }

    /// <summary>All file addresses in the order of access: first the main one, then the backup one.</summary>
    public IReadOnlyList<Uri> DownloadURLs(File file) =>
        new[] { DownloadURL(file), MirrorURL(file) }.OfType<Uri>().ToArray();

    /// <summary>
    /// Swift builds the address through <c>URLComponents</c>, which returns <c>nil</c> for
    /// anything it cannot assemble. <see cref="Uri.TryCreate(string, UriKind, out Uri)"/> is
    /// the same door: a manifest holding garbage produces no address instead of an exception
    /// somewhere deep in the install.
    /// </summary>
    private static Uri? BuildURL(string host, string path) =>
        Uri.TryCreate($"https://{host}{path}", UriKind.Absolute, out Uri? url) ? url : null;

    /// <summary>Element-wise, like the Swift array equality this replaces.</summary>
    public bool Equals(ModelManifest? other) =>
        other is not null
        && ModelID == other.ModelID
        && Repository == other.Repository
        && Revision == other.Revision
        && FluidAudioVersion == other.FluidAudioVersion
        && Quantization == other.Quantization
        && License == other.License
        && Files.SequenceEqual(other.Files)
        && MirrorSource == other.MirrorSource;

    public override int GetHashCode() =>
        HashCode.Combine(ModelID, Repository, Revision, FluidAudioVersion, Files.Count, MirrorSource);
}

/// <remarks>
/// The Swift original is an <c>Error</c> enum with associated values; here the cases are
/// nested exception types so that <c>throw</c> stays honest.
/// </remarks>
public abstract class ModelManifestError : Exception
{
    private protected ModelManifestError(string message)
        : base(message)
    {
    }

    /// <summary>The compiled resource is not in the assembly.</summary>
    public sealed class ResourceMissing : ModelManifestError
    {
        public ResourceMissing()
            : base("the compiled manifest resource is missing")
        {
        }
    }

    /// <summary>The bytes are not the JSON we expect.</summary>
    public sealed class Malformed : ModelManifestError
    {
        public Malformed(string reason)
            : base(reason)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }

    /// <summary>
    /// The manifest exists, but this build's engine cannot use what it describes.
    ///
    /// Kept apart from <see cref="Invalid"/> on purpose: an invalid manifest is our bug, and
    /// this one is a missing publication upstream. Nothing the user does - repair, re-download,
    /// reinstall - changes it, so nothing should offer them any of those.
    /// </summary>
    public sealed class EngineUnsupported : ModelManifestError
    {
        public EngineUnsupported(string reason)
            : base(reason)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }

    /// <summary>The JSON parsed, but the manifest does not make sense.</summary>
    public sealed class Invalid : ModelManifestError
    {
        public Invalid(string reason)
            : base(reason)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }
}

// MARK: - Loading the compiled manifest

public sealed partial record ModelManifest
{
    /// <summary>
    /// Explicit, so nothing depends on the ambient serializer defaults: the manifest is the
    /// root of trust for the whole installation.
    /// </summary>
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    /// <summary>
    /// Parse the manifest and make sure it makes sense.
    ///
    /// The checks are intentionally strict: the manifest is the root of trust in the entire
    /// installation, and it is better to refuse at the start than to download 483 MB and
    /// discover the problem later.
    /// </summary>
    public static ModelManifest Decode(ReadOnlySpan<byte> data)
    {
        ModelManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ModelManifest>(data, SerializerOptions);
        }
        catch (JsonException error)
        {
            throw new ModelManifestError.Malformed(error.Message);
        }

        if (manifest is null)
        {
            throw new ModelManifestError.Malformed("the manifest is null");
        }

        if (manifest.Files.Count == 0)
        {
            throw new ModelManifestError.Invalid("the manifest lists no files");
        }

        // The revision must be the full SHA of the commit: a short form or a branch name
        // does not guarantee that the contents will not change.
        if (manifest.Revision.Length != 40 || !manifest.Revision.All(Uri.IsHexDigit))
        {
            throw new ModelManifestError.Invalid("the revision must be a full commit SHA-1");
        }

        foreach (File file in manifest.Files)
        {
            if (file.ByteCount <= 0)
            {
                throw new ModelManifestError.Invalid($"zero size for {file.Path}");
            }

            if (file.Sha256.Length != 64 || !file.Sha256.All(char.IsAsciiHexDigitLower))
            {
                throw new ModelManifestError.Invalid($"invalid SHA-256 for {file.Path}");
            }

            // The path comes from the manifest and participates in constructing the path on
            // the disk, therefore leaving the installation directory must not be possible.
            if (file.Path.StartsWith('/')
                || file.Path.Contains("..", StringComparison.Ordinal)
                || file.Path.Length == 0)
            {
                throw new ModelManifestError.Invalid($"invalid path: {file.Path}");
            }
        }

        if (manifest.MirrorSource is { } mirror)
        {
            // The backup address is also a root of trust: the URL is built from it, and
            // garbage in it should be caught at the start, and not when the main source
            // fails, when there is no time to repair.
            string[] parts = mirror.Repository.Split('/');
            if (parts.Length != 2 || parts.Any(string.IsNullOrEmpty))
            {
                throw new ModelManifestError.Invalid("the mirror repository must be “owner/name”");
            }

            if (mirror.ReleaseTag.Length == 0)
            {
                throw new ModelManifestError.Invalid("the mirror has an empty release tag");
            }

            if (!manifest.Files.All(file => manifest.MirrorURL(file) is not null))
            {
                throw new ModelManifestError.Invalid("couldn't build a URL from the mirror");
            }
        }

        return manifest;
    }
}
