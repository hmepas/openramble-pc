using System.Text.Json.Serialization;

namespace LocalASR;

/// <summary>
/// Layout of model files on disk.
///
/// Installation goes through an intermediate directory: files are downloaded into staging,
/// are checked there and only then move to their final place in a single rename. A half
/// installation that could be taken for a working one does not exist by construction.
/// </summary>
public sealed class ModelInstallLayout
{
    public ModelInstallLayout(string root, string modelID, string revision, string engineFolderName)
    {
        Root = root;
        ModelID = modelID;
        Revision = revision;
        EngineFolderName = engineFolderName;
    }

    public ModelInstallLayout(ModelManifest manifest, string? root = null)
    {
        string basePath = root
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenRamble",
                "Models");

        // The library gets the folder name from the repository name, discarding the suffix
        // "-coreml". Tested against tag code 0.15.5.
        string[] parts = manifest.Repository.Split('/');
        string folder = (parts.Length > 0 && parts[^1].Length > 0 ? parts[^1] : manifest.ModelID)
            .Replace("-coreml", "", StringComparison.Ordinal);

        Root = basePath;
        ModelID = manifest.ModelID;
        Revision = manifest.Revision;
        EngineFolderName = folder;
    }

    /// <summary>Root: %LOCALAPPDATA%\OpenRamble\Models (Swift: ~/Library/Application Support/OpenRamble/Models). Matches AppPaths.Models() — the app and asr-bench must agree on this directory.</summary>
    public string Root { get; }

    public string ModelID { get; }

    public string Revision { get; }

    /// <summary>
    /// The folder name required by the engine.
    ///
    /// The library computes the path as "parent of the passed directory + repository name",
    /// although its documentation promises that passing the folder with the bundles is
    /// enough. The discrepancy was checked on tag code 0.15.5, so the layout adapts to the
    /// actual behavior.
    /// </summary>
    public string EngineFolderName { get; }

    /// <summary>Directory of a specific model.</summary>
    public string ModelDirectory => Path.Combine(Root, ModelID);

    /// <summary>
    /// Final installation location. The revision is in the name, so changing the revision
    /// does not spoil an already installed model and allows a rollback.
    /// </summary>
    public string InstalledDirectory => Path.Combine(ModelDirectory, Revision);

    /// <summary>Directory to be passed to the engine.</summary>
    public string EngineDirectory => Path.Combine(InstalledDirectory, EngineFolderName);

    /// <summary>Where to put files inside the intermediate installation directory.</summary>
    public string EngineDirectoryInside(string staging) => Path.Combine(staging, EngineFolderName);

    /// <summary>
    /// Ready marker. Written last; its presence means that all files are in place and
    /// verified.
    /// </summary>
    public string ReadyMarker => Path.Combine(InstalledDirectory, ReadyMarkerName);

    /// <summary>The marker's file name, needed on its own by interrupted-promotion recovery.</summary>
    internal const string ReadyMarkerName = ".ready.json";

    /// <summary>
    /// The last known working installation, kept for the duration of a crash-safe promotion.
    /// On normal completion it is deleted; after a crash it is used for recovery before the
    /// state is shown to the interface.
    /// </summary>
    public string BackupDirectory => Path.Combine(ModelDirectory, $".backup-{Revision}");

    /// <summary>
    /// Directory for a specific installation attempt. Unique so that two parallel attempts
    /// do not trample each other.
    /// </summary>
    public string StagingDirectory(Guid attempt) =>
        Path.Combine(ModelDirectory, $".staging-{attempt:D}");

    /// <summary>
    /// File path inside the installation directory.
    ///
    /// The path from the manifest has already been checked to stay inside the directory, but
    /// here it is rechecked: too expensive a mistake to rely on a single barrier.
    /// </summary>
    public string Destination(ModelManifest.File file, string directory)
    {
        // The path from the manifest itself is checked, not the result of gluing.
        //
        // The glued path would have to be brought to canonical form through the file
        // system, but it answers differently for an existing entry and for one not created
        // yet: the first gives "/tmp", the second "/private/tmp" - the same location,
        // different strings. The barrier began to depend on what already lay on disk, and
        // rejected perfectly legal files, breaking the installation entirely. Parsing a
        // relative path does not depend on the disk at all.
        //
        // Exactly three things lead outside: "..", a leading slash, and an empty path.
        string[] components = file.Path.Split('/');
        bool safe = components.Length > 0
            && !components.Any(component =>
                component.Length == 0
                || component == "."
                || component == ".."
                // Not in the Swift original, and required here: on Windows a backslash is
                // also a separator, so "a\..\..\b" would be a single innocent-looking
                // component there and a way out of the directory on disk.
                || component.AsSpan().IndexOfAny('\\', ':') >= 0);
        if (!safe)
        {
            throw new ModelInstallError.UnsafePath(file.Path);
        }

        string destination = directory;
        foreach (string component in components)
        {
            destination = Path.Combine(destination, component);
        }

        return destination;
    }
}

/// <remarks>
/// The Swift original is an <c>Error</c> enum with associated values; here the cases are
/// nested exception types so that <c>throw</c> stays honest.
/// </remarks>
public abstract class ModelInstallError : Exception
{
    private protected ModelInstallError(string message)
        : base(message)
    {
    }

    public sealed class UnsafePath : ModelInstallError
    {
        public UnsafePath(string path)
            : base($"unsafe path: {path}")
        {
            Path = path;
        }

        public string Path { get; }
    }

    public sealed class InsufficientDiskSpace : ModelInstallError
    {
        public InsufficientDiskSpace(long requiredBytes, long availableBytes)
            : base($"not enough disk space: {requiredBytes} required, {availableBytes} available")
        {
            RequiredBytes = requiredBytes;
            AvailableBytes = availableBytes;
        }

        public long RequiredBytes { get; }

        public long AvailableBytes { get; }
    }

    public sealed class PromoteFailed : ModelInstallError
    {
        public PromoteFailed(string reason)
            : base(reason)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }
}

// MARK: - Ready marker

/// <summary>
/// Contents of <c>.ready.json</c>.
///
/// Stores exactly what is needed to understand whether what is installed corresponds to what
/// we expect now. If the revision or the version of FluidAudio differs, the installation is
/// considered unsuitable, and not "probably will do".
/// </summary>
public sealed record ModelReadyMarker
{
    [JsonConstructor]
    public ModelReadyMarker(
        string revision,
        string fluidAudioVersion,
        int fileCount,
        long totalByteCount,
        DateTimeOffset verifiedAt,
        IReadOnlyList<InstalledFile>? installedFiles = null)
    {
        Revision = revision;
        FluidAudioVersion = fluidAudioVersion;
        FileCount = fileCount;
        TotalByteCount = totalByteCount;
        VerifiedAt = verifiedAt;
        InstalledFiles = installedFiles;
    }

    public ModelReadyMarker(
        ModelManifest manifest,
        DateTimeOffset verifiedAt,
        IReadOnlyList<InstalledFile>? installedFiles = null)
        : this(
            manifest.Revision,
            manifest.FluidAudioVersion,
            manifest.Files.Count,
            manifest.TotalByteCount,
            verifiedAt,
            installedFiles)
    {
    }

    public sealed record InstalledFile
    {
        [JsonConstructor]
        public InstalledFile(string path, long byteCount, DateTimeOffset? modifiedAt)
        {
            Path = path;
            ByteCount = byteCount;
            ModifiedAt = modifiedAt;
        }

        [JsonPropertyName("path")]
        public string Path { get; }

        [JsonPropertyName("byteCount")]
        public long ByteCount { get; }

        [JsonPropertyName("modifiedAt")]
        public DateTimeOffset? ModifiedAt { get; }
    }

    [JsonPropertyName("revision")]
    public string Revision { get; }

    [JsonPropertyName("fluidAudioVersion")]
    public string FluidAudioVersion { get; }

    [JsonPropertyName("fileCount")]
    public int FileCount { get; }

    [JsonPropertyName("totalByteCount")]
    public long TotalByteCount { get; }

    [JsonPropertyName("verifiedAt")]
    public DateTimeOffset VerifiedAt { get; }

    /// <summary>
    /// Old marker files do not contain an inventory. They decode, but go through the full
    /// check once and are rewritten in the new format.
    /// </summary>
    [JsonPropertyName("installedFiles")]
    public IReadOnlyList<InstalledFile>? InstalledFiles { get; }

    /// <summary>Whether what is installed fully matches the current manifest.</summary>
    public bool Matches(ModelManifest manifest) =>
        DescribesSameFiles(manifest) && FluidAudioVersion == manifest.FluidAudioVersion;

    /// <summary>
    /// Whether these are the same files that the manifest asks for.
    ///
    /// Separated from <see cref="Matches"/> intentionally. The FluidAudio version is a
    /// property of the application, not of the weights: a library update that does not
    /// affect the model revision does not make the files on disk differ by a byte. While
    /// this was one check, a dependency patch meant "the model is damaged, download 483 MB"
    /// for every user - for the fact that we raised a version number.
    ///
    /// The revision is the main thing: it is the pinned SHA of the model commit, and its
    /// match means exactly the same bytes are expected. A match is a reason to double-check
    /// the files by checksum, not to download them again.
    /// </summary>
    public bool DescribesSameFiles(ModelManifest manifest) =>
        Revision == manifest.Revision
        && FileCount == manifest.Files.Count
        && TotalByteCount == manifest.TotalByteCount;

    /// <summary>Element-wise, like the Swift array equality this replaces.</summary>
    public bool Equals(ModelReadyMarker? other) =>
        other is not null
        && Revision == other.Revision
        && FluidAudioVersion == other.FluidAudioVersion
        && FileCount == other.FileCount
        && TotalByteCount == other.TotalByteCount
        && VerifiedAt == other.VerifiedAt
        && (InstalledFiles is null
            ? other.InstalledFiles is null
            : other.InstalledFiles is not null && InstalledFiles.SequenceEqual(other.InstalledFiles));

    public override int GetHashCode() =>
        HashCode.Combine(Revision, FluidAudioVersion, FileCount, TotalByteCount, VerifiedAt);
}
