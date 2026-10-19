using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.MnemonicDB.Abstractions;

namespace NexusMods.Networking.NexusWebApi.Auth;

/// <summary>
/// Picks the credential per call, so signing in or out switches between a personal API key and
/// an OAuth token without rebuilding the container.
/// </summary>
/// <remarks>
/// A personal API key (stored, or from <c>NEXUS_API_KEY</c>) wins over a stored OAuth token: it is
/// the credential the user manages directly.
/// </remarks>
public class CredentialRoutedMessageFactory(
    IConnection conn,
    ApiKeyMessageFactory apiKeyFactory,
    OAuth2MessageFactory oAuthFactory) : IAuthenticatingMessageFactory
{
    private IAuthenticatingMessageFactory? Route()
    {
        if (apiKeyFactory.HasApiKey) return apiKeyFactory;
        if (JWTToken.TryFind(conn.Db, out _)) return oAuthFactory;
        return null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// With no credential stored this fails with the API key factory's "No API key set", which is
    /// what the REST client has always done for an unauthenticated call.
    /// </remarks>
    public async ValueTask<HttpRequestMessage> Create(HttpMethod method, Uri uri)
    {
        var route = Route() ?? apiKeyFactory;
        return await route.Create(method, uri);
    }

    /// <inheritdoc/>
    public async ValueTask ApplyAuthentication(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var route = Route();
        if (route is null) return;

        await route.ApplyAuthentication(request, cancellationToken);
    }

    /// <inheritdoc/>
    public async ValueTask<bool> IsAuthenticated()
    {
        var route = Route();
        return route is not null && await route.IsAuthenticated();
    }

    /// <inheritdoc/>
    public async ValueTask<UserInfo?> Verify(INexusApiClient nexusApiNexusApiClient, CancellationToken token)
    {
        var route = Route();
        return route is null ? null : await route.Verify(nexusApiNexusApiClient, token);
    }
}
