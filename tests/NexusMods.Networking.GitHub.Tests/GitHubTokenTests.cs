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
}
