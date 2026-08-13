namespace DictationCore;

/// <summary>
/// Reading a file from disk.
///
/// Exists so that reading by arbitrary URL does not occur in the project. Such a read accepts
/// any URL, and an http address will silently go online - which means an automatic
/// check of the network surface would not be able to distinguish between reading settings and
/// an undeclared data send. Here the address must be a file one.
/// </summary>
public static class LocalFile
{
    /// <summary>
    /// Reading failures. The Swift original is an <c>Error</c> enum with associated
    /// values; in C# the cases are nested exception types, so <c>throw</c> stays honest.
    /// </summary>
    public abstract class Failure : Exception
    {
        private protected Failure(string message)
            : base(message)
        {
        }

        /// <summary>The address does not point at a local file.</summary>
        public sealed class NotAFileURL : Failure
        {
            public NotAFileURL(string scheme)
                : base($"not a file URL: {scheme}")
            {
                Scheme = scheme;
            }

            public string Scheme { get; }
        }

        /// <summary>The file exists as an address but could not be read.</summary>
        public sealed class Unreadable : Failure
        {
            public Unreadable(string reason)
                : base(reason)
            {
                Reason = reason;
            }

            public string Reason { get; }
        }
    }

    /// <summary>Read the entire local file.</summary>
    public static byte[] Read(Uri url)
    {
        if (!url.IsAbsoluteUri)
        {
            throw new Failure.NotAFileURL("no scheme");
        }

        if (!url.IsFile)
        {
            throw new Failure.NotAFileURL(url.Scheme);
        }

        try
        {
            return File.ReadAllBytes(url.LocalPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new Failure.Unreadable(error.Message);
        }
    }
}
