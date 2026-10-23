using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Secrets;
using NexusMods.Sdk;
using NexusMods.Secrets;
using Xunit;

namespace NexusMods.Networking.GitHub.Tests;

public class GitHubTokenTests
{
    private sealed class FakeEnvironment(Dictionary<string, string> values) : IEnvironmentReader
    {
        public string? Get(string name) => values.GetValueOrDefault(name);
    }

    private sealed class HeaderRecordingHandler : HttpMessageHandler
    {
        public List<AuthenticationHeaderValue?> Authorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private static GitHubApi Api(ISecretStore store, HttpMessageHandler? handler = null)
        => new(NullLogger<GitHubApi>.Instance, new HttpClient(handler ?? new HeaderRecordingHandler()), store);

    // The same wiring the app uses: the overlay over an in-memory store, with the key registered by AddGitHubApi.
    private static ServiceProvider Host(Dictionary<string, string> environment, HttpMessageHandler? handler = null) => new ServiceCollection()
        .AddLogging()
        .AddSingleton<IEnvironmentReader>(new FakeEnvironment(environment))
        .AddSingleton(new HttpClient(handler ?? new HeaderRecordingHandler()))
        .AddSecrets(new SecretStoreOptions { Mode = SecretStoreMode.NonInteractive, Backend = SecretBackendChoice.Memory })
        .AddGitHubApi()
        .BuildServiceProvider();

    [Fact]
    public void TheTokenKeyAndItsConventionalVariableAreTheOnesTheOverlayAliases()
    {
        GitHubSecretKeys.Token.Name.Should().Be("github.token");
        GitHubSecretKeys.TokenEnvironmentVariable.Should().Be("GITHUB_TOKEN");
    }

    [Fact]
    public async Task ReadsAStoredToken()
    {
        var store = new InMemorySecretStore();
        await store.SetAsync(GitHubSecretKeys.Token, "ghp_stored", CancellationToken.None);

        (await Api(store).TryGetTokenAsync(CancellationToken.None)).Should().Be("ghp_stored");
    }

    [Fact]
    public async Task HasNoTokenWhenTheStoreHoldsNone()
    {
        (await Api(new InMemorySecretStore()).TryGetTokenAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task HasNoTokenWhenTheStoreCannotAnswer()
    {
        var store = new InMemorySecretStore { Availability = (SecretUnavailableKind.NoBus, "no session bus") };

        (await Api(store).TryGetTokenAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task GithubTokenInTheEnvironmentStillWorksThroughTheOverlayAlias()
    {
        await using var provider = Host(new Dictionary<string, string> { ["GITHUB_TOKEN"] = "ghp_from_environment" });

        var api = (GitHubApi)provider.GetRequiredService<IGitHubApi>();

        (await api.TryGetTokenAsync(CancellationToken.None)).Should().Be("ghp_from_environment");
    }

    [Fact]
    public async Task TheKeysOwnVariableNameAlsoSuppliesIt()
    {
        await using var provider = Host(new Dictionary<string, string> { ["SILVERMM_SECRET_GITHUB_TOKEN"] = "ghp_from_key_variable" });

        var api = (GitHubApi)provider.GetRequiredService<IGitHubApi>();

        (await api.TryGetTokenAsync(CancellationToken.None)).Should().Be("ghp_from_key_variable");
    }

    [Fact]
    public async Task RequestsCarryTheTokenWhenThereIsOne()
    {
        var handler = new HeaderRecordingHandler();
        await using var provider = Host(new Dictionary<string, string> { ["GITHUB_TOKEN"] = "ghp_from_environment" }, handler);

        await provider.GetRequiredService<IGitHubApi>().FetchReleases("o", "r");

        handler.Authorizations.Should().ContainSingle().Which.Should().Be(new AuthenticationHeaderValue("Bearer", "ghp_from_environment"));
    }

    [Fact]
    public async Task RequestsAreAnonymousWhenThereIsNoToken()
    {
        var handler = new HeaderRecordingHandler();
        await using var provider = Host([], handler);

        await provider.GetRequiredService<IGitHubApi>().FetchReleases("o", "r");

        handler.Authorizations.Should().ContainSingle().Which.Should().BeNull();
    }

    [Fact]
    public async Task AHeaderSetOnTheApiWinsOverTheStoredToken()
    {
        var handler = new HeaderRecordingHandler();
        var store = new InMemorySecretStore();
        await store.SetAsync(GitHubSecretKeys.Token, "ghp_stored", CancellationToken.None);
        var api = Api(store, handler);
        api.AuthenticationHeader = new AuthenticationHeaderValue("token", "explicit");

        await api.FetchReleases("o", "r");

        handler.Authorizations.Should().ContainSingle().Which.Should().Be(new AuthenticationHeaderValue("token", "explicit"));
    }

    // Answers 401 to any request that carries a token and 200 to one that doesn't, like GitHub with a revoked token.
    private sealed class RejectsTokensHandler(string body) : HttpMessageHandler
    {
        public List<AuthenticationHeaderValue?> Authorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization);
            return Task.FromResult(request.Headers.Authorization is null
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }

    private sealed class CountingLogger : Microsoft.Extensions.Logging.ILogger<GitHubApi>
    {
        public int Warnings { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning) Warnings++;
        }
    }

    private static async Task<InMemorySecretStore> StoreWithToken()
    {
        var store = new InMemorySecretStore();
        await store.SetAsync(GitHubSecretKeys.Token, "ghp_revoked", CancellationToken.None);
        return store;
    }

    [Fact]
    public async Task ARejectedTokenIsDroppedAndTheReleasesAreFetchedAnonymously()
    {
        // A stale or revoked token must not break update checks or release downloads.
        var handler = new RejectsTokensHandler("[]");
        var api = new GitHubApi(NullLogger<GitHubApi>.Instance, new HttpClient(handler), await StoreWithToken());

        var releases = await api.FetchReleases("o", "r");

        releases.Should().NotBeNull();
        handler.Authorizations.Should().HaveCount(2);
        handler.Authorizations[0].Should().Be(new AuthenticationHeaderValue("Bearer", "ghp_revoked"));
        handler.Authorizations[1].Should().BeNull();
    }

    [Fact]
    public async Task ARejectedTokenIsDroppedForASingleRelease()
    {
        var handler = new RejectsTokensHandler("""{"id": 1, "tag_name": "v1", "name": "v1", "assets": []}""");
        var api = new GitHubApi(NullLogger<GitHubApi>.Instance, new HttpClient(handler), await StoreWithToken());

        var release = await api.FetchReleaseByTag("o", "r", "v1");

        release.Should().NotBeNull();
        handler.Authorizations.Should().HaveCount(2);
        handler.Authorizations[1].Should().BeNull();
    }

    [Fact]
    public async Task AnAnonymousRequestThatIsRefusedIsNotRetried()
    {
        var handler = new RejectsTokensHandler("[]");
        var api = new GitHubApi(NullLogger<GitHubApi>.Instance, new HttpClient(new UnauthorizedHandler(handler)), new InMemorySecretStore());

        (await api.FetchReleases("o", "r")).Should().BeNull();

        handler.Authorizations.Should().ContainSingle();
    }

    private sealed class UnauthorizedHandler(RejectsTokensHandler recorder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            recorder.Authorizations.Add(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }

    [Fact]
    public async Task ARejectedTokenIsReportedOnceNotOnEveryRequest()
    {
        var logger = new CountingLogger();
        var api = new GitHubApi(logger, new HttpClient(new RejectsTokensHandler("[]")), await StoreWithToken());

        await api.FetchReleases("o", "r");
        await api.FetchReleases("o", "r");

        logger.Warnings.Should().Be(1);
    }
}
