using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.DTOs.OAuth;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Abstractions.Secrets;

namespace SilverModManager.Networking.NexusWebApi.Auth;

/// <summary>
/// OAuth2 based authentication
/// </summary>
public class OAuth2MessageFactory : BaseHttpMessageFactory, IAuthenticatingMessageFactory
{
    private readonly ILogger<OAuth2MessageFactory> _logger;
    private readonly OAuth _auth;
    private readonly NexusCredentialCache _cache;
    private readonly SecretStoreOptions _options;

    /// <summary>
    /// Constructor.
    /// </summary>
    public OAuth2MessageFactory(
        NexusCredentialCache cache,
        OAuth auth,
        SecretStoreOptions options,
        ILogger<OAuth2MessageFactory> logger)
    {
        _cache = cache;
        _auth = auth;
        _options = options;
        _logger = logger;
    }

    private async ValueTask<string?> GetOrRefreshToken(CancellationToken cancellationToken)
    {
        var token = await _cache.GetTokenAsync(cancellationToken);
        if (token is null) return null;
        if (!token.HasExpired) return token.AccessToken;

        // OAuth refresh tokens are single use. A background process whose store cannot take the new token would
        // spend the stored one and leave the app with a dead token, so it stays anonymous instead.
        if (_options.Mode == SecretStoreMode.NonInteractive && !_cache.Writable)
        {
            _logger.LogWarning("The OAuth token has expired and the secret store cannot take a refreshed one; continuing without a credential");
            return null;
        }

        if (!_auth.HasClientId)
        {
            // Without a client id the refresh request cannot be made. Leave the stored token alone
            // and report no usable credential, so the user is asked to sign in again or use an API key.
            _logger.LogWarning("The OAuth token has expired and no OAuth client id is configured to refresh it; sign in again or use an API key");
            return null;
        }

        // Another process (the app next to a game launch) may have refreshed it already, and its refresh
        // token is the live one.
        token = await _cache.ReloadTokenAsync(cancellationToken);
        if (token is null) return null;
        if (!token.HasExpired) return token.AccessToken;

        _logger.LogDebug("Refreshing expired OAuth token");

        var reply = await _auth.RefreshToken(token.RefreshToken, cancellationToken);
        var newToken = reply is null ? null : NexusOAuthToken.FromReply(reply);
        if (newToken is null)
        {
            _logger.LogError("The OAuth token endpoint did not return a usable token for the refresh");

            // Nexus may have refused because the other process got there first.
            var stored = await _cache.ReloadTokenAsync(cancellationToken);
            return stored is { HasExpired: false } ? stored.AccessToken : null;
        }

        await _cache.SetTokenAsync(newToken, cancellationToken);
        return newToken.AccessToken;
    }

    /// <inheritdoc/>
    public override async ValueTask<HttpRequestMessage> Create(HttpMethod method, Uri uri)
    {
        var token = await GetOrRefreshToken(CancellationToken.None);
        if (token is null) throw new Exception("Unauthorized!");

        var requestMessage = await base.Create(method, uri);
        requestMessage.Headers.Add("Authorization", $"Bearer {token}");
        return requestMessage;
    }

    /// <inheritdoc/>
    public override async ValueTask ApplyAuthentication(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Refresh here rather than skip an expired token, or the request would go out anonymous.
        var token = await GetOrRefreshToken(cancellationToken);
        if (token is null) return;

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <inheritdoc/>
    public override async ValueTask<bool> IsAuthenticated()
    {
        var token = await GetOrRefreshToken(CancellationToken.None);
        return token is not null;
    }

    /// <inheritdoc/>
    public async ValueTask<UserInfo?> Verify(INexusApiClient nexusApiNexusApiClient, CancellationToken cancel)
    {
        OAuthUserInfo oAuthUserInfo;
        try
        {
            var res = await nexusApiNexusApiClient.GetOAuthUserInfo(cancellationToken: cancel);
            oAuthUserInfo = res.Data;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Exception while fetching OAuth user info");
            return null;
        }

        return new UserInfo
        {
            UserId = UserId.From(ulong.Parse(oAuthUserInfo.Sub)),
            Name = oAuthUserInfo.Name,
            UserRole =  oAuthUserInfo.MembershipRoles.Contains(MembershipRole.Premium) ? UserRole.Premium : oAuthUserInfo.MembershipRoles.Contains(MembershipRole.Supporter) ? UserRole.Supporter : UserRole.Free,
            AvatarUrl = oAuthUserInfo.Avatar,
        };
    }
}
