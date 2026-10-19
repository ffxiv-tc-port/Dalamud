using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

using Dalamud.Plugin.Internal;
using Dalamud.Utility;
using Microsoft.VisualBasic.CompilerServices;

namespace Dalamud.Networking.Http;

/// <summary>
/// A service to help build and manage HttpClients with some semblance of Happy Eyeballs (RFC 8305 - IPv4 fallback)
/// awareness.
/// </summary>
[ServiceManager.BlockingEarlyLoadedService($"{nameof(PluginManager)} currently uses this.")]
// ^ TODO: This seems unnecessary, remove the hard dependency at a later time.
//         Otherwise, if PM eventually marks this class as required, note that in the comment above.
internal class HappyHttpClient : IInternalDisposableService
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HappyHttpClient"/> class.
    ///
    /// A service to talk to the Smileton Loporrits to build an HTTP Client aware of Happy Eyeballs.
    /// </summary>
    [ServiceManager.ServiceConstructor]
    private HappyHttpClient()
    {
        this.SharedHappyEyeballsCallback = new HappyEyeballsCallback();

        this.SharedHttpClient = CreateClient();

        // Large transfers are bounded by the caller's CancellationToken instead of a wall-clock
        // budget, so a slow but still-progressing download is not killed mid-transfer.
        this.SharedLargeFileHttpClient = CreateClient();
        this.SharedLargeFileHttpClient.Timeout = System.Threading.Timeout.InfiniteTimeSpan;

        // Range checks compare byte counts with the stored file, so nothing may be decompressed on the way.
        // A range abandoned part way is not worth draining at a few KB/s; its connection is closed instead.
        var pluginDownloadClient = CreateClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = this.SharedHappyEyeballsCallback.ConnectCallback,
            MaxResponseDrainSize = 0,
        });
        pluginDownloadClient.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        this.PluginDownloader = new SegmentedDownloader(pluginDownloadClient);

        HttpClient CreateClient(SocketsHttpHandler? handler = null)
        {
            // Each client gets its own handler (sharing a SocketsHttpHandler risks GC issues) but
            // shares the Happy Eyeballs callback state.
            var client = new HttpClient(new GitHubAuthHandler(handler ?? new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                ConnectCallback        = this.SharedHappyEyeballsCallback.ConnectCallback,
            }))
            {
                DefaultRequestHeaders =
                {
                    UserAgent =
                    {
                        new ProductInfoHeaderValue("Dalamud", Util.AssemblyVersion),
                    },
                },
            };
            client.DefaultRequestHeaders.Add("X-Machine-Token", DeviceUtils.GetDeviceId());
            return client;
        }
    }

    /// <summary>
    /// Gets a <see cref="HttpClient"/> meant to be shared across all (standard) requests made by the application,
    /// where custom configurations are not required.
    ///
    /// May or may not have been properly tested by the Loporrits.
    /// </summary>
    public HttpClient SharedHttpClient { get; }

    /// <summary>
    /// Gets a <see cref="HttpClient"/> for transfers whose size is not known up front, such as plugin
    /// downloads. It has no <see cref="HttpClient.Timeout"/> of its own: callers MUST pass a
    /// <see cref="System.Threading.CancellationToken"/> that bounds both stalls and total duration.
    /// </summary>
    public HttpClient SharedLargeFileHttpClient { get; }

    /// <summary>
    /// Gets the downloader for plugin archives, which fetches one file over several range requests at once.
    /// Its client authenticates like the others but never decompresses, and it has no timeout of its own.
    /// </summary>
    public SegmentedDownloader PluginDownloader { get; }

    /// <summary>
    /// Gets a <see cref="HappyEyeballsCallback"/> meant to be shared across any custom <see cref="HttpClient"/>s that
    /// need to be made in other parts of the application.
    ///
    /// This should be used when shared callback state is desired across multiple clients, as sharing the SocketsHandler
    /// may lead to GC issues.
    /// </summary>
    public HappyEyeballsCallback SharedHappyEyeballsCallback { get; }

    /// <summary>
    /// Creates a new <see cref="HttpClient"/> that authenticates against this organisation's
    /// GitHub hosts and leaves every other host untouched.
    /// </summary>
    /// <returns>A new client, not one of the shared ones. The caller must dispose it.</returns>
    public HttpClient CreateGitHubAuthenticatedClient()
    {
        var client = new HttpClient(new GitHubAuthHandler(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = this.SharedHappyEyeballsCallback.ConnectCallback,
        }));

        // api.github.com rejects requests that carry no User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Dalamud", Util.AssemblyVersion));
        return client;
    }

    /// <inheritdoc/>
    void IInternalDisposableService.DisposeService()
    {
        this.SharedHttpClient.Dispose();
        this.SharedLargeFileHttpClient.Dispose();
        this.PluginDownloader.Dispose();
        this.SharedHappyEyeballsCallback.Dispose();

        GC.SuppressFinalize(this);
    }
}
