using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.NexusWebApi.DTOs;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Abstractions.Secrets;

namespace NexusMods.Networking.NexusWebApi.Auth;

/// <summary>
/// Injects Nexus API keys into HTTP messages.
/// </summary>
/// <remarks>
/// The key comes from the credential cache, which reads the secret store. <c>NEXUS_API_KEY</c> is
/// the secret overlay's business, so it reaches here as an ordinary stored key.
/// </remarks>
public class ApiKeyMessageFactory(NexusCredentialCache cache) : BaseHttpMessageFactory, IAuthenticatingMessageFactory
{
    /// <inheritdoc />
    public override async ValueTask<HttpRequestMessage> Create(HttpMethod method, Uri uri)
    {
        var apiKey = await cache.GetApiKeyAsync(CancellationToken.None);
        if (string.IsNullOrWhiteSpace(apiKey)) throw new Exception("No API key set");

        var requestMessage = await base.Create(method, uri);
        requestMessage.Headers.Add("apikey", apiKey);
        return requestMessage;
    }

    /// <inheritdoc />
    public override async ValueTask ApplyAuthentication(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Keep unauthenticated requests anonymous instead of failing them.
        var apiKey = await cache.GetApiKeyAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey)) return;

        request.Headers.Remove("apikey");
        request.Headers.Add("apikey", apiKey);
    }

    /// <summary>
    /// True when a key is stored or supplied from outside the store.
    /// </summary>
    public async ValueTask<bool> HasApiKey(CancellationToken cancellationToken = default)
        => !string.IsNullOrWhiteSpace(await cache.GetApiKeyAsync(cancellationToken));

    /// <inheritdoc />
    public override async ValueTask<bool> IsAuthenticated() => await HasApiKey();

    /// <summary>
    /// Sets the API key used for future requests.
    /// </summary>
    /// <param name="apiKey">The new API key set.</param>
    /// <returns>Whether the store took it. A key the store refuses is still used until the process ends.</returns>
    public async ValueTask<SecretWriteResult> SetApiKey(string apiKey)
        => await cache.SetApiKeyAsync(apiKey, CancellationToken.None);

    /// <inheritdoc/>
    public async ValueTask<UserInfo?> Verify(INexusApiClient nexusApiNexusApiClient, CancellationToken token)
    {
        var result = await nexusApiNexusApiClient.Validate(token);
        return ToUserInfo(result.Data);
    }

    /// <summary>
    /// The account described by a validate response.
    /// </summary>
    internal static UserInfo ToUserInfo(ValidateInfo info) => new()
    {
        UserId = info.UserId,
        Name = info.Name,
        UserRole = info.IsPremium ? UserRole.Premium : info.IsSupporter ? UserRole.Supporter : UserRole.Free,
        AvatarUrl = info.ProfileUrl,
    };
}
