using System.Security.Cryptography;

namespace LocalASR;

/// <summary>
/// Checking downloaded files against the manifest.
///
/// Sums are counted streamwise: the encoder file weighs 446 MB, and keeping it in memory
/// whole is entirely unnecessary.
/// </summary>
public sealed class ModelVerifier
{
    /// <summary>Chunk size when reading. Tradeoff between number of system calls and memory.</summary>
    private const int ChunkSize = 4 * 1024 * 1024;

    /// <remarks>
    /// The Swift original is an <c>Error</c> enum with associated values; here the cases are
    /// nested exception types so that <c>throw</c> stays honest.
    /// </remarks>
    public abstract class Failure : Exception
    {
        private protected Failure(string message)
            : base(message)
        {
        }

        public sealed class FileMissing : Failure
        {
            public FileMissing(string path)
                : base($"file missing: {path}")
            {
                Path = path;
            }

            public string Path { get; }
        }

        public sealed class SizeMismatch : Failure
        {
            public SizeMismatch(string path, long expected, long actual)
                : base($"size mismatch for {path}: expected {expected}, got {actual}")
            {
                Path = path;
                Expected = expected;
                Actual = actual;
            }

            public string Path { get; }

            public long Expected { get; }

            public long Actual { get; }
        }

        public sealed class ChecksumMismatch : Failure
        {
            public ChecksumMismatch(string path)
                : base($"checksum mismatch: {path}")
            {
                Path = path;
            }

            public string Path { get; }
        }

        public sealed class Unreadable : Failure
        {
            public Unreadable(string path, string reason)
                : base($"{path} is unreadable: {reason}")
            {
                Path = path;
                Reason = reason;
            }

            public string Path { get; }

            public string Reason { get; }
        }
    }

    /// <summary>Check one file: first the size (cheap), then the sum.</summary>
    public void Verify(ModelManifest.File file, string path)
    {
        if (!System.IO.File.Exists(path))
        {
            throw new Failure.FileMissing(file.Path);
        }

        long actualSize;
        try
        {
            actualSize = new FileInfo(path).Length;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new Failure.Unreadable(file.Path, error.Message);
        }

        if (actualSize != file.ByteCount)
        {
            throw new Failure.SizeMismatch(file.Path, file.ByteCount, actualSize);
        }

        string digest = Sha256(path, file.Path);
        if (!string.Equals(digest, file.Sha256.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new Failure.ChecksumMismatch(file.Path);
        }
    }

    /// <summary>
    /// Check the entire set. Stops at the first problem: there is no point in continuing,
    /// the installation will not take place anyway.
    /// </summary>
    public void VerifyAll(
        ModelManifest manifest,
        string directory,
        ModelInstallLayout layout,
        Action<int, int>? progress = null)
    {
        for (int index = 0; index < manifest.Files.Count; index++)
        {
            ModelManifest.File file = manifest.Files[index];
            string destination = layout.Destination(file, directory);
            Verify(file, destination);
            progress?.Invoke(index + 1, manifest.Files.Count);
        }
    }

    /// <summary>Streaming SHA-256, lower case hex.</summary>
    public string Sha256(string path, string manifestPath)
    {
        FileStream handle;
        try
        {
            handle = new FileStream(path, FileMode.Open, FileAccess.Read);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new Failure.Unreadable(manifestPath, error.Message);
        }

        using (handle)
        {
            using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] chunk = new byte[ChunkSize];
            while (true)
            {
                int read;
                try
                {
                    read = handle.Read(chunk, 0, chunk.Length);
                }
                catch (IOException error)
                {
                    throw new Failure.Unreadable(manifestPath, error.Message);
                }

                if (read <= 0)
                {
                    break;
                }

                hasher.AppendData(chunk, 0, read);
            }

            return Convert.ToHexStringLower(hasher.GetHashAndReset());
        }
    }
}
