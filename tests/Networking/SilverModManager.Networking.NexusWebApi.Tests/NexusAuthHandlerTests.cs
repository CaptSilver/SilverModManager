using System.Net;
using System.Text.Json;
using FluentAssertions;
using SilverModManager.Abstractions.NexusWebApi.DTOs.OAuth;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Networking.NexusWebApi.Auth;
using SilverModManager.Sdk.Jobs;
using NSubstitute;

namespace SilverModManager.Networking.NexusWebApi.Tests;

[Collection(AuthStateCollection.Name)]
public class NexusAuthHandlerTests : IAsyncLifetime
{
    private readonly InMemorySecretStore _store = new();
    private readonly NexusCredentialCache _cache;
    private readonly IJobMonitor _jobMonitor;
    private string? _savedEnvironmentKey;

    public NexusAuthHandlerTests(IJobMonitor jobMonitor)
    {
        _jobMonitor = jobMonitor;
        _cache = AuthTestSupport.CreateCache(_store);
    }

    public async ValueTask InitializeAsync()
    {
        _savedEnvironmentKey = Environment.GetEnvironmentVariable(NexusSecretKeys.ApiKeyEnvironmentVariable);
        Environment.SetEnvironmentVariable(NexusSecretKeys.ApiKeyEnvironmentVariable, null);
        await AuthTestSupport.ClearCredentials(_store);
    }

    public async ValueTask DisposeAsync()
    {
        _cache.Dispose();
        await AuthTestSupport.ClearCredentials(_store);
        Environment.SetEnvironmentVariable(NexusSecretKeys.ApiKeyEnvironmentVariable, _savedEnvironmentKey);
    }

    private static MockHttpMessageHandler RecordingHandler(List<HttpRequestMessage> seen, HttpStatusCode status = HttpStatusCode.OK)
    {
        var inner = Substitute.ForPartsOf<MockHttpMessageHandler>();
        inner.SendMock(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            seen.Add(call.Arg<HttpRequestMessage>());
            return Task.FromResult(new HttpResponseMessage(status));
        });
        return inner;
    }

    private HttpClient CreateClient(MockHttpMessageHandler inner, HttpMessageHandler oauthHandler)
    {
        var router = AuthTestSupport.CreateRouter(_cache, AuthTestSupport.CreateOAuth(_jobMonitor, oauthHandler));
        var handler = new NexusAuthHandler(router) { InnerHandler = inner };
        return new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/") };
    }

    [Fact]
    public async Task StoredKeySendsApiKeyOnGraphQlShapedPost()
    {
        await new ApiKeyMessageFactory(_cache).SetApiKey("the-key");
        var seen = new List<HttpRequestMessage>();
        using var client = CreateClient(RecordingHandler(seen), new MockHttpMessageHandler());

        using var content = new StringContent("{\"query\":\"{ __typename }\"}", System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("graphql", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        seen.Should().ContainSingle();
        seen[0].Headers.GetValues("apikey").Should().ContainSingle().Which.Should().Be("the-key");
    }

    [Fact]
    public async Task NoCredentialStillSendsRequestWithoutAuthHeader()
    {
        var seen = new List<HttpRequestMessage>();
        using var client = CreateClient(RecordingHandler(seen), new MockHttpMessageHandler());

        using var response = await client.PostAsync("graphql", new StringContent("{}"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        seen.Should().ContainSingle();
        seen[0].Headers.Contains("apikey").Should().BeFalse();
        seen[0].Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task ExpiredTokenIsRefreshedBeforeTheRequestGoesOut()
    {
        await AuthTestSupport.StoreToken(_store, "stale-token", TimeSpan.FromHours(-1));

        var refresh = Substitute.ForPartsOf<MockHttpMessageHandler>();
        refresh.SendMock(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var reply = new JwtTokenReply
            {
                AccessToken = "fresh-token",
                RefreshToken = "fresh-refresh",
                Type = "Bearer",
                ExpiresIn = 3600,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(reply)) });
        });

        var seen = new List<HttpRequestMessage>();
        using var client = CreateClient(RecordingHandler(seen), refresh);

        using var response = await client.PostAsync("graphql", new StringContent("{}"));

        seen.Should().ContainSingle();
        seen[0].Headers.Authorization.Should().NotBeNull();
        seen[0].Headers.Authorization!.Parameter.Should().Be("fresh-token");
    }
}
