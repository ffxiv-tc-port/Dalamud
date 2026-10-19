using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Dalamud.Networking.Http;

/// <summary>
/// Downloads a file over several HTTP range requests at once, resuming any range that breaks off.
/// One instance is shared by every download, so that together they hold at most their first connection each
/// plus a fixed pool of extra ones.
/// </summary>
/// <remarks>
/// Never log a request URL here: the storage URL GitHub redirects to lets anyone who has it download the file,
/// and a cached copy answers to the path alone.
/// </remarks>
internal sealed class SegmentedDownloader : IDisposable
{
    /// <summary>
    /// The number of extra connections all downloads share, on top of the first connection each download always has.
    /// </summary>
    public const int DefaultSharedExtraConnections = 16;

    private const string GitHubApiHost = "api.github.com";
    private const string Sha256DigestPrefix = "sha256:";

    // Not disposed with the client: workers still running at shutdown return their slot afterwards.
    private readonly SemaphoreSlim extraConnections;

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentedDownloader"/> class.
    /// </summary>
    /// <param name="client">
    /// The client for every request. It must follow redirects and must not decompress: range checks compare byte
    /// counts with what the server stores. It has to bound nothing itself; each download passes its own token.
    /// </param>
    /// <param name="sharedExtraConnections">How many connections beyond the first one all downloads may add together.</param>
    /// <param name="options">Sizes and timings; <see cref="SegmentedDownloadOptions.Default"/> when omitted.</param>
    public SegmentedDownloader(
        HttpClient client, int sharedExtraConnections = DefaultSharedExtraConnections, SegmentedDownloadOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sharedExtraConnections);
        this.Client = client;
        this.Options = options ?? SegmentedDownloadOptions.Default;
        this.extraConnections = new SemaphoreSlim(sharedExtraConnections, Math.Max(1, sharedExtraConnections));
    }

    /// <summary>
    /// Gets the client every request goes through.
    /// </summary>
    public HttpClient Client { get; }

    /// <summary>
    /// Gets the sizes and timings every download uses.
    /// </summary>
    public SegmentedDownloadOptions Options { get; }

    /// <summary>
    /// Gets the number of shared extra connections not taken by any download right now.
    /// </summary>
    public int AvailableExtraConnections => this.extraConnections.CurrentCount;

    /// <summary>
    /// Downloads a file, splitting it into range requests when the server allows it.
    /// </summary>
    /// <param name="url">The file. Redirects are followed, and later requests go where the first one ended up.</param>
    /// <param name="maxConnections">The most requests this download may have in flight at once, the first one included.</param>
    /// <param name="prepareRequest">Adds the headers every request needs, such as Accept. Called from any thread.</param>
    /// <param name="onTotalKnown">Called once with the file size, when the first range response confirms it.</param>
    /// <param name="onProgress">Called with each batch of newly received bytes. Calls never overlap.</param>
    /// <param name="cancellationToken">Aborts the whole download, which then throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>The file, the whole-file response of a server that ignored the range, or why to download it the plain way.</returns>
    public async Task<SegmentedDownloadResult> DownloadAsync(
        Uri url,
        int maxConnections,
        Action<HttpRequestMessage>? prepareRequest,
        Action<long> onTotalKnown,
        Action<long> onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConnections, 1);

        var firstTo = this.Options.FirstRangeBytes - 1;
        HttpResponseMessage response;
        Uri finalUrl;
        using (var firstCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        using (var request = this.CreateRangeRequest(url, 0, firstTo, prepareRequest))
        {
            firstCts.CancelAfter(this.Options.IdleTimeout);
            try
            {
                response = await this.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, firstCts.Token)
                                     .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return SegmentedDownloadResult.NotSupported(
                    $"no response within {this.Options.IdleTimeout.TotalSeconds:0} seconds", null);
            }
            catch (HttpRequestException ex)
            {
                return SegmentedDownloadResult.NotSupported("the first request failed", ex);
            }

            finalUrl = response.RequestMessage?.RequestUri ?? url;
        }

        if (response.StatusCode == HttpStatusCode.OK && !IsEncoded(response))
            return SegmentedDownloadResult.Whole(response);

        var unusable = this.CheckFirstResponse(response, firstTo);
        if (unusable is not null)
        {
            response.Dispose();
            return SegmentedDownloadResult.NotSupported(unusable, null);
        }

        var range = response.Content.Headers.ContentRange!;
        var total = range.Length!.Value;
        SegmentedDownloadJob job;
        try
        {
            onTotalKnown(total);
            job = new SegmentedDownloadJob(
                this,
                url,
                finalUrl,
                prepareRequest,
                maxConnections,
                total,
                GetStrongETag(response),
                response.Content.Headers.LastModified,
                onProgress,
                cancellationToken);
        }
        catch
        {
            response.Dispose();
            throw;
        }

        return await job.RunAsync(response, range.To!.Value).ConfigureAwait(false);
    }

    /// <summary>
    /// Looks up the SHA-256 GitHub lists for a release asset, when <paramref name="url"/> is one on its REST API.
    /// </summary>
    /// <param name="url">The URL the file is downloaded from.</param>
    /// <param name="cancellationToken">Aborts the lookup.</param>
    /// <returns>The 32-byte digest, or <c>null</c> when the URL is not a GitHub asset or the lookup failed. Never faults.</returns>
    public async Task<byte[]?> TryGetGitHubAssetSha256Async(Uri url, CancellationToken cancellationToken)
    {
        if (!IsGitHubReleaseAsset(url))
            return null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(this.Options.IdleTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await this.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                                           .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                return null;

            var body = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                using var json = await JsonDocument.ParseAsync(body, default, cts.Token).ConfigureAwait(false);
                if (!json.RootElement.TryGetProperty("digest", out var digest) || digest.ValueKind != JsonValueKind.String)
                    return null;

                var text = digest.GetString();
                if (text is null || text.Length != Sha256DigestPrefix.Length + 64 ||
                    !text.StartsWith(Sha256DigestPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return Convert.FromHexString(text.AsSpan(Sha256DigestPrefix.Length));
            }
        }
        catch (Exception)
        {
            // Callers may stop waiting for this task, so it must never fault; any failure only means "not checked".
            return null;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        this.Client.Dispose();
    }

    /// <summary>
    /// Decides whether a response carries a content coding, which makes its bytes differ from the stored file.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <returns>Whether any coding other than identity was applied.</returns>
    internal static bool IsEncoded(HttpResponseMessage response)
    {
        foreach (var coding in response.Content.Headers.ContentEncoding)
        {
            if (!coding.Equals("identity", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Gets the entity tag of a response when it is a strong one; weak tags do not promise identical bytes.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <returns>The quoted tag, or <c>null</c>.</returns>
    internal static string? GetStrongETag(HttpResponseMessage response)
        => response.Headers.ETag is { IsWeak: false } etag ? etag.Tag : null;

    /// <summary>
    /// Decides whether a Content-Range counts bytes and states both the range and the full length.
    /// </summary>
    /// <param name="range">The header value, if any.</param>
    /// <returns>Whether the range can be checked against a request.</returns>
    internal static bool IsCompleteByteRange(ContentRangeHeaderValue? range)
        => range is { HasRange: true, HasLength: true } &&
           string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Takes one of the shared extra connections, without waiting for one to free up.
    /// </summary>
    /// <returns>Whether one was free. Every successful call must be matched by <see cref="ReturnExtraConnection"/>.</returns>
    internal bool TryTakeExtraConnection() => this.extraConnections.Wait(0);

    /// <summary>
    /// Gives back a connection taken with <see cref="TryTakeExtraConnection"/>.
    /// </summary>
    internal void ReturnExtraConnection() => this.extraConnections.Release();

    /// <summary>
    /// Builds a GET for one byte range: HTTP/1.1 only, so that parallel ranges really get their own connections,
    /// and identity encoding only, so that the byte counts are the stored file's.
    /// </summary>
    /// <param name="url">The target.</param>
    /// <param name="from">The first byte.</param>
    /// <param name="to">The last byte, inclusive.</param>
    /// <param name="prepareRequest">Adds the caller's headers.</param>
    /// <returns>The request. The caller disposes it.</returns>
    internal HttpRequestMessage CreateRangeRequest(Uri url, long from, long to, Action<HttpRequestMessage>? prepareRequest)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.Range = new RangeHeaderValue(from, to);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        prepareRequest?.Invoke(request);
        return request;
    }

    private static bool IsGitHubReleaseAsset(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(url.Host, GitHubApiHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // /repos/<owner>/<repo>/releases/assets/<id>
        var parts = url.AbsolutePath.Split('/');
        return parts.Length == 7 && parts[0].Length == 0 && parts[1] == "repos" && parts[2].Length > 0 &&
               parts[3].Length > 0 && parts[4] == "releases" && parts[5] == "assets" &&
               parts[6].Length > 0 && parts[6].All(char.IsAsciiDigit);
    }

    private string? CheckFirstResponse(HttpResponseMessage response, long firstTo)
    {
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
            return $"the server answered HTTP {(int)response.StatusCode}";

        if (IsEncoded(response))
            return "the server compressed the file";

        var range = response.Content.Headers.ContentRange;
        if (!IsCompleteByteRange(range) || range!.From != 0 || range.To is not { } to || to < 0 || to > firstTo ||
            range.Length <= to)
        {
            return "the server sent an unexpected Content-Range";
        }

        if (response.Content.Headers.ContentLength is { } length && length != to + 1)
            return "Content-Length does not match Content-Range";

        if (GetStrongETag(response) is null && response.Content.Headers.LastModified is null)
            return "the server sent neither a strong ETag nor Last-Modified";

        return range.Length > this.Options.MaxTotalBytes ? "the file is too large to hold in memory" : null;
    }
}
