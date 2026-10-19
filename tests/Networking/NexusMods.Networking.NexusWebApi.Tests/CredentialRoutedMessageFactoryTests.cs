using FluentAssertions;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.NexusWebApi.Auth;
using NexusMods.Sdk.Jobs;

namespace NexusMods.Networking.NexusWebApi.Tests;

[Collection(AuthStateCollection.Name)]
public class CredentialRoutedMessageFactoryTests : IAsyncLifetime
{
    private static readonly Uri RequestUri = new("https://example.invalid/graphql");

    private readonly IConnection _conn;
    private readonly ApiKeyMessageFactory _keyFactory;
    private readonly IAuthenticatingMessageFactory _router;
    private string? _savedEnvironmentKey;

    public CredentialRoutedMessageFactoryTests(IConnection conn, IJobMonitor jobMonitor)
    {
        _conn = conn;
        _keyFactory = new ApiKeyMessageFactory(conn);
        _router = AuthTestSupport.CreateRouter(conn, AuthTestSupport.CreateOAuth(jobMonitor, new MockHttpMessageHandler()));
    }

    public async ValueTask InitializeAsync()
    {
        // The environment key counts as a stored key, so a developer's own NEXUS_API_KEY would change every result here.
        _savedEnvironmentKey = Environment.GetEnvironmentVariable(ApiKeyMessageFactory.NexusApiKeyEnvironmentVariable);
        Environment.SetEnvironmentVariable(ApiKeyMessageFactory.NexusApiKeyEnvironmentVariable, null);
        await AuthTestSupport.ClearCredentials(_conn);
    }

    public async ValueTask DisposeAsync()
    {
        await AuthTestSupport.ClearCredentials(_conn);
        Environment.SetEnvironmentVariable(ApiKeyMessageFactory.NexusApiKeyEnvironmentVariable, _savedEnvironmentKey);
    }

    private static HttpRequestMessage NewRequest() => new(HttpMethod.Post, RequestUri);

    [Fact]
    public async Task NoCredentialIsNotAuthenticatedAndLeavesRequestUntouched()
    {
        (await _router.IsAuthenticated()).Should().BeFalse();

        using var request = NewRequest();
        await _router.ApplyAuthentication(request, CancellationToken.None);

        request.Headers.Contains("apikey").Should().BeFalse();
        request.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task KeyFactoryWithoutKeyLeavesRequestAnonymous()
    {
        // The key factory is also resolvable on its own, so it must honour the pass-through itself
        // rather than rely on the router never calling it without a key.
        using var request = NewRequest();
        await _keyFactory.ApplyAuthentication(request, CancellationToken.None);

        request.Headers.Contains("apikey").Should().BeFalse();
    }

    [Fact]
    public async Task StoredKeyAddsApiKeyHeader()
    {
        await _keyFactory.SetApiKey("the-key");

        (await _router.IsAuthenticated()).Should().BeTrue();
        using var request = NewRequest();
        await _router.ApplyAuthentication(request, CancellationToken.None);

        request.Headers.GetValues("apikey").Should().ContainSingle().Which.Should().Be("the-key");
        request.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task StoredTokenAddsBearerHeader()
    {
        await AuthTestSupport.StoreToken(_conn, "the-token", TimeSpan.FromHours(1));

        (await _router.IsAuthenticated()).Should().BeTrue();
        using var request = NewRequest();
        await _router.ApplyAuthentication(request, CancellationToken.None);

        request.Headers.Authorization.Should().NotBeNull();
        request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        request.Headers.Authorization.Parameter.Should().Be("the-token");
        request.Headers.Contains("apikey").Should().BeFalse();
    }

    [Fact]
    public async Task KeyWinsOverToken()
    {
        await AuthTestSupport.StoreToken(_conn, "the-token", TimeSpan.FromHours(1));
        await _keyFactory.SetApiKey("the-key");

        using var request = NewRequest();
        await _router.ApplyAuthentication(request, CancellationToken.None);

        request.Headers.GetValues("apikey").Should().ContainSingle().Which.Should().Be("the-key");
        request.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task StoringKeyAfterConstructionSwitchesCredential()
    {
        using (var before = NewRequest())
        {
            await _router.ApplyAuthentication(before, CancellationToken.None);
            before.Headers.Contains("apikey").Should().BeFalse();
        }

        await _keyFactory.SetApiKey("late-key");

        using var after = NewRequest();
        await _router.ApplyAuthentication(after, CancellationToken.None);
        after.Headers.GetValues("apikey").Should().ContainSingle().Which.Should().Be("late-key");
    }

    [Fact]
    public async Task CreateWithoutCredentialKeepsFailingLoudly()
    {
        Func<Task> call = async () => await _router.Create(HttpMethod.Get, RequestUri);
        await call.Should().ThrowAsync<Exception>().WithMessage("No API key set");
    }

    [Fact]
    public async Task CreateWithStoredKeyAddsApiKeyHeader()
    {
        await _keyFactory.SetApiKey("the-key");

        using var request = await _router.Create(HttpMethod.Get, RequestUri);
        request.Headers.GetValues("apikey").Should().ContainSingle().Which.Should().Be("the-key");
    }
}
