using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Sdk.Errors;
using Xunit;

namespace SilverModManager.Networking.GitHub.Tests;

public class RateLimitTests
{
    private const string ReleaseBody = """
    {"url":"u","assets_url":"u","html_url":"https://github.com/o/r/releases/tag/v1","id":1,"tag_name":"v1","target_commitish":"main",
     "name":"One","draft":false,"prerelease":false,"created_at":"2025-01-01T00:00:00Z","published_at":"2025-01-01T00:00:00Z",
     "assets":[],"tarball_url":"u","zipball_url":"u","body":""}
    """;

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(reply(request));
        }
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string body, long? remaining, DateTimeOffset? reset)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        if (remaining is { } left) response.Headers.Add("x-ratelimit-remaining", left.ToString());
        if (reset is { } at) response.Headers.Add("x-ratelimit-reset", at.ToUnixTimeSeconds().ToString());
        return response;
    }

    private static GitHubApi CreateApi(ScriptedHandler handler, FakeTimeProvider time)
        => new(NullLogger<GitHubApi>.Instance, new HttpClient(handler), new InMemorySecretStore(), time);

    [Fact]
    public async Task AUsedUpRateLimitRefusesNamingTheResetTime()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        var reset = time.GetUtcNow().AddMinutes(25);
        var handler = new ScriptedHandler(_ => Reply(HttpStatusCode.Forbidden, "{\"message\":\"API rate limit exceeded\"}", remaining: 0, reset));

        var act = async () => await CreateApi(handler, time).FetchMarkedLatestRelease("o", "r");

        var refusal = (await act.Should().ThrowAsync<GitHubRateLimitedException>()).Which;
        refusal.Should().BeAssignableTo<UserFacingException>();
        refusal.ResetAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(reset.ToUnixTimeSeconds()));
        refusal.Message.Should().Contain(refusal.ResetAt.ToLocalTime().ToString("t")).And.Contain("Settings");
    }

    [Fact]
    public async Task ARefusalWithoutAnEmptyBudgetIsAnOrdinaryFailure()
    {
        var time = new FakeTimeProvider();
        var handler = new ScriptedHandler(_ => Reply(HttpStatusCode.Forbidden, "{}", remaining: 12, DateTimeOffset.UnixEpoch));

        var release = await CreateApi(handler, time).FetchMarkedLatestRelease("o", "r");

        release.Should().BeNull();
    }

    [Fact]
    public async Task AnAnswerThatSpentTheLastRequestIsStillReturned()
    {
        var time = new FakeTimeProvider();
        var handler = new ScriptedHandler(_ => Reply(HttpStatusCode.OK, ReleaseBody, remaining: 0, time.GetUtcNow().AddMinutes(10)));

        var release = await CreateApi(handler, time).FetchMarkedLatestRelease("o", "r");

        release.Should().NotBeNull();
        release!.TagName.Should().Be("v1");
    }

    [Fact]
    public async Task NothingIsSentUntilTheResetTimeOnceTheBudgetIsSpent()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        var handler = new ScriptedHandler(_ => Reply(HttpStatusCode.OK, ReleaseBody, remaining: 0, time.GetUtcNow().AddMinutes(10)));
        var api = CreateApi(handler, time);
        await api.FetchMarkedLatestRelease("o", "r");

        var tooEarly = async () => await api.FetchMarkedLatestRelease("o", "r");
        await tooEarly.Should().ThrowAsync<GitHubRateLimitedException>();
        handler.Requests.Should().Be(1, "the second request is refused before it is sent");

        time.Advance(TimeSpan.FromMinutes(11));
        (await api.FetchMarkedLatestRelease("o", "r")).Should().NotBeNull();
        handler.Requests.Should().Be(2);
    }

    [Fact]
    public async Task ATokenAddedAfterTheAnonymousBudgetRanOutIsUsedRightAway()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        var handler = new ScriptedHandler(request => request.Headers.Authorization is null
            ? Reply(HttpStatusCode.Forbidden, "{\"message\":\"API rate limit exceeded\"}", remaining: 0, time.GetUtcNow().AddMinutes(40))
            : Reply(HttpStatusCode.OK, ReleaseBody, remaining: 4999, time.GetUtcNow().AddMinutes(60)));
        var api = CreateApi(handler, time);
        var anonymous = async () => await api.FetchMarkedLatestRelease("o", "r");
        await anonymous.Should().ThrowAsync<GitHubRateLimitedException>();

        api.AuthenticationHeader = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "token");

        (await api.FetchMarkedLatestRelease("o", "r")).Should().NotBeNull("a token has a budget of its own");
        handler.Requests.Should().Be(2);
    }

    [Fact]
    public async Task TheRefusalAlsoComesFromTheListOfReleases()
    {
        var time = new FakeTimeProvider();
        var handler = new ScriptedHandler(_ => Reply(HttpStatusCode.TooManyRequests, "{}", remaining: 0, time.GetUtcNow().AddMinutes(3)));

        var act = async () => await CreateApi(handler, time).FetchReleases("o", "r");

        await act.Should().ThrowAsync<GitHubRateLimitedException>();
    }

    [Fact]
    public async Task TheRepositoryIdIsReadFromTheRepositoryEndpoint()
    {
        var time = new FakeTimeProvider();
        Uri? asked = null;
        var handler = new ScriptedHandler(request =>
        {
            asked = request.RequestUri;
            return Reply(HttpStatusCode.OK, "{\"id\":540883721,\"name\":\"me3\"}", remaining: 50, null);
        });

        var id = await CreateApi(handler, time).FetchRepositoryId("garyttierney", "me3");

        id.Should().Be(540883721UL);
        asked!.ToString().Should().Be("https://api.github.com/repos/garyttierney/me3");
    }

    [Fact]
    public async Task AMissingRepositoryHasNoId()
    {
        var handler = new ScriptedHandler(_ => Reply(HttpStatusCode.NotFound, "{}", remaining: 50, null));

        (await CreateApi(handler, new FakeTimeProvider()).FetchRepositoryId("o", "gone")).Should().BeNull();
    }
}
