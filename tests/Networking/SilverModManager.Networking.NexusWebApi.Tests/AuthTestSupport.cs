using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.NexusWebApi.DTOs.OAuth;
using NexusMods.Abstractions.Secrets;
using NexusMods.Networking.NexusWebApi.Auth;
using NexusMods.Sdk;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Settings;
using NSubstitute;
using R3;

namespace NexusMods.Networking.NexusWebApi.Tests;

/// <summary>
/// The credential tests share the process environment (NEXUS_API_KEY), so they run in a single
/// collection and start from a store with no credentials.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class AuthStateCollection
{
    public const string Name = "Auth state";
}

/// <summary>
/// A store that behaves like <see cref="InMemorySecretStore"/> but never tells anyone about changes,
/// the way a write made by another process is invisible to this one.
/// </summary>
internal sealed class SilentSecretStore(InMemorySecretStore inner) : ISecretStore
{
    public Observable<SecretKey> Changed => Observable.Never<SecretKey>();

    public Observable<SecretStoreStatus> StatusChanged => Observable.Never<SecretStoreStatus>();

    public ValueTask<SecretResult> GetAsync(SecretKey key, CancellationToken cancellationToken) => inner.GetAsync(key, cancellationToken);

    public ValueTask<SecretWriteResult> SetAsync(SecretKey key, string value, CancellationToken cancellationToken) => inner.SetAsync(key, value, cancellationToken);

    public ValueTask<SecretWriteResult> DeleteAsync(SecretKey key, CancellationToken cancellationToken) => inner.DeleteAsync(key, cancellationToken);

    public ValueTask<IReadOnlyList<SecretKey>> ListAsync(CancellationToken cancellationToken) => inner.ListAsync(cancellationToken);

    public ValueTask<SecretStoreStatus> DescribeAsync(CancellationToken cancellationToken) => inner.DescribeAsync(cancellationToken);

    public ValueTask<SecretStoreStatus> UnlockAsync(CancellationToken cancellationToken) => inner.UnlockAsync(cancellationToken);
}

internal sealed class FakeEnvironment(params (string Name, string Value)[] variables) : IEnvironmentReader
{
    private readonly Dictionary<string, string> _variables = variables.ToDictionary(v => v.Name, v => v.Value);

    public string? Get(string name) => _variables.GetValueOrDefault(name);
}

internal static class AuthTestSupport
{
    public static async Task ClearCredentials(ISecretStore store)
    {
        foreach (var key in NexusSecretKeys.All) await store.DeleteAsync(key, CancellationToken.None);
    }

    public static async Task StoreToken(ISecretStore store, string accessToken, TimeSpan validFor, string refreshToken = "stored-refresh")
    {
        var token = new NexusOAuthToken(accessToken, refreshToken, DateTimeOffset.UtcNow + validFor);
        var result = await store.SetAsync(NexusSecretKeys.OAuth, JsonSerializer.Serialize(token), CancellationToken.None);
        if (result is not SecretWriteResult.Written) throw new InvalidOperationException($"Could not store the test token: {result}");
    }

    public static async Task StoreKey(ISecretStore store, string key)
    {
        var result = await store.SetAsync(NexusSecretKeys.ApiKey, key, CancellationToken.None);
        if (result is not SecretWriteResult.Written) throw new InvalidOperationException($"Could not store the test key: {result}");
    }

    public static async Task<string?> ReadKey(ISecretStore store)
        => (await store.GetAsync(NexusSecretKeys.ApiKey, CancellationToken.None)) is SecretResult.Found found ? found.Value : null;

    public static async Task<NexusOAuthToken?> ReadToken(ISecretStore store)
        => (await store.GetAsync(NexusSecretKeys.OAuth, CancellationToken.None)) is SecretResult.Found found
            ? JsonSerializer.Deserialize<NexusOAuthToken>(found.Value)
            : null;

    public static JwtTokenReply Reply(string accessToken, string refreshToken = "fresh-refresh") => new()
    {
        AccessToken = accessToken,
        RefreshToken = refreshToken,
        Type = "Bearer",
        ExpiresIn = 3600,
        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    };

    public static OAuth CreateOAuth(IJobMonitor jobMonitor, HttpMessageHandler handler, string oauthClientId = "test-client")
    {
        var settings = CreateSettings(oauthClientId);
        return new OAuth(jobMonitor, NullLogger<OAuth>.Instance, new HttpClient(handler), Substitute.For<IIDGenerator>(), Substitute.For<IOSInterop>(), settings);
    }

    public static ISettingsManager CreateSettings(string oauthClientId)
    {
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<NexusAuthSettings>(Arg.Any<string?>()).Returns(new NexusAuthSettings { OAuthClientId = oauthClientId });
        return settings;
    }

    public static NexusCredentialCache CreateCache(ISecretStore store) => new(store, NullLogger<NexusCredentialCache>.Instance);

    public static SecretStoreOptions Options(SecretStoreMode mode = SecretStoreMode.Interactive)
        => new() { Mode = mode, Backend = SecretBackendChoice.Memory };

    public static OAuth2MessageFactory CreateOAuthFactory(NexusCredentialCache cache, OAuth oauth, SecretStoreMode mode = SecretStoreMode.Interactive)
        => new(cache, oauth, Options(mode), NullLogger<OAuth2MessageFactory>.Instance);

    public static CredentialRoutedMessageFactory CreateRouter(NexusCredentialCache cache, OAuth oauth, SecretStoreMode mode = SecretStoreMode.Interactive)
    {
        return new CredentialRoutedMessageFactory(
            cache,
            new ApiKeyMessageFactory(cache),
            CreateOAuthFactory(cache, oauth, mode)
        );
    }
}
