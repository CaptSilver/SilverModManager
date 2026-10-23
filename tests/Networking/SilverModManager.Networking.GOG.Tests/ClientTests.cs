using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.GOG.Values;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Abstractions.Secrets;
using NexusMods.Sdk;
using NSubstitute;

namespace NexusMods.Networking.GOG.Tests;

public class ClientTests : IDisposable
{
    private readonly InMemorySecretStore _store = new();
    private readonly GogCredentialCache _cache;
    private readonly ScriptedHandler _handler = new();
    private readonly Client _client;

    public ClientTests()
    {
        _cache = new GogCredentialCache(_store, NullLogger<GogCredentialCache>.Instance);
        _client = new Client(NullLogger<Client>.Instance, _cache, Substitute.For<IOSInterop>(), new HttpClient(_handler), new JsonSerializerOptions());
    }

    public void Dispose() => _cache.Dispose();

    private async Task<GogCredential?> Stored()
        => await _store.GetAsync(GogSecretKeys.OAuth, CancellationToken.None) is SecretResult.Found found && GogCredential.TryParse(found.Value, out var credential) ? credential : null;

    private async Task StoreCredential(string accessToken, DateTimeOffset expiresAt)
        => await _store.SetAsync(GogSecretKeys.OAuth, new GogCredential(accessToken, "old-refresh", "old-session", 7UL, expiresAt).ToJson(), CancellationToken.None);

    [Fact]
    public async Task ALoginResponseIsStoredAsOneValueUnderTheOAuthKey()
    {
        _handler.Token = TokenJson("new-access", "new-refresh", "new-session", "42", 3600);
        _client.AuthUrl(new NXMGogAuthUrl(new Uri("nxm://gog-auth/?code=abc")));

        await _client.Login(CancellationToken.None);

        var stored = await Stored();
        stored.Should().NotBeNull();
        stored!.AccessToken.Should().Be("new-access");
        stored.RefreshToken.Should().Be("new-refresh");
        stored.SessionId.Should().Be("new-session");
        stored.UserId.Should().Be(42UL);
        stored.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddSeconds(3600), TimeSpan.FromSeconds(30));
        _store.Calls.Count(call => call.StartsWith("Set ")).Should().Be(1);
        (await _store.ListAsync(CancellationToken.None)).Should().Equal(GogSecretKeys.OAuth);
    }

    [Fact]
    public async Task ARefreshReplacesTheOneValueAndTheOldAccessTokenIsGone()
    {
        await StoreCredential("old-access", DateTimeOffset.UtcNow.AddSeconds(30));
        _handler.Token = TokenJson("fresh-access", "fresh-refresh", "fresh-session", "7", 3600);

        await _client.GetBuilds(ProductId.From(1), OS.windows, CancellationToken.None);

        var stored = await Stored();
        stored!.AccessToken.Should().Be("fresh-access");
        stored.RefreshToken.Should().Be("fresh-refresh");
        (await _store.ListAsync(CancellationToken.None)).Should().Equal(GogSecretKeys.OAuth);
        var found = (SecretResult.Found)await _store.GetAsync(GogSecretKeys.OAuth, CancellationToken.None);
        found.Value.Should().NotContain("old-access");
        _handler.RefreshTokensSent.Should().Equal("old-refresh");
    }

    [Fact]
    public async Task ATokenOneMinuteFromExpiryIsRefreshedBeforeTheRequest()
    {
        await StoreCredential("old-access", DateTimeOffset.UtcNow.AddSeconds(50));
        _handler.Token = TokenJson("fresh-access", "fresh-refresh", "s", "7", 3600);

        await _client.GetBuilds(ProductId.From(1), OS.windows, CancellationToken.None);

        _handler.Requests.Select(r => r.Host).Should().Equal("auth.gog.com", "content-system.gog.com");
        _handler.BearerTokens.Should().Equal("fresh-access");
    }

    [Fact]
    public async Task AValidTokenIsUsedWithoutARefresh()
    {
        await StoreCredential("good-access", DateTimeOffset.UtcNow.AddMinutes(10));

        await _client.GetBuilds(ProductId.From(1), OS.windows, CancellationToken.None);

        _handler.Requests.Select(r => r.Host).Should().Equal("content-system.gog.com");
        _handler.BearerTokens.Should().Equal("good-access");
        _store.Calls.Count(call => call.StartsWith("Set ")).Should().Be(1, "only the test's own seeding wrote");
    }

    [Fact]
    public async Task ARequestWithoutALoginFailsLoudly()
    {
        var act = async () => await _client.GetBuilds(ProductId.From(1), OS.windows, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*authentication*");
    }

    [Fact]
    public async Task LogOutDeletesTheKey()
    {
        await StoreCredential("old-access", DateTimeOffset.UtcNow.AddMinutes(10));

        await _client.LogOut();

        (await _store.GetAsync(GogSecretKeys.OAuth, CancellationToken.None)).Should().BeOfType<SecretResult.NotFound>();
        _store.Calls.Should().Contain("Delete gog.oauth");
        (await _cache.GetAsync(CancellationToken.None)).Should().BeNull();
    }

    private static string TokenJson(string access, string refresh, string session, string userId, int expiresIn)
        => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["access_token"] = access,
            ["token_type"] = "bearer",
            ["expires_in"] = expiresIn,
            ["refresh_token"] = refresh,
            ["scope"] = "",
            ["session_id"] = session,
            ["user_id"] = userId,
        });

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public string Token { get; set; } = "{}";
        public List<Uri> Requests { get; } = [];
        public List<string> BearerTokens { get; } = [];
        public List<string> RefreshTokensSent { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            if (uri.Host == "auth.gog.com")
            {
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                if (query["refresh_token"] is { } refresh) RefreshTokensSent.Add(refresh);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Token, Encoding.UTF8, "application/json") });
            }

            if (request.Headers.Authorization is { Parameter: { } bearer }) BearerTokens.Add(bearer);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
