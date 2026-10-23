using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;

namespace SilverModManager.Networking.NexusWebApi.Auth;

/// <summary>
/// Picks the credential per call, so signing in or out switches between a personal API key and
/// an OAuth token without rebuilding the container.
/// </summary>
/// <remarks>
/// A personal API key (stored, or supplied from outside by the secret overlay) wins over a stored OAuth token: it is
/// the credential the user manages directly.
/// </remarks>
public class CredentialRoutedMessageFactory(
    NexusCredentialCache cache,
    ApiKeyMessageFactory apiKeyFactory,
    OAuth2MessageFactory oAuthFactory) : IAuthenticatingMessageFactory
{
    private async ValueTask<IAuthenticatingMessageFactory?> Route(CancellationToken cancellationToken = default)
    {
        if (await apiKeyFactory.HasApiKey(cancellationToken)) return apiKeyFactory;
        if (await cache.GetTokenAsync(cancellationToken) is not null) return oAuthFactory;
        return null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// With no credential stored this fails with the API key factory's "No API key set", which is
    /// what the REST client has always done for an unauthenticated call.
    /// </remarks>
    public async ValueTask<HttpRequestMessage> Create(HttpMethod method, Uri uri)
    {
        var route = await Route() ?? apiKeyFactory;
        return await route.Create(method, uri);
    }

    /// <inheritdoc/>
    public async ValueTask ApplyAuthentication(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var route = await Route(cancellationToken);
        if (route is null) return;

        await route.ApplyAuthentication(request, cancellationToken);
    }

    /// <inheritdoc/>
    public async ValueTask<bool> IsAuthenticated()
    {
        var route = await Route();
        return route is not null && await route.IsAuthenticated();
    }

    /// <inheritdoc/>
    public async ValueTask<UserInfo?> Verify(INexusApiClient nexusApiNexusApiClient, CancellationToken token)
    {
        var route = await Route(token);
        return route is null ? null : await route.Verify(nexusApiNexusApiClient, token);
    }
}
