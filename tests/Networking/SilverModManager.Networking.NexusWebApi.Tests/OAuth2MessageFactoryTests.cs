using System.Net;
using System.Text.Json;
using FluentAssertions;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Networking.NexusWebApi.Auth;
using SilverModManager.Sdk.Jobs;
using NSubstitute;

namespace SilverModManager.Networking.NexusWebApi.Tests;

public class OAuth2MessageFactoryTests : IDisposable
{
    private readonly IJobMonitor _jobMonitor;
    private readonly InMemorySecretStore _inner = new();
    private readonly List<NexusCredentialCache> _caches = [];

    public OAuth2MessageFactoryTests(IJobMonitor jobMonitor)
    {
        _jobMonitor = jobMonitor;
    }

    public void Dispose()
    {
        foreach (var cache in _caches) cache.Dispose();
    }

    private NexusCredentialCache Cache(ISecretStore store)
    {
        var cache = AuthTestSupport.CreateCache(store);
        _caches.Add(cache);
        return cache;
    }

    private static MockHttpMessageHandler RefreshEndpoint(List<HttpRequestMessage> seen, Func<HttpResponseMessage> reply)
    {
        var handler = Substitute.ForPartsOf<MockHttpMessageHandler>();
        handler.SendMock(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            seen.Add(call.Arg<HttpRequestMessage>());
            return Task.FromResult(reply());
        });
        return handler;
    }

    private static HttpResponseMessage Accepted(string accessToken) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(AuthTestSupport.Reply(accessToken))),
    };

    private static HttpResponseMessage Rejected() => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent("""{"error":"invalid_grant"}"""),
    };

    [Fact]
    public async Task AValidTokenIsUsedAsIs()
    {
        await AuthTestSupport.StoreToken(_inner, "good", TimeSpan.FromHours(1));
        var seen = new List<HttpRequestMessage>();
        var factory = AuthTestSupport.CreateOAuthFactory(Cache(_inner), AuthTestSupport.CreateOAuth(_jobMonitor, RefreshEndpoint(seen, Rejected)));

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/");
        await factory.ApplyAuthentication(request, CancellationToken.None);

        request.Headers.Authorization!.Parameter.Should().Be("good");
        seen.Should().BeEmpty();
    }

    [Fact]
    public async Task ANonInteractiveProcessWithAStoreItCannotWriteToDoesNotRefresh()
    {
        await AuthTestSupport.StoreToken(_inner, "stale", TimeSpan.FromHours(-1));
        _inner.Status = _inner.Status with { Writable = false, Locked = true };
        var seen = new List<HttpRequestMessage>();
        var factory = AuthTestSupport.CreateOAuthFactory(Cache(_inner), AuthTestSupport.CreateOAuth(_jobMonitor, RefreshEndpoint(seen, () => Accepted("fresh"))), SecretStoreMode.NonInteractive);

        (await factory.IsAuthenticated()).Should().BeFalse();

        seen.Should().BeEmpty("a refresh whose result cannot be stored would burn the stored refresh token");
        (await AuthTestSupport.ReadToken(_inner))!.RefreshToken.Should().Be("stored-refresh");
    }

    [Fact]
    public async Task AnInteractiveProcessRefreshesEvenWhenTheStoreIsNotWritable()
    {
        await AuthTestSupport.StoreToken(_inner, "stale", TimeSpan.FromHours(-1));
        _inner.Status = _inner.Status with { Writable = false, Locked = true };
        var seen = new List<HttpRequestMessage>();
        var cache = Cache(_inner);
        var factory = AuthTestSupport.CreateOAuthFactory(cache, AuthTestSupport.CreateOAuth(_jobMonitor, RefreshEndpoint(seen, () => Accepted("fresh"))));

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/");
        await factory.ApplyAuthentication(request, CancellationToken.None);

        seen.Should().ContainSingle();
        request.Headers.Authorization!.Parameter.Should().Be("fresh");
        (await cache.GetTokenAsync(CancellationToken.None))!.AccessToken.Should().Be("fresh");
    }

    [Fact]
    public async Task ANonInteractiveProcessWithAWritableStoreRefreshesAndStoresTheToken()
    {
        await AuthTestSupport.StoreToken(_inner, "stale", TimeSpan.FromHours(-1));
        var seen = new List<HttpRequestMessage>();
        var factory = AuthTestSupport.CreateOAuthFactory(Cache(_inner), AuthTestSupport.CreateOAuth(_jobMonitor, RefreshEndpoint(seen, () => Accepted("fresh"))), SecretStoreMode.NonInteractive);

        (await factory.IsAuthenticated()).Should().BeTrue();

        seen.Should().ContainSingle();
        (await AuthTestSupport.ReadToken(_inner))!.AccessToken.Should().Be("fresh");
    }

    [Fact]
    public async Task ARefreshReadsTheStoreFirstAndUsesATokenAnotherProcessAlreadyRefreshed()
    {
        await AuthTestSupport.StoreToken(_inner, "stale", TimeSpan.FromHours(-1));
        var silent = new SilentSecretStore(_inner);
        var cache = Cache(silent);
        await cache.EnsureLoadedAsync(CancellationToken.None);
        // Written by another process: this one is not told.
        await AuthTestSupport.StoreToken(_inner, "refreshed-elsewhere", TimeSpan.FromHours(1), refreshToken: "elsewhere-refresh");
        var seen = new List<HttpRequestMessage>();
        var factory = AuthTestSupport.CreateOAuthFactory(cache, AuthTestSupport.CreateOAuth(_jobMonitor, RefreshEndpoint(seen, () => Accepted("fresh"))));

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/");
        await factory.ApplyAuthentication(request, CancellationToken.None);

        request.Headers.Authorization!.Parameter.Should().Be("refreshed-elsewhere");
        seen.Should().BeEmpty("Nexus is not asked when the store already holds a usable token");
    }

    [Fact]
    public async Task ARefreshUsesTheRefreshTokenFromTheStoreNotTheCachedOne()
    {
        await AuthTestSupport.StoreToken(_inner, "stale", TimeSpan.FromHours(-1), refreshToken: "old-refresh");
        var silent = new SilentSecretStore(_inner);
        var cache = Cache(silent);
        await cache.EnsureLoadedAsync(CancellationToken.None);
        await AuthTestSupport.StoreToken(_inner, "also-stale", TimeSpan.FromHours(-1), refreshToken: "newer-refresh");
        var seen = new List<string>();
        var handler = Substitute.ForPartsOf<MockHttpMessageHandler>();
        handler.SendMock(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            seen.Add(await call.Arg<HttpRequestMessage>().Content!.ReadAsStringAsync());
            return Accepted("fresh");
        });
        var factory = AuthTestSupport.CreateOAuthFactory(cache, AuthTestSupport.CreateOAuth(_jobMonitor, handler));

        await factory.IsAuthenticated();

        seen.Should().ContainSingle().Which.Should().Contain("refresh_token=newer-refresh");
    }

    [Fact]
    public async Task ARejectedRefreshReadsTheStoreOnceMoreAndUsesWhatAnotherProcessStored()
    {
        await AuthTestSupport.StoreToken(_inner, "stale", TimeSpan.FromHours(-1));
        var silent = new SilentSecretStore(_inner);
        var cache = Cache(silent);
        var seen = new List<HttpRequestMessage>();
        var handler = RefreshEndpoint(seen, () =>
        {
            // The other process wins the race while this request is in flight.
            AuthTestSupport.StoreToken(_inner, "won-the-race", TimeSpan.FromHours(1)).GetAwaiter().GetResult();
            return Rejected();
        });
        var factory = AuthTestSupport.CreateOAuthFactory(cache, AuthTestSupport.CreateOAuth(_jobMonitor, handler));

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/");
        await factory.ApplyAuthentication(request, CancellationToken.None);

        request.Headers.Authorization!.Parameter.Should().Be("won-the-race");
        seen.Should().ContainSingle();
    }

    [Fact]
    public async Task ARejectedRefreshWithNothingNewerReadsTheStoreOnceAndGivesUp()
    {
        await AuthTestSupport.StoreToken(_inner, "stale", TimeSpan.FromHours(-1));
        var cache = Cache(_inner);
        var seen = new List<HttpRequestMessage>();
        var factory = AuthTestSupport.CreateOAuthFactory(cache, AuthTestSupport.CreateOAuth(_jobMonitor, RefreshEndpoint(seen, Rejected)));

        (await factory.IsAuthenticated()).Should().BeFalse();

        seen.Should().ContainSingle();
        // The load, the read before the refresh and the one read after the rejection.
        _inner.Calls.Count(c => c == "Get nexus.oauth").Should().Be(3);
        (await AuthTestSupport.ReadToken(_inner))!.AccessToken.Should().Be("stale");
    }

    [Fact]
    public async Task WithoutAClientIdAnExpiredTokenIsLeftAloneWithoutAStoreRead()
    {
        await AuthTestSupport.StoreToken(_inner, "stale", TimeSpan.FromHours(-1));
        var http = Substitute.ForPartsOf<MockHttpMessageHandler>();
        var factory = AuthTestSupport.CreateOAuthFactory(Cache(_inner), AuthTestSupport.CreateOAuth(_jobMonitor, http, oauthClientId: ""));

        (await factory.IsAuthenticated()).Should().BeFalse();

        await http.DidNotReceive().SendMock(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>());
        (await AuthTestSupport.ReadToken(_inner))!.AccessToken.Should().Be("stale");
    }

    [Fact]
    public async Task AFailedWriteOfTheRefreshedTokenKeepsItForTheProcess()
    {
        await AuthTestSupport.StoreToken(_inner, "stale", TimeSpan.FromHours(-1));
        var cache = Cache(_inner);
        await cache.EnsureLoadedAsync(CancellationToken.None);
        var seen = new List<HttpRequestMessage>();
        var factory = AuthTestSupport.CreateOAuthFactory(cache, AuthTestSupport.CreateOAuth(_jobMonitor, RefreshEndpoint(seen, () =>
        {
            _inner.Availability = (SecretUnavailableKind.Locked, "wallet is locked");
            return Accepted("fresh");
        })));
        // The store answers the pre-refresh read, then goes away for the write.
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/");
        await factory.ApplyAuthentication(request, CancellationToken.None);

        request.Headers.Authorization!.Parameter.Should().Be("fresh");
        cache.TokenSource.Should().Be(CredentialSource.Memory);
    }
}
