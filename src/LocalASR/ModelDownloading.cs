using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace LocalASR;

/// <summary>
/// Download one file.
///
/// A port exists for the sake of the tests: the real network does not take part in them, but
/// the entire installation logic - states, checks, atomic move - is checked against a dummy
/// downloader.
/// </summary>
public interface IModelDownloading
{
    /// <summary>
    /// Download the file at the address to a temporary location and return the path to it.
    /// </summary>
    /// <param name="onProgress">How many bytes of this file have already been received.</param>
    Task<string> Download(
        Uri url,
        long expectedBytes,
        Action<long> onProgress,
        CancellationToken cancellationToken = default);
}

/// <remarks>
/// The Swift original is an <c>Error</c> enum with associated values; here the cases are
/// nested exception types so that <c>throw</c> stays honest.
/// </remarks>
public abstract class ModelDownloadError : Exception
{
    private protected ModelDownloadError(string message)
        : base(message)
    {
    }

    public sealed class Network : ModelDownloadError
    {
        public Network(string reason)
            : base(reason)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }

    public sealed class HttpStatus : ModelDownloadError
    {
        public HttpStatus(int code)
            : base($"HTTP {code}")
        {
            Code = code;
        }

        public int Code { get; }
    }

    public sealed class UnapprovedURL : ModelDownloadError
    {
        public UnapprovedURL(string host)
            : base($"unapproved address: {host}")
        {
            Host = host;
        }

        public string Host { get; }
    }

    public sealed class UnexpectedSize : ModelDownloadError
    {
        public UnexpectedSize(long expected, long actual)
            : base($"unexpected size: expected {expected}, got {actual}")
        {
            Expected = expected;
            Actual = actual;
        }

        public long Expected { get; }

        public long Actual { get; }
    }

    /// <summary>
    /// The bytes arrived but this machine could not keep them.
    ///
    /// Separate from <see cref="Network"/> because the remedy is the opposite one. A network
    /// failure is worth another attempt; a full disk is not - every retry pulls the file
    /// again and fills the disk again. Before this case existed a disk that filled at 90% of
    /// the 425 MB encoder cost six attempts and roughly 2.7 GB of downloads before failing,
    /// and the user was told the network was at fault.
    /// </summary>
    public sealed class LocalWriteFailed : ModelDownloadError
    {
        public LocalWriteFailed(string reason)
            : base(reason)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }

    /// <summary>
    /// The bytes arrived intact in size but wrong in content.
    ///
    /// Raised by the store, not the downloader: only the store knows the manifest checksum.
    /// It lives here so the source-walking loop can treat it as a reason to try the mirror,
    /// which is exactly what the mirror is for.
    /// </summary>
    public sealed class CorruptContent : ModelDownloadError
    {
        public CorruptContent(string reason)
            : base(reason)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }

    public sealed class Cancelled : ModelDownloadError
    {
        public Cancelled()
            : base("cancelled")
        {
        }
    }
}

/// <summary>Clean network surface policy for the model download.</summary>
public sealed class ModelDownloadPolicy
{
    public bool Allows(Uri url)
    {
        if (!string.Equals(url.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string host = url.Host.ToLowerInvariant();
        return host == "huggingface.co"
            || host.EndsWith(".huggingface.co", StringComparison.Ordinal)
            || host == "hf.co"
            || host.EndsWith(".hf.co", StringComparison.Ordinal)
            || host == "github.com"
            || host == "objects.githubusercontent.com"
            || host == "release-assets.githubusercontent.com";
    }

    public void Validate(Uri url)
    {
        if (!Allows(url))
        {
            throw new ModelDownloadError.UnapprovedURL(
                url.Host.Length > 0 ? url.Host : url.AbsoluteUri);
        }
    }
}

/// <summary>
/// Downloader on top of <see cref="HttpClient"/> (Swift: <c>URLSessionModelDownloader</c>).
///
/// The only place in the project - together with the updater - where a network call is
/// allowed at all. This is checked in CI.
///
/// The file is streamed to disk in large chunks and never held in memory: the encoder alone
/// is 445 MB.
/// </summary>
public sealed class HttpClientModelDownloader : IModelDownloading, IDisposable
{
    private readonly HttpClient client;
    private readonly ModelDownloadPolicy policy;

    /// <summary>
    /// The user explicitly starts this large download after seeing its size, so the whole
    /// transfer - not one request - gets an hour. Swift sets
    /// <c>timeoutIntervalForResource = 3600</c> for the same reason.
    /// </summary>
    internal static readonly TimeSpan ResourceTimeout = TimeSpan.FromHours(1);

    public HttpClientModelDownloader(
        HttpMessageHandler? handler = null,
        ModelDownloadPolicy? policy = null)
    {
        this.policy = policy ?? new ModelDownloadPolicy();
        client = new HttpClient(
            handler ?? new SocketsHttpHandler
            {
                // Redirects are followed by hand below so that every hop can be held
                // against the allowlist. Letting the stack follow them would take the
                // policy out of the loop exactly where it matters.
                AllowAutoRedirect = false,
            },
            disposeHandler: true)
        {
            Timeout = ResourceTimeout,
        };
    }

    public void Dispose() => client.Dispose();

    /// <summary>
    /// Did this fail because the bytes could not be written here?
    ///
    /// A dropped connection and a full disk both surface as exceptions, and telling them
    /// apart is the only way to stop retrying a full disk. Swift reads NSError domains; here
    /// the network exceptions are the ones carrying an <see cref="HttpRequestException"/> or
    /// a <see cref="SocketException"/>, and everything else that fails around a file handle
    /// is this machine's problem.
    /// </summary>
    internal static bool IsLocalWriteFailure(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException or SocketException or WebException)
            {
                return false;
            }
        }

        return error is IOException or UnauthorizedAccessException;
    }

    /// <summary>
    /// How many times a dropped transfer is picked up again.
    ///
    /// Only ever used when bytes were actually on disk. Blind re-dialling belongs to the
    /// layer above, which also knows about the mirror.
    /// </summary>
    private const int ResumeAttempts = 3;

    /// <summary>How many redirects one address may take before we call it a loop.</summary>
    private const int MaximumRedirects = 5;

    public async Task<string> Download(
        Uri url,
        long expectedBytes,
        Action<long> onProgress,
        CancellationToken cancellationToken = default)
    {
        policy.Validate(url);
        if (expectedBytes <= 0)
        {
            throw new ModelDownloadError.UnexpectedSize(expectedBytes, 0);
        }

        string destination = Path.Combine(Path.GetTempPath(), $"wai-model-{Guid.NewGuid():D}");

        // The encoder is 445 MB. Measured on a real flaky link: the transfer reached 415 MB,
        // dropped, and the retry above started from zero - throwing away six minutes of
        // download and, on a link that drops regularly, never finishing at all. Resuming
        // from the bytes already on disk lets the next attempt continue instead of starting
        // from nothing.
        long received = 0;
        Exception lastError = new ModelDownloadError.Network("the download never started");

        for (int attempt = 1; attempt <= ResumeAttempts; attempt++)
        {
            try
            {
                await Attempt(url, expectedBytes, destination, received, onProgress, cancellationToken)
                    .ConfigureAwait(false);
                return destination;
            }
            catch (ResumableFailure failure)
            {
                lastError = failure.Underlying;
                received = failure.ReceivedBytes;
                // Nothing on disk to continue from. Re-dialling here would duplicate the
                // retry above without saving a single byte, so hand the failure over.
                if (!failure.CanResume)
                {
                    TryRemove(destination);
                    throw failure.Underlying;
                }
            }
        }

        TryRemove(destination);
        throw lastError;
    }

    /// <summary>One transfer: either from scratch, or continuing a dropped one.</summary>
    private async Task Attempt(
        Uri url,
        long expectedBytes,
        string destination,
        long resumeFrom,
        Action<long> onProgress,
        CancellationToken cancellationToken)
    {
        long received = resumeFrom;
        HttpResponseMessage? response = null;
        try
        {
            response = await Send(url, resumeFrom, cancellationToken).ConfigureAwait(false);

            // A response carrying an error also arrives as a body - with an error page
            // inside. Without checking the status it would go into the installation and
            // fail the checksum, but for an unclear reason.
            if (!response.IsSuccessStatusCode)
            {
                throw Terminal(new ModelDownloadError.HttpStatus((int)response.StatusCode), received);
            }

            bool resumed = resumeFrom > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (resumeFrom > 0 && !resumed)
            {
                // The server ignored the range: start the file over rather than glue two
                // different offsets together.
                received = 0;
            }

            long? declared = response.Content.Headers.ContentLength;
            if (declared is { } length && length >= 0 && length + received != expectedBytes)
            {
                throw Terminal(
                    new ModelDownloadError.UnexpectedSize(expectedBytes, length + received),
                    received);
            }

            await using Stream body = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            FileStream file;
            try
            {
                file = new FileStream(
                    destination,
                    received > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write);
            }
            catch (Exception error) when (IsLocalWriteFailure(error))
            {
                throw Terminal(new ModelDownloadError.LocalWriteFailed(error.Message), received);
            }

            await using (file)
            {
                byte[] buffer = new byte[81920];
                while (true)
                {
                    int read;
                    try
                    {
                        read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // A cancel is the user's, and it carries nothing forward:
                        // continuing later would be continuing against their command.
                        throw Terminal(new ModelDownloadError.Cancelled(), received);
                    }
                    catch (Exception error)
                    {
                        throw Resumable(new ModelDownloadError.Network(error.Message), received);
                    }

                    if (read <= 0)
                    {
                        break;
                    }

                    if (received + read > expectedBytes)
                    {
                        throw Terminal(
                            new ModelDownloadError.UnexpectedSize(expectedBytes, received + read),
                            received);
                    }

                    try
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw Terminal(new ModelDownloadError.Cancelled(), received);
                    }
                    catch (Exception error)
                    {
                        // The download succeeded and this machine could not store it - a
                        // full disk, not a bad connection.
                        throw Terminal(new ModelDownloadError.LocalWriteFailed(error.Message), received);
                    }

                    received += read;
                    onProgress(received);
                }
            }

            long actual = new FileInfo(destination).Length;
            if (actual != expectedBytes)
            {
                // A body that ended cleanly but short is the source holding a different
                // file, not a dropped link: repeating it changes nothing.
                throw Terminal(new ModelDownloadError.UnexpectedSize(expectedBytes, actual), actual);
            }
        }
        catch (ResumableFailure)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw Terminal(new ModelDownloadError.Cancelled(), received);
        }
        catch (ModelDownloadError error)
        {
            // Raised before the body loop, e.g. by the redirect policy: nothing to resume.
            throw Terminal(error, received);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or SocketException)
        {
            throw Resumable(new ModelDownloadError.Network(error.Message), received);
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <summary>
    /// Send the request, following redirects by hand so the allowlist sees every hop.
    /// </summary>
    private async Task<HttpResponseMessage> Send(
        Uri url,
        long resumeFrom,
        CancellationToken cancellationToken)
    {
        Uri current = url;
        for (int hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (resumeFrom > 0)
            {
                request.Headers.Range = new RangeHeaderValue(resumeFrom, null);
            }

            HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            int status = (int)response.StatusCode;
            if (status is < 300 or > 399 || response.Headers.Location is null)
            {
                return response;
            }

            Uri next = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(current, response.Headers.Location);
            response.Dispose();

            if (!policy.Allows(next))
            {
                throw new ModelDownloadError.UnapprovedURL(
                    next.Host.Length > 0 ? next.Host : "unknown address");
            }

            if (hop >= MaximumRedirects)
            {
                throw new ModelDownloadError.Network("too many redirects");
            }

            current = next;
        }
    }

    private static void TryRemove(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A leftover in the system temporary directory is not worth a second failure.
        }
    }

    private static ResumableFailure Terminal(ModelDownloadError error, long received) =>
        new(error, received, canResume: false);

    private static ResumableFailure Resumable(ModelDownloadError error, long received) =>
        new(error, received, canResume: received > 0);

    /// <summary>
    /// A dropped transfer plus whatever was saved of it.
    ///
    /// Private on purpose: callers see a plain <see cref="ModelDownloadError"/>. Resuming is
    /// a detail of how this downloader recovers, not part of the contract.
    /// </summary>
    private sealed class ResumableFailure : Exception
    {
        public ResumableFailure(ModelDownloadError underlying, long receivedBytes, bool canResume)
            : base(underlying.Message, underlying)
        {
            Underlying = underlying;
            ReceivedBytes = receivedBytes;
            CanResume = canResume;
        }

        public ModelDownloadError Underlying { get; }

        public long ReceivedBytes { get; }

        public bool CanResume { get; }
    }
}
