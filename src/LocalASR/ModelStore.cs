using System.Text.Json;
using System.Threading.Channels;

namespace LocalASR;

/// <summary>Model installation status.</summary>
/// <remarks>
/// The Swift original is an <c>enum</c> with associated values; here it is a closed record
/// hierarchy, so every case keeps its payload and pattern matching stays exhaustive.
/// </remarks>
public abstract record ModelState
{
    private ModelState()
    {
    }

    public sealed record NotInstalled : ModelState;

    public sealed record Downloading(long ReceivedBytes, long TotalBytes) : ModelState;

    public sealed record Verifying(int Checked, int Total) : ModelState;

    public sealed record Ready(string Directory) : ModelState;

    public sealed record RepairRequired(string Reason) : ModelState;

    public sealed record Failed(ModelStoreError Error) : ModelState;

    public sealed record Deleting : ModelState;

    public bool IsReady => this is Ready;

    public bool RequiresRepair => this is RepairRequired;

    /// <summary>Percentage of completion for the indicator, if it is meaningful.</summary>
    public double? Progress => this switch
    {
        Downloading(var received, var total) when total > 0 => Math.Min(1, (double)received / total),
        Verifying(var done, var total) when total > 0 => (double)done / total,
        _ => null,
    };
}

/// <remarks>
/// The Swift original is an <c>Error</c> enum with associated values; here the cases are
/// nested exception types so that <c>throw</c> stays honest.
/// </remarks>
public abstract class ModelStoreError : Exception
{
    private protected ModelStoreError(string message)
        : base(message)
    {
    }

    public sealed class Manifest : ModelStoreError
    {
        public Manifest(string reason)
            : base(reason)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }

    public sealed class Download : ModelStoreError
    {
        public Download(string reason)
            : base(reason)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }

    public sealed class Verification : ModelStoreError
    {
        public Verification(string reason)
            : base(reason)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }

    public sealed class Install : ModelStoreError
    {
        public Install(string reason)
            : base(reason)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }

    public sealed class RepairRequired : ModelStoreError
    {
        public RepairRequired(string reason)
            : base(reason)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }

    /// <summary>
    /// The files are intact, but the engine that reads them cannot start on this machine.
    /// </summary>
    /// <remarks>
    /// Port addition, and never thrown by the store: it is how a verified model that cannot be
    /// used is shown, without offering the download that would not help.
    /// </remarks>
    public sealed class EngineUnavailable : ModelStoreError
    {
        public EngineUnavailable(string reason)
            : base(reason)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }

    /// <summary>The folder the user asked to take the model from is not suitable.</summary>
    public sealed class ImportSource : ModelStoreError
    {
        public ImportSource(string reason)
            : base(reason)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }

    public sealed class NotEnoughDiskSpace : ModelStoreError
    {
        public NotEnoughDiskSpace(long requiredBytes, long availableBytes)
            : base($"not enough disk space: {requiredBytes} required, {availableBytes} available")
        {
            RequiredBytes = requiredBytes;
            AvailableBytes = availableBytes;
        }

        public long RequiredBytes { get; }

        public long AvailableBytes { get; }
    }

    public sealed class Cancelled : ModelStoreError
    {
        public Cancelled()
            : base("cancelled")
        {
        }
    }
}

/// <summary>Test seam simulating a process kill between the file steps of a promotion.</summary>
public enum ModelPromotionCheckpoint
{
    AfterBackup,
    AfterStagingMove,
    AfterReadyMarker,
    AfterBackupRemoval,
}

/// <summary>
/// Installing, checking and deleting the model.
///
/// The order is strict: download into staging → check there → move in one motion → write the
/// ready marker. An intermediate state never looks like a working installation.
/// </summary>
/// <remarks>
/// The Swift original is an <c>actor</c>, so installing, deleting and reading the state never
/// intersect. Here a lock guards the state and a single active task slot gives the same
/// serialization; the long file and network work happens outside the lock, exactly as an
/// actor suspends across <c>await</c>.
/// </remarks>
public sealed class ModelStore
{
    private readonly Lock gate = new();
    private readonly ModelManifest manifest;
    private readonly ModelInstallLayout layout;
    private readonly IModelDownloading downloader;

    /// <summary>Pause between attempts on the same source. Injected so tests do not wait.</summary>
    private readonly TimeSpan retryDelay;

    private readonly ModelVerifier verifier;
    private readonly ModelPromotionCheckpoint? injectedPromotionFailure;

    private ModelState state = new ModelState.NotInstalled();
    private readonly Dictionary<Guid, Channel<ModelState>> observers = [];
    private Task? activeTask;
    private CancellationTokenSource? activeCancellation;

    public ModelStore(
        ModelManifest manifest,
        ModelInstallLayout layout,
        IModelDownloading downloader,
        ModelVerifier? verifier = null,
        TimeSpan? retryDelay = null,
        ModelPromotionCheckpoint? injectedPromotionFailure = null)
    {
        this.manifest = manifest;
        this.layout = layout;
        this.downloader = downloader;
        this.verifier = verifier ?? new ModelVerifier();
        this.retryDelay = retryDelay ?? TimeSpan.FromSeconds(2);
        this.injectedPromotionFailure = injectedPromotionFailure;
    }

    /// <summary>
    /// How many times one source is tried before handing over to the mirror.
    ///
    /// Three, not more: the encoder is 92% of a 586 MB install, and a source that fails three
    /// times in a row is not blinking, it is down. The mirror is the better answer at that
    /// point.
    /// </summary>
    private const int AttemptsPerSource = 3;

    /// <summary>
    /// Is this failure worth trying again?
    ///
    /// Only the failures where the same request, sent again, can plausibly succeed.
    /// Everything else is a property of the address or of the user's intent, and repeating it
    /// would be either useless or wrong:
    /// <list type="bullet">
    /// <item><c>Cancelled</c> - the user said stop. Retrying keeps pulling 586 MB against a
    /// direct command.</item>
    /// <item><c>UnapprovedURL</c> - a redirect left the allowlist. Retrying knocks on a
    /// disallowed host again, which is exactly what the policy forbids.</item>
    /// <item><c>HttpStatus(4xx)</c> - the address is wrong. No repetition fixes that.</item>
    /// <item><c>UnexpectedSize</c> - the source holds a different file than the manifest
    /// describes. Re-pulling it cannot change what is on the other end.</item>
    /// </list>
    /// </summary>
    private static bool IsTransient(ModelDownloadError error) => error switch
    {
        ModelDownloadError.Network => true,
        // 408 and 429 are explicit "ask again"; 5xx is the server's bad minute rather than
        // our bad address.
        ModelDownloadError.HttpStatus status =>
            status.Code is 408 or 429 or (>= 500 and <= 599),
        // Every retry downloads the file again and fills the disk again. The remedy is free
        // space, and only the person can provide it.
        ModelDownloadError.LocalWriteFailed => false,
        // The same source will serve the same bytes. The mirror is the answer here, and the
        // loop moves on to it.
        ModelDownloadError.CorruptContent => false,
        _ => false,
    };

    // MARK: - Observation

    public ModelState CurrentState()
    {
        lock (gate)
        {
            return state;
        }
    }

    /// <summary>State flow for the interface. The current value comes first.</summary>
    public async IAsyncEnumerable<ModelState> States(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        Channel<ModelState> channel = Channel.CreateUnbounded<ModelState>();
        lock (gate)
        {
            observers[id] = channel;
            channel.Writer.TryWrite(state);
        }

        try
        {
            await foreach (ModelState value in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return value;
            }
        }
        finally
        {
            lock (gate)
            {
                observers.Remove(id);
            }
        }
    }

    private void SetState(ModelState newState)
    {
        lock (gate)
        {
            state = newState;
            foreach (Channel<ModelState> observer in observers.Values)
            {
                observer.Writer.TryWrite(newState);
            }
        }
    }

    // MARK: - Checking what is already installed

    /// <summary>
    /// Inspect the disk and update the state.
    ///
    /// The installation is considered ready only if the marker matches the current manifest.
    /// Everything else is "not installed", even if the files are partially in place.
    /// </summary>
    public ModelState RefreshState()
    {
        try
        {
            RecoverInterruptedPromotion();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetState(new ModelState.RepairRequired("couldn't recover an interrupted model update"));
            return CurrentState();
        }

        SweepStaleStaging();

        if (!File.Exists(layout.ReadyMarker))
        {
            if (Directory.Exists(layout.InstalledDirectory))
            {
                SetState(new ModelState.RepairRequired("the model's ready marker is missing"));
                return CurrentState();
            }

            SetState(new ModelState.NotInstalled());
            return CurrentState();
        }

        ModelReadyMarker marker;
        try
        {
            marker = ReadReadyMarker();
            if (!marker.DescribesSameFiles(manifest))
            {
                throw new ModelStoreError.RepairRequired(
                    "the model marker is incompatible with this app version");
            }
        }
        catch (ModelStoreError error)
        {
            SetRefreshFailure(error);
            return CurrentState();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            SetState(new ModelState.RepairRequired("the model marker is damaged"));
            return CurrentState();
        }

        // The revision is the same, but the version of FluidAudio in the marker differs -
        // this is an application update on top of intact files, not corruption. Check all the
        // checksums again and rewrite the marker. There is not a byte of network here: that
        // is exactly why this check is separated from "other files".
        ModelReadyMarker verifiedMarker = marker.Matches(manifest)
            ? marker
            : new ModelReadyMarker(
                marker.Revision,
                marker.FluidAudioVersion,
                marker.FileCount,
                marker.TotalByteCount,
                marker.VerifiedAt,
                installedFiles: null);

        try
        {
            IReadOnlyList<ModelReadyMarker.InstalledFile> metadata = ValidateInstalledFiles(verifiedMarker);
            if (marker.InstalledFiles is null
                || !marker.InstalledFiles.SequenceEqual(metadata)
                || !marker.Matches(manifest))
            {
                WriteReadyMarker(metadata);
            }
        }
        catch (ModelStoreError error)
        {
            SetRefreshFailure(error);
            return CurrentState();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetState(new ModelState.RepairRequired(error.Message));
            return CurrentState();
        }

        SetState(new ModelState.Ready(layout.InstalledDirectory));
        return CurrentState();
    }

    private void SetRefreshFailure(ModelStoreError error)
    {
        if (error is ModelStoreError.RepairRequired repair)
        {
            SetState(new ModelState.RepairRequired(repair.Reason));
        }
        else
        {
            SetState(new ModelState.Failed(error));
        }
    }

    /// <summary>
    /// Check the exact inventory and the sizes. If the metadata changed, or if the marker is
    /// of the old format, SHA-256 is confirmed before Ready.
    /// </summary>
    private IReadOnlyList<ModelReadyMarker.InstalledFile> ValidateInstalledFiles(ModelReadyMarker marker)
    {
        var expectedPaths = manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        string engine = layout.EngineDirectory;
        if (!Directory.Exists(engine))
        {
            throw new ModelStoreError.RepairRequired("the model folder is missing");
        }

        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(engine, "*", SearchOption.AllDirectories);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ModelStoreError.RepairRequired("couldn't read the model files");
        }

        var actualPaths = new HashSet<string>(StringComparer.Ordinal);
        var metadata = new List<ModelReadyMarker.InstalledFile>();
        Dictionary<string, ModelReadyMarker.InstalledFile> previous =
            (marker.InstalledFiles ?? []).ToDictionary(file => file.Path, StringComparer.Ordinal);

        foreach (string entry in entries)
        {
            var info = new FileInfo(entry);
            if (info.LinkTarget is not null)
            {
                throw new ModelStoreError.RepairRequired($"{Path.GetFileName(entry)} is a symbolic link");
            }

            if ((info.Attributes & FileAttributes.Directory) != 0)
            {
                continue;
            }

            // Finder creates `.DS_Store` when simply browsing a folder. That is its litter,
            // not a model file: remove it and continue, because repair here would mean
            // transferring hundreds of megabytes because of an open window.
            if (Path.GetFileName(entry) == ".DS_Store")
            {
                File.Delete(entry);
                continue;
            }

            string relative = Path.GetRelativePath(engine, entry).Replace('\\', '/');
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                throw new ModelStoreError.RepairRequired("a model file escaped the install directory");
            }

            actualPaths.Add(relative);

            ModelManifest.File? expected = manifest.Files
                .FirstOrDefault(file => string.Equals(file.Path, relative, StringComparison.Ordinal));
            if (expected is null)
            {
                throw new ModelStoreError.RepairRequired($"unexpected file {relative}");
            }

            long byteCount = info.Length;
            if (byteCount != expected.ByteCount)
            {
                throw new ModelStoreError.RepairRequired($"wrong size for {relative}");
            }

            var item = new ModelReadyMarker.InstalledFile(
                relative,
                byteCount,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
            if (!previous.TryGetValue(relative, out ModelReadyMarker.InstalledFile? before) || before != item)
            {
                try
                {
                    verifier.Verify(expected, entry);
                }
                catch (ModelVerifier.Failure)
                {
                    throw new ModelStoreError.RepairRequired($"{relative} is damaged");
                }
            }

            metadata.Add(item);
        }

        if (!actualPaths.SetEquals(expectedPaths))
        {
            string missing = string.Join(", ", expectedPaths.Except(actualPaths).Order(StringComparer.Ordinal));
            throw new ModelStoreError.RepairRequired($"missing files: {missing}");
        }

        return metadata.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Remove abandoned staging directories from aborted attempts.
    ///
    /// While an installation is in progress, touch nothing: the disk inspection is called
    /// from the interface and easily lands in the middle of a download - otherwise it would
    /// delete the folder the installation writes into, and that would fall over mid-process.
    /// </summary>
    /// <summary>
    /// After a new revision is in place, the previous ones are only disk: a model folder holds
    /// one revision at a time by design, and a change of manifest that left 640 MB of the old
    /// export beside the new one for every person who updated is exactly the kind of quiet
    /// cost nobody would ever find. Only after a successful promotion, and only the siblings -
    /// the staging and backup names have their own sweeps and their own rules.
    /// </summary>
    private void SweepOtherRevisions()
    {
        string[] entries;
        try
        {
            entries = Directory.GetDirectories(layout.ModelDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (string entry in entries)
        {
            string name = Path.GetFileName(entry);
            if (name.StartsWith('.') || string.Equals(name, layout.Revision, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                Directory.Delete(entry, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Left behind, not hidden: the next successful install tries again.
            }
        }
    }

    private void SweepStaleStaging()
    {
        lock (gate)
        {
            if (activeTask is not null)
            {
                return;
            }
        }

        string[] entries;
        try
        {
            entries = Directory.GetDirectories(layout.ModelDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (string entry in entries)
        {
            if (!Path.GetFileName(entry).StartsWith(".staging-", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                Directory.Delete(entry, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // The next launch tries again; a stale staging folder harms nothing.
            }
        }
    }

    /// <summary>Bring a promotion interrupted between renames to a consistent state.</summary>
    private void RecoverInterruptedPromotion()
    {
        string destination = layout.InstalledDirectory;
        string backup = layout.BackupDirectory;
        if (!Directory.Exists(backup))
        {
            return;
        }

        bool destinationHasMarker = File.Exists(
            Path.Combine(destination, ModelInstallLayout.ReadyMarkerName));
        if (destinationHasMarker)
        {
            // The new model is in place and marked ready, so the backup is leftover
            // housekeeping. Failing to delete it is not a broken install, and treating it as
            // one used to offer the person exactly one remedy - redownload 483 MB - which
            // lands in the same state.
            try
            {
                Directory.Delete(backup, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Left behind, not hidden.
            }

            return;
        }

        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, recursive: true);
        }

        Directory.Move(backup, destination);
    }

    // MARK: - Installation

    /// <summary>Download and install the model. Repeated calls while it runs are ignored.</summary>
    public async Task Install()
    {
        await RunExclusively(
            skipWhenReady: true,
            token => RunInstall(staging => DownloadAll(staging, token), token))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Cancel the installation. Anything downloaded into staging is deleted - we do not keep
    /// untrusted remains.
    /// </summary>
    public void CancelInstall()
    {
        lock (gate)
        {
            activeCancellation?.Cancel();
        }
    }

    /// <summary>
    /// Explicit repair chosen by the user. Unlike <see cref="Install"/>, it does not trust a
    /// previous Ready: an engine warm-up may have found a defect that a file inventory cannot
    /// diagnose. The old copy is still held by the promotion until a fully checked new one is
    /// in place.
    /// </summary>
    public async Task Repair()
    {
        await RunExclusively(
            skipWhenReady: false,
            token => RunInstall(staging => DownloadAll(staging, token), token))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Take the model from a ready-made folder without going online.
    ///
    /// A second independent path to the model: a person downloaded it on another machine or
    /// received it from a colleague. The check is exactly the same as after a download - all
    /// the SHA-256 sums from the manifest - so trusting the folder is not required.
    ///
    /// The folder is expected to hold the manifest paths directly: <c>Encoder.mlmodelc/…</c>,
    /// <c>parakeet_vocab.json</c> and so on. That is the same folder
    /// <see cref="ModelInstallLayout.EngineDirectory"/> gives on a machine where the model is
    /// already installed.
    /// </summary>
    /// <param name="directory">The folder holding the manifest's files.</param>
    /// <param name="force">
    /// Import even over a copy the store believes ready - the import counterpart of
    /// <see cref="Repair"/>, for a bundled model that the engine has just rejected.
    /// </param>
    public async Task ImportModel(string directory, bool force = false)
    {
        await RunExclusively(
            skipWhenReady: !force,
            token => RunInstall(
                staging =>
                {
                    CopyAll(directory, staging, token);
                    return Task.CompletedTask;
                },
                token))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// One long operation at a time, and the caller waits for it - the actor's job in the
    /// Swift original.
    /// </summary>
    private async Task RunExclusively(bool skipWhenReady, Func<CancellationToken, Task> work)
    {
        var handle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource cancellation;
        lock (gate)
        {
            if (activeTask is not null)
            {
                return;
            }

            if (skipWhenReady && state is ModelState.Ready)
            {
                return;
            }

            cancellation = new CancellationTokenSource();
            activeCancellation = cancellation;
            activeTask = handle.Task;
        }

        try
        {
            await work(cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                activeTask = null;
                activeCancellation = null;
            }

            cancellation.Dispose();
            handle.SetResult();
        }
    }

    /// <summary>
    /// The general skeleton of an installation: assemble into staging, check, move.
    ///
    /// Downloading and importing differ only in where the files come from. Everything else -
    /// checking the sums, the atomic move, cleaning up after oneself on any error - must
    /// match, otherwise one of the two paths sooner or later becomes weaker than the other.
    /// </summary>
    private async Task RunInstall(Func<string, Task> collect, CancellationToken cancellationToken)
    {
        var attempt = Guid.NewGuid();
        string staging = layout.StagingDirectory(attempt);

        try
        {
            PreflightDiskSpace();
            Directory.CreateDirectory(staging);

            await collect(staging).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            VerifyAll(staging, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Promote(staging);

            ExcludeFromBackup(layout.ModelDirectory);
            SetState(new ModelState.Ready(layout.InstalledDirectory));
            SweepOtherRevisions();
        }
        catch (OperationCanceledException)
        {
            try
            {
                RemoveStaging(staging);
                SetState(new ModelState.NotInstalled());
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                SetState(new ModelState.Failed(new ModelStoreError.Install(
                    $"download cancelled, but the partial files weren't deleted: {error.Message}")));
            }
        }
        catch (ModelStoreError error)
        {
            string primary = error.Message;
            try
            {
                RemoveStaging(staging);
                SetState(new ModelState.Failed(error));
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                SetState(new ModelState.Failed(new ModelStoreError.Install(
                    $"{primary}; staging wasn't deleted: {cleanup.Message}")));
            }
        }
        catch (Exception error)
        {
            string primary = error.Message;
            try
            {
                RemoveStaging(staging);
                SetState(new ModelState.Failed(new ModelStoreError.Install(primary)));
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                SetState(new ModelState.Failed(new ModelStoreError.Install(
                    $"{primary}; staging wasn't deleted: {cleanup.Message}")));
            }
        }
    }

    private static void RemoveStaging(string staging)
    {
        if (!Directory.Exists(staging))
        {
            return;
        }

        Directory.Delete(staging, recursive: true);
    }

    /// <summary>
    /// Copy the manifest files from someone else's folder into staging.
    ///
    /// We copy rather than move: the folder belongs to someone else, files may not be taken
    /// out of it.
    /// </summary>
    private void CopyAll(string source, string staging, CancellationToken cancellationToken)
    {
        long total = manifest.TotalByteCount;
        long completed = 0;
        SetState(new ModelState.Downloading(0, total));

        foreach (ModelManifest.File file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The path barrier is the same and just as strict: a file that arrived from
            // outside has no more trust than a downloaded one. Both sides are checked: where
            // we take it from and where we put it.
            string origin;
            string destination;
            try
            {
                origin = layout.Destination(file, source);
                destination = layout.Destination(file, layout.EngineDirectoryInside(staging));
            }
            catch (ModelInstallError)
            {
                throw new ModelStoreError.ImportSource($"the path {file.Path} leads outside the folder");
            }

            CheckImportable(origin, file.Path);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            try
            {
                File.Copy(origin, destination, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new ModelStoreError.ImportSource($"couldn't copy {file.Path}: {error.Message}");
            }

            completed += file.ByteCount;
            SetState(new ModelState.Downloading(completed, total));
        }
    }

    /// <summary>
    /// Is the file from someone else's folder suitable for copying?
    ///
    /// A symbolic link is rejected separately: its content can be replaced after the sum is
    /// checked, and the installed model would hold a pointer outside instead of a file.
    /// </summary>
    private static void CheckImportable(string path, string manifestPath)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new ModelStoreError.ImportSource($"the folder has no file {manifestPath}");
        }

        if (info.LinkTarget is not null)
        {
            throw new ModelStoreError.ImportSource(
                $"{manifestPath} is a symbolic link, but a regular file is required");
        }

        if ((info.Attributes & FileAttributes.Directory) != 0)
        {
            throw new ModelStoreError.ImportSource($"{manifestPath} is not a regular file");
        }
    }

    /// <summary>
    /// Make sure there is enough space: files first land in staging and are moved afterwards,
    /// so the peak reserve needed is a little larger than the model itself.
    /// </summary>
    private void PreflightDiskSpace()
    {
        long required = (long)(manifest.TotalByteCount * 1.2);
        long available;
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(layout.Root));
            if (root is null)
            {
                return;
            }

            available = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // The volume did not answer; the install still deserves its try.
            return;
        }

        if (available <= required)
        {
            throw new ModelStoreError.NotEnoughDiskSpace(required, available);
        }
    }

    private async Task DownloadAll(string staging, CancellationToken cancellationToken)
    {
        long total = manifest.TotalByteCount;
        long completed = 0;
        SetState(new ModelState.Downloading(0, total));

        foreach (ModelManifest.File file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<Uri> sources = manifest.DownloadURLs(file);
            if (sources.Count == 0)
            {
                throw new ModelStoreError.Manifest($"couldn't build a URL for {file.Path}");
            }

            string destination = layout.Destination(file, layout.EngineDirectoryInside(staging));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            long alreadyDone = completed;
            await DownloadFromAnySource(sources, file, destination, alreadyDone, total, cancellationToken)
                .ConfigureAwait(false);
            completed += file.ByteCount;
            SetState(new ModelState.Downloading(completed, total));
        }
    }

    /// <summary>
    /// Walk the addresses from top to bottom until the file is downloaded.
    ///
    /// A source failing and the user cancelling are two different things. No file, no access,
    /// no host - a reason to take the next address. A cancel is a reason to stop completely:
    /// walking the mirrors after "cancel" means continuing to download 483 MB despite a
    /// direct command.
    /// </summary>
    private async Task DownloadFromAnySource(
        IReadOnlyList<Uri> sources,
        ModelManifest.File file,
        string destination,
        long alreadyDone,
        long total,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        // Corruption is a different verdict from "could not download". If every source hands
        // us bad bytes, the honest word for that is a failed check, not a failed transfer -
        // and the UI tells the two apart.
        var corruptions = new List<string>();

        for (int index = 0; index < sources.Count; index++)
        {
            Uri url = sources[index];
            ModelDownloadError? lastError = null;

            for (int attempt = 1; attempt <= AttemptsPerSource; attempt++)
            {
                try
                {
                    string temporary = await downloader.Download(
                        url,
                        file.ByteCount,
                        received => ReportDownloadProgress(alreadyDone + received, total),
                        cancellationToken)
                        .ConfigureAwait(false);

                    // Checked here, and not only in the final pass, so that a source serving
                    // corrupt bytes hands over to the mirror. Content corruption is the one
                    // failure SHA-256 exists to catch, and before this it aborted the whole
                    // install and sent the user back to the same bad source. The manifest
                    // promises the mirror means a single source is not a single point of
                    // failure; for corrupt content it provided none.
                    //
                    // Check and place without letting go of the file in between. These used
                    // to be in different scopes, and the gap cost a whole install once: a
                    // verified temporary vanished before the caller could move it, and a four
                    // minute download died with no way back.
                    try
                    {
                        verifier.Verify(file, temporary);
                    }
                    catch (ModelVerifier.Failure failure)
                    {
                        TryDelete(temporary);
                        throw new ModelDownloadError.CorruptContent(failure.Message);
                    }

                    try
                    {
                        File.Move(temporary, destination, overwrite: true);
                        return;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        TryDelete(temporary);
                        // Placing it is a local operation, so a failure here is this
                        // machine's, not the source's - but unlike a full disk it is worth
                        // one more try, because what we saw was a file going missing rather
                        // than a volume filling up.
                        throw new ModelDownloadError.Network(
                            $"couldn't store {file.Path}: {error.Message}");
                    }
                }
                catch (ModelDownloadError.Cancelled)
                {
                    throw new OperationCanceledException();
                }
                catch (ModelDownloadError.LocalWriteFailed local)
                {
                    // The disk is full no matter which mirror the bytes come from. Walking to
                    // the next source would download the file a second time and fail in
                    // exactly the same place.
                    throw new ModelStoreError.Download($"{file.Path}: {local.Reason}");
                }
                catch (ModelDownloadError error)
                {
                    lastError = error;
                    // Progress could move forward on an unsuccessful attempt - put it back,
                    // otherwise the indicator overtakes itself.
                    ReportDownloadProgress(alreadyDone, total);

                    if (!IsTransient(error) || attempt >= AttemptsPerSource)
                    {
                        break;
                    }

                    // Cancellation during the pause must still stop us: sleeping through a
                    // cancel would delay the stop by the whole delay.
                    await Task.Delay(RetryDelay(retryDelay, attempt), cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (lastError is not null)
            {
                failures.Add($"{url.Host}: {Describe(lastError)}");
                if (lastError is ModelDownloadError.CorruptContent corrupt)
                {
                    corruptions.Add(corrupt.Reason);
                }
            }

            if (index == sources.Count - 1)
            {
                // Corruption outranks "could not download" because it is the more specific
                // verdict: the bytes arrived and were wrong. A source that was merely absent
                // says nothing about the file, while a source that served damaged content
                // says a great deal. The full list still travels with it, so no detail is
                // lost.
                if (corruptions.Count != 0)
                {
                    throw new ModelStoreError.Verification(
                        $"{file.Path}: {string.Join("; ", failures)}");
                }

                throw new ModelStoreError.Download($"{file.Path}: {string.Join("; ", failures)}");
            }
        }

        throw new ModelStoreError.Download($"{file.Path}: no sources left");
    }

    /// <summary>Backoff that grows, so a source under load is not hit at a fixed beat.</summary>
    private static TimeSpan RetryDelay(TimeSpan basis, int attempt) => basis * attempt;

    /// <summary>
    /// A description that survives the error type.
    ///
    /// Swift's <c>localizedDescription</c> flattens an enum into "error 3", which tells
    /// nobody anything, so it uses <c>String(describing:)</c> instead. The case name plus its
    /// payload is exactly what a person or a bug report needs - here that is the exception's
    /// own name and message.
    /// </summary>
    private static string Describe(Exception error) => $"{error.GetType().Name}({error.Message})";

    private void ReportDownloadProgress(long received, long total)
    {
        lock (gate)
        {
            if (state is not ModelState.Downloading)
            {
                return;
            }
        }

        SetState(new ModelState.Downloading(received, total));
    }

    private void VerifyAll(string staging, CancellationToken cancellationToken)
    {
        SetState(new ModelState.Verifying(0, manifest.Files.Count));
        try
        {
            for (int index = 0; index < manifest.Files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ModelManifest.File file = manifest.Files[index];
                string destination = layout.Destination(file, layout.EngineDirectoryInside(staging));
                verifier.Verify(file, destination);
                SetState(new ModelState.Verifying(index + 1, manifest.Files.Count));
            }
        }
        catch (ModelVerifier.Failure failure)
        {
            throw new ModelStoreError.Verification(Describe(failure));
        }
    }

    /// <summary>Crash-safe swap: the old model stays in the backup until the new one is marked.</summary>
    private void Promote(string staging)
    {
        string destination = layout.InstalledDirectory;
        string backup = layout.BackupDirectory;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }

            if (Directory.Exists(destination))
            {
                Directory.Move(destination, backup);
            }

            InjectPromotionFailure(ModelPromotionCheckpoint.AfterBackup);
            Directory.Move(staging, destination);
            InjectPromotionFailure(ModelPromotionCheckpoint.AfterStagingMove);
            IReadOnlyList<ModelReadyMarker.InstalledFile> metadata = InstalledMetadataWithoutMarker();
            WriteReadyMarker(metadata);
            InjectPromotionFailure(ModelPromotionCheckpoint.AfterReadyMarker);
        }
        catch (ModelStoreError error)
        {
            throw new ModelStoreError.Install(error.Message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ModelStoreError.Install(error.Message);
        }

        // Past this line the model IS installed: the files are in place and the ready marker
        // is written. Removing the old copy is housekeeping, and failing at housekeeping must
        // not be reported as a failed install.
        //
        // It used to be inside the same block, so a backup that would not delete - a stray
        // lock, a permissions oddity - turned a finished install into a failure, skipped the
        // engine warm-up, and on the next refresh became "repair required", whose only
        // offered remedy is downloading 483 MB again into the very same situation.
        try
        {
            InjectPromotionFailure(ModelPromotionCheckpoint.AfterBackupRemoval);
            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }
        }
        catch (Exception error) when (error is ModelStoreError or IOException or UnauthorizedAccessException)
        {
            // Left behind, not hidden: the next install reclaims it, and
            // `RecoverInterruptedPromotion` knows how to read this state.
        }
    }

    private void InjectPromotionFailure(ModelPromotionCheckpoint checkpoint)
    {
        if (injectedPromotionFailure != checkpoint)
        {
            return;
        }

        throw new ModelStoreError.Install($"injected promotion failure: {checkpoint}");
    }

    private IReadOnlyList<ModelReadyMarker.InstalledFile> InstalledMetadataWithoutMarker() =>
        manifest.Files
            .Select(file =>
            {
                var info = new FileInfo(layout.Destination(file, layout.EngineDirectory));
                return new ModelReadyMarker.InstalledFile(
                    file.Path,
                    info.Length,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
            })
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Explicit options, so the marker on disk does not change shape with a serializer
    /// default. It is written and read only by this application.
    /// </summary>
    internal static readonly JsonSerializerOptions MarkerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private ModelReadyMarker ReadReadyMarker()
    {
        byte[] data = File.ReadAllBytes(layout.ReadyMarker);
        return JsonSerializer.Deserialize<ModelReadyMarker>(data, MarkerOptions)
            ?? throw new ModelStoreError.RepairRequired("the model marker is damaged");
    }

    private void WriteReadyMarker(IReadOnlyList<ModelReadyMarker.InstalledFile> installedFiles)
    {
        var marker = new ModelReadyMarker(manifest, DateTimeOffset.UtcNow, installedFiles);
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(marker, MarkerOptions);
        // Swift writes with `.atomic`; here that is a temporary file plus a replace, which is
        // the same promise: a reader never sees half a marker.
        string temporary = layout.ReadyMarker + ".partial";
        File.WriteAllBytes(temporary, data);
        File.Move(temporary, layout.ReadyMarker, overwrite: true);
    }

    /// <summary>
    /// The model is a downloadable cache; it has no place in backups.
    /// </summary>
    /// <remarks>
    /// Swift sets <c>isExcludedFromBackup</c>, a Time Machine flag. Windows has no
    /// per-directory equivalent - File History follows libraries, not marks - so this stays
    /// deliberately empty rather than pretending to do something.
    /// </remarks>
    private static void ExcludeFromBackup(string directory)
    {
        _ = directory;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A leftover in the temporary directory must not fail an install.
        }
    }

    // MARK: - Delete

    /// <summary>
    /// Delete the installed model.
    ///
    /// If a download is going on at this moment, stop it first and wait for the end.
    /// Otherwise deletion would pull the folder out from under a live installation: it would
    /// keep writing into nowhere and end with a vague error instead of a clear "model
    /// deleted".
    /// </summary>
    public async Task Delete()
    {
        Task? running;
        lock (gate)
        {
            running = activeTask;
            activeCancellation?.Cancel();
        }

        if (running is not null)
        {
            await running.ConfigureAwait(false);
        }

        SetState(new ModelState.Deleting());
        try
        {
            if (Directory.Exists(layout.ModelDirectory))
            {
                Directory.Delete(layout.ModelDirectory, recursive: true);
            }

            SetState(new ModelState.NotInstalled());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetState(new ModelState.Failed(
                new ModelStoreError.Install($"couldn't delete the model: {error.Message}")));
        }
    }
}
