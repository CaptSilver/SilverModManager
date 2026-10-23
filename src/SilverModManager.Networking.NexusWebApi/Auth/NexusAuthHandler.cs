using SilverModManager.Abstractions.NexusWebApi;

namespace SilverModManager.Networking.NexusWebApi.Auth;

/// <summary>
/// Adds the current credential to every request made through the GraphQL <see cref="HttpClient"/>.
/// Doing it per request, instead of once when the client is built, means a login, logout or token
/// refresh takes effect on the next call.
/// </summary>
public class NexusAuthHandler(IHttpMessageFactory messageFactory) : DelegatingHandler
{
    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await messageFactory.ApplyAuthentication(request, cancellationToken);
        return await base.SendAsync(request, cancellationToken);
    }
}
