using System.Net;
using System.Text;

namespace LocalASR.Tests;

/// <summary>
/// The Swift file is <c>URLSessionModelDownloaderTests</c>; the downloader is called
/// <see cref="HttpClientModelDownloader"/> here, and the stubbing seam is an injected
/// <see cref="HttpMessageHandler"/> rather than a <c>URLProtocol</c> subclass.
/// </summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, HttpResponseMessage> responder;
    private int requestCount;

    public StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder)
    {
        this.responder = responder;
    }

    public int RequestCount => Volatile.Read(ref requestCount);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        int attempt = Interlocked.Increment(ref requestCount);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(responder(request, attempt));
    }
}

/// <summary>A body that ends mid-transfer, the way a dropped connection does.</summary>
internal sealed class TruncatedStream : Stream
{
    private readonly byte[] payload;
    private readonly int breakAfter;
    private int position;

    public TruncatedStream(byte[] payload, int breakAfter)
    {
        this.payload = payload;
        this.breakAfter = breakAfter;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => payload.Length;

    public override long Position
    {
        get => position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (position >= breakAfter)
        {
            throw new IOException("The response ended prematurely.");
        }

        int read = Math.Min(count, breakAfter - position);
        Array.Copy(payload, position, buffer, offset, read);
        position += read;
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

public class HttpClientModelDownloaderTests
{
    private readonly Uri url = new("https://huggingface.co/test/model.bin");

    private static HttpResponseMessage Response(HttpStatusCode status, byte[] body, long? contentLength = null)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentLength = contentLength ?? body.Length;
        return response;
    }

    /// <summary>
    /// The Swift test asserts the URLSession flags that let an explicitly started download run
    /// on any network. The only one of those with a counterpart here is the transfer-wide
    /// hour: HttpClient has no notion of expensive or constrained networks, and .NET never
    /// waits for connectivity in the first place.
    /// </summary>
    [Fact]
    public void DefaultConfigurationGivesTheWholeTransferAnHour() =>
        Assert.Equal(TimeSpan.FromHours(1), HttpClientModelDownloader.ResourceTimeout);

    [Fact]
    public async Task ExactHTTPSResponseIsAccepted()
    {
        byte[] body = Encoding.UTF8.GetBytes("model");
        using var downloader = new HttpClientModelDownloader(
            new StubHandler((_, _) => Response(HttpStatusCode.OK, body)));

        string path = await downloader.Download(url, body.Length, _ => { });
        try
        {
            Assert.Equal(body, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task HTTPFailureIsNotPromotedAsModelData()
    {
        byte[] body = Encoding.UTF8.GetBytes("server error");
        using var downloader = new HttpClientModelDownloader(
            new StubHandler((_, _) => Response(HttpStatusCode.ServiceUnavailable, body)));

        var error = await Assert.ThrowsAsync<ModelDownloadError.HttpStatus>(
            () => downloader.Download(url, body.Length, _ => { }));
        Assert.Equal(503, error.Code);
    }

    [Fact]
    public async Task OversizedBodyStopsBeforePromotion()
    {
        byte[] body = Enumerable.Repeat((byte)0x41, 32).ToArray();
        using var downloader = new HttpClientModelDownloader(
            new StubHandler((_, _) => Response(HttpStatusCode.OK, body)));

        var error = await Assert.ThrowsAsync<ModelDownloadError.UnexpectedSize>(
            () => downloader.Download(url, 8, _ => { }));
        Assert.Equal(8, error.Expected);
        Assert.True(error.Actual > 8);
    }

    /// <summary>
    /// A redirect off the allowlist never reaches the wire. Swift validates it in the session
    /// delegate; here the downloader follows redirects by hand for exactly this reason.
    /// </summary>
    [Fact]
    public async Task RedirectOffTheAllowlistIsRefusedBeforeItIsFollowed()
    {
        var handler = new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://evil.example.com/model.bin");
            return response;
        });
        using var downloader = new HttpClientModelDownloader(handler);

        var error = await Assert.ThrowsAsync<ModelDownloadError.UnapprovedURL>(
            () => downloader.Download(url, 100, _ => { }));
        Assert.Equal("evil.example.com", error.Host);
        Assert.Equal(1, handler.RequestCount);
    }
}

/// <summary>
/// Continuing a dropped transfer instead of starting it over.
///
/// Measured on a real flaky link: the 445 MB encoder reached 415 MB, the connection dropped,
/// and the retry started from zero. Six minutes of download thrown away, and on a link that
/// drops regularly the install never finishes.
/// </summary>
public class DownloadResumeTests
{
    private readonly Uri url = new("https://huggingface.co/test/model.bin");

    /// <summary>
    /// A drop with nothing saved is handed straight up, without re-dialling.
    ///
    /// The layer above already retries and also knows about the mirror. Dialling again here
    /// would multiply attempts without saving a single byte.
    /// </summary>
    [Fact]
    public async Task FailureWithoutResumeDataIsNotRetriedHere()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("connection lost"));
        using var downloader = new HttpClientModelDownloader(handler);

        await Assert.ThrowsAsync<ModelDownloadError.Network>(
            () => downloader.Download(url, 100, _ => { }));

        Assert.Equal(1, handler.RequestCount);
    }

    /// <summary>
    /// Bytes on disk are continued from, not thrown away: that is the whole point of the
    /// three attempts.
    /// </summary>
    [Fact]
    public async Task DroppedTransferResumesFromTheBytesAlreadyWritten()
    {
        byte[] payload = Encoding.UTF8.GetBytes("model");
        var handler = new StubHandler((request, attempt) =>
        {
            if (attempt == 1)
            {
                var dropped = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new TruncatedStream(payload, breakAfter: 3)),
                };
                dropped.Content.Headers.ContentLength = payload.Length;
                return dropped;
            }

            long from = request.Headers.Range?.Ranges.Single().From
                ?? throw new InvalidOperationException("the retry must ask for a range");
            var rest = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(payload[(int)from..]),
            };
            rest.Content.Headers.ContentLength = payload.Length - from;
            return rest;
        });
        using var downloader = new HttpClientModelDownloader(handler);

        string path = await downloader.Download(url, payload.Length, _ => { });
        try
        {
            Assert.Equal(payload, File.ReadAllBytes(path));
            Assert.Equal(2, handler.RequestCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A cancel carries nothing forward: continuing later would continue against the user's
    /// direct command.
    /// </summary>
    [Fact]
    public async Task CancellationIsReportedAsCancellation()
    {
        var handler = new StubHandler((_, _) => Response());
        using var downloader = new HttpClientModelDownloader(handler);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<ModelDownloadError.Cancelled>(
            () => downloader.Download(url, 100, _ => { }, cancellation.Token));

        static HttpResponseMessage Response() =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
    }

    /// <summary>
    /// A refused host stays one attempt: retrying it is exactly what the network policy
    /// forbids.
    /// </summary>
    [Fact]
    public async Task DisallowedURLNeverReachesTheNetwork()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("must not be reached"));
        using var downloader = new HttpClientModelDownloader(handler);
        var outside = new Uri("https://evil.example.com/model.bin");

        await Assert.ThrowsAsync<ModelDownloadError.UnapprovedURL>(
            () => downloader.Download(outside, 100, _ => { }));

        Assert.Equal(0, handler.RequestCount);
    }
}
