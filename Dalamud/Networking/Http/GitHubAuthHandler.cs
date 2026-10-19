using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Configuration.Internal;

namespace Dalamud.Networking.Http;

/// <summary>
/// A <see cref="DelegatingHandler"/> that attaches the configured GitHub token to requests aimed at
/// this organisation's GitHub hosts, and to no other request.
/// </summary>
/// <remarks>
/// The shared clients also talk to Universalis, kamori and other orgs' raw endpoints, so the token
/// must never be put in <see cref="HttpClient.DefaultRequestHeaders"/>.
/// The token is read once per request, so changing it in the settings takes effect without a restart.
/// .NET drops the Authorization header when following a redirect, so the signed storage URL that
/// GitHub's asset endpoint redirects to never sees it.
/// </remarks>
internal sealed class GitHubAuthHandler : DelegatingHandler
{
    /// <summary>
    /// The only GitHub owner whose URLs ever receive an Authorization header.
    /// </summary>
    public const string Owner = "ffxiv-tc-port";

    private const string RawHost = "raw.githubusercontent.com";
    private const string ApiHost = "api.github.com";
    private const string ApiRepoPrefix = "/repos/" + Owner + "/";

    private readonly Func<string?> tokenProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="GitHubAuthHandler"/> class.
    /// </summary>
    /// <param name="innerHandler">The handler that performs the request.</param>
    /// <param name="tokenProvider">
    /// Token source. Defaults to the Dalamud configuration; an explicit provider lets this handler be
    /// exercised outside a running service container.
    /// </param>
    public GitHubAuthHandler(HttpMessageHandler innerHandler, Func<string?>? tokenProvider = null)
        : base(innerHandler)
    {
        this.tokenProvider = tokenProvider ?? GetConfiguredToken;
    }

    /// <summary>
    /// Decides whether a URI is one of this organisation's GitHub endpoints.
    /// </summary>
    /// <param name="uri">The request URI. May be null or relative.</param>
    /// <returns>Whether an Authorization header may be attached to it.</returns>
    public static bool ShouldAuthenticate(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
            return false;

        return uri.Host switch
        {
            RawHost => FirstPathSegmentIsOwner(uri.AbsolutePath),
            ApiHost => uri.AbsolutePath.StartsWith(ApiRepoPrefix, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <inheritdoc/>
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // An Authorization header the caller set itself always wins.
        if (request.Headers.Authorization is null && ShouldAuthenticate(request.RequestUri))
        {
            var token = this.tokenProvider();
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return base.SendAsync(request, cancellationToken);
    }

    // raw.githubusercontent.com/<owner>/<repo>/<ref>/<path> - only the first segment names the owner.
    private static bool FirstPathSegmentIsOwner(string absolutePath)
    {
        if (absolutePath.Length < 2 || absolutePath[0] != '/')
            return false;

        var end = absolutePath.IndexOf('/', 1);
        var length = (end < 0 ? absolutePath.Length : end) - 1;

        return length == Owner.Length &&
               string.Compare(absolutePath, 1, Owner, 0, length, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static string? GetConfiguredToken()
        => Service<DalamudConfiguration>.GetNullable()?.GitHubToken;
}
