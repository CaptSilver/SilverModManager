using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Networking.Forums.Auth;

/// <summary>
/// The OAuth conversation with a forum: the authorize URL, the code exchange, and keeping the stored token
/// usable. Every entry point refuses while the site has no client id, and sends nothing.
/// </summary>
/// <remarks>
/// Tokens, codes, verifiers and request bodies never reach a log. The HTTP client passed in must not log request
/// URLs either.
/// </remarks>
public sealed class ForumOAuth
{
    private readonly HttpClient _http;
    private readonly IForumCredentials _credentials;
    private readonly Func<ForumSite, ForumEndpoints> _endpoints;
    private readonly TimeProvider _time;
    private readonly ILogger<ForumOAuth> _logger;

    // One gate per site: sites share code but not tokens, so one site's slow refresh must not hold up the other.
    private readonly Dictionary<ForumSite, SemaphoreSlim> _gates = Enum.GetValues<ForumSite>().ToDictionary(site => site, _ => new SemaphoreSlim(1, 1));

    public ForumOAuth(HttpClient http, IForumCredentials credentials, ISettingsManager settings, TimeProvider time, ILogger<ForumOAuth> logger)
        : this(http, credentials, site => ForumEndpoints.For(site, settings.Get<ForumAuthSettings>()), time, logger)
    {
    }

    internal ForumOAuth(HttpClient http, IForumCredentials credentials, Func<ForumSite, ForumEndpoints> endpoints, TimeProvider time, ILogger<ForumOAuth> logger)
    {
        _http = http;
        _credentials = credentials;
        _endpoints = endpoints;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// True when the site has a client id, i.e. when sign-in is switched on for it.
    /// </summary>
    public bool HasClientId(ForumSite site) => !string.IsNullOrWhiteSpace(_endpoints(site).ClientId);

    /// <summary>
    /// The site's endpoints as they stand, with or without a client id.
    /// </summary>
    public ForumEndpoints EndpointsFor(ForumSite site) => _endpoints(site);

    /// <summary>
    /// The site's endpoints with a client id. A request without one must not go out: it would be rejected or,
    /// worse, made as another app.
    /// </summary>
    /// <exception cref="SignInNotSetUpException">The site has no client id.</exception>
    public ForumEndpoints RequireClientId(ForumSite site)
    {
        var endpoints = _endpoints(site);
        if (string.IsNullOrWhiteSpace(endpoints.ClientId)) throw new SignInNotSetUpException(site.DisplayName());
        return endpoints;
    }

    /// <summary>
    /// The page the user approves the sign-in on.
    /// </summary>
    public Uri BuildAuthorizeUri(ForumSite site, Uri redirect, string state, string? challenge)
        => RequireClientId(site).BuildAuthorizeUri(redirect, state, challenge);

    /// <summary>
    /// Trades the code from the redirect for a token. Does not store it.
    /// </summary>
    /// <param name="verifier">The PKCE verifier; sent only while the site uses PKCE.</param>
    /// <param name="redirect">The same address the authorize URL carried.</param>
    /// <exception cref="SignInRejectedException">The forum refused the code or answered without an access token.</exception>
    public async Task<OAuthToken> ExchangeCode(ForumSite site, string code, string? verifier, Uri redirect, CancellationToken cancellationToken)
    {
        var endpoints = RequireClientId(site);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = endpoints.ClientId,
            ["redirect_uri"] = redirect.AbsoluteUri,
            ["code"] = code,
        };
        if (endpoints.UsePkce && !string.IsNullOrEmpty(verifier)) form["code_verifier"] = verifier;

        var token = await PostToken(endpoints, form, cancellationToken);
        if (token is null) throw new SignInRejectedException(site.DisplayName());
        return token;
    }

    /// <summary>
    /// Asks the forum for a new access token. Null when it refuses; a network failure throws, because it says
    /// nothing about whether the refresh token is still good.
    /// </summary>
    public async Task<OAuthToken?> Refresh(ForumSite site, string refreshToken, CancellationToken cancellationToken)
    {
        var endpoints = RequireClientId(site);
        var token = await PostToken(endpoints, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = endpoints.ClientId,
            ["refresh_token"] = refreshToken,
        }, cancellationToken);

        // A server that does not rotate refresh tokens leaves it out of the reply; the old one still works.
        return token is null ? null : token with { RefreshToken = token.RefreshToken ?? refreshToken };
    }

    /// <summary>
    /// The stored token, refreshed first when it has expired. Callers that race on an expired token send one
    /// refresh between them: the others wait on the site's gate and find the new token when they get in. With
    /// rotating refresh tokens a second refresh would fail and sign the user out in the middle of a batch.
    /// </summary>
    /// <exception cref="NotSignedInException">Nothing is stored for the site.</exception>
    /// <exception cref="SignInAgainException">The token expired and the forum will not renew it; it is cleared.</exception>
    public async Task<OAuthToken> GetUsableToken(ForumSite site, CancellationToken cancellationToken)
    {
        var cache = _credentials.For(site);
        var token = await cache.GetTokenAsync(cancellationToken) ?? throw new NotSignedInException(site.DisplayName());
        if (!token.HasExpired(_time.GetUtcNow())) return token;

        var gate = _gates[site];
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Another caller, or another process, may have refreshed while this one waited.
            token = await cache.GetTokenAsync(cancellationToken) ?? throw new NotSignedInException(site.DisplayName());
            if (!token.HasExpired(_time.GetUtcNow())) return token;

            if (token.RefreshToken is not { } refreshToken)
            {
                _logger.LogInformation("The {Site} token expired and has no refresh token; signing out", site);
                await cache.ClearAsync(cancellationToken);
                throw new SignInAgainException(site.DisplayName());
            }

            var fresh = await Refresh(site, refreshToken, cancellationToken);
            if (fresh is null)
            {
                _logger.LogInformation("{Site} refused the token refresh; signing out", site);
                await cache.ClearAsync(cancellationToken);
                throw new SignInAgainException(site.DisplayName());
            }

            await cache.SetTokenAsync(fresh, cancellationToken);
            return fresh;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<OAuthToken?> PostToken(ForumEndpoints endpoints, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(endpoints.Token, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // The reply may echo the code or verifier; log the status only.
            _logger.LogInformation("The token endpoint answered {Status}", (int)response.StatusCode);
            return null;
        }

        try
        {
            var reply = await response.Content.ReadFromJsonAsync<OAuthTokenReply>(cancellationToken);
            return reply is null ? null : OAuthToken.FromReply(reply, _time.GetUtcNow());
        }
        catch (JsonException)
        {
            _logger.LogInformation("The token endpoint's reply was not a token reply");
            return null;
        }
    }
}
