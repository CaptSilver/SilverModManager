using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using R3;
using SilverModManager.Abstractions.Accounts;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Sdk.Settings;
using Xunit;

namespace SilverModManager.Networking.GitHub.Tests;

public class GitHubAccountTests
{
    // Answers GET /user: 200 for the one token it knows, 401 for any other.
    private sealed class UserEndpoint(string acceptedToken, HttpStatusCode otherwise = HttpStatusCode.Unauthorized) : HttpMessageHandler
    {
        public List<(Uri Uri, AuthenticationHeaderValue? Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Headers.Authorization));
            var ok = request.Headers.Authorization is { Scheme: "Bearer" } auth && auth.Parameter == acceptedToken;
            return Task.FromResult(new HttpResponseMessage(ok ? HttpStatusCode.OK : otherwise)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    // Plays the user: pastes a fixed value, but only after the validator has had its say, as the overlay does.
    private sealed class PastingPrompt(string? pasted) : ISecretPrompt
    {
        public string? Title { get; private set; }
        public string? RefusedWith { get; private set; }
        public Uri? Page { get; private set; }

        public async ValueTask<string?> PromptAsync(string title, string instructions, Uri? page, Func<string, CancellationToken, ValueTask<string?>> validate, CancellationToken cancellationToken)
        {
            Title = title;
            Page = page;
            if (pasted is null) return null;
            RefusedWith = await validate(pasted, cancellationToken);
            return RefusedWith is null ? pasted : null;
        }
    }

    private static GitHubApi Api(ISecretStore store, HttpMessageHandler handler)
        => new(NullLogger<GitHubApi>.Instance, new HttpClient(handler), store);

    private static GitHubAccount Account(InMemorySecretStore store, string? pasted = null, string acceptedToken = "ghp_good", PastingPrompt? prompt = null)
        => new(store, prompt ?? new PastingPrompt(pasted), Api(store, new UserEndpoint(acceptedToken)));

    [Fact]
    public async Task WithoutATokenTheRowOffersToAddOne()
    {
        var account = Account(new InMemorySecretStore());
        await account.RefreshAsync(CancellationToken.None);

        account.Status.State.Should().Be(AccountState.SignedOut);
        account.Action!.Label.Should().Be("Add token");
        new AccountButton(account).Current.Should().Be(new ActionButtonState("Not signed in", "Add token"));
    }

    [Fact]
    public async Task AStoredTokenReadsAsSignedInAndOffersToRemoveIt()
    {
        var store = new InMemorySecretStore();
        await store.SetAsync(GitHubSecretKeys.Token, "ghp_good", CancellationToken.None);
        var account = Account(store);
        await account.RefreshAsync(CancellationToken.None);

        account.Status.Should().Be(new AccountStatus(AccountState.SignedIn, "in-memory store"));
        account.Action!.Label.Should().Be("Remove token");
    }

    [Fact]
    public async Task AnAcceptedTokenIsStoredUnderGithubToken()
    {
        var store = new InMemorySecretStore();
        var account = Account(store, pasted: "ghp_good");
        await account.RefreshAsync(CancellationToken.None);

        await account.Action!.Run(CancellationToken.None);

        (await store.GetAsync(GitHubSecretKeys.Token, CancellationToken.None)).Should().BeOfType<SecretResult.Found>().Which.Value.Should().Be("ghp_good");
        account.Status.State.Should().Be(AccountState.SignedIn);
    }

    [Fact]
    public async Task ThePromptIsTitledWithTheNameOfTheSettingsRow()
    {
        var prompt = new PastingPrompt("ghp_good");
        var account = Account(new InMemorySecretStore(), prompt: prompt);
        await account.RefreshAsync(CancellationToken.None);

        await account.Action!.Run(CancellationToken.None);

        prompt.Title.Should().Be("GitHub");
        prompt.Page.Should().Be(new Uri("https://github.com/settings/tokens"), "the prompt links the page the key is made on");
    }

    [Fact]
    public async Task ARejectedTokenIsNotStoredAndTheUserIsToldWhy()
    {
        var store = new InMemorySecretStore();
        var prompt = new PastingPrompt("ghp_bad");
        var account = Account(store, prompt: prompt);
        await account.RefreshAsync(CancellationToken.None);

        await account.Action!.Run(CancellationToken.None);

        prompt.RefusedWith.Should().Be("GitHub did not accept this token.");
        (await store.GetAsync(GitHubSecretKeys.Token, CancellationToken.None)).Should().BeOfType<SecretResult.NotFound>();
    }

    [Fact]
    public async Task ACancelledPromptStoresNothing()
    {
        var store = new InMemorySecretStore();
        var account = Account(store, pasted: null);
        await account.RefreshAsync(CancellationToken.None);

        await account.Action!.Run(CancellationToken.None);

        store.Calls.Should().NotContain(call => call.StartsWith("Set "));
    }

    [Fact]
    public async Task ATokenGitHubCannotBeReachedToCheckIsReportedNotStored()
    {
        var store = new InMemorySecretStore();
        var prompt = new PastingPrompt("ghp_good");
        var account = new GitHubAccount(store, prompt, Api(store, new UserEndpoint("nobody", HttpStatusCode.ServiceUnavailable)));
        await account.RefreshAsync(CancellationToken.None);

        await account.Action!.Run(CancellationToken.None);

        prompt.RefusedWith.Should().StartWith("Could not check the token with GitHub");
        store.Calls.Should().NotContain(call => call.StartsWith("Set "));
    }

    [Fact]
    public async Task RemovingTheTokenDeletesIt()
    {
        var store = new InMemorySecretStore();
        await store.SetAsync(GitHubSecretKeys.Token, "ghp_good", CancellationToken.None);
        var account = Account(store);
        await account.RefreshAsync(CancellationToken.None);

        await account.Action!.Run(CancellationToken.None);

        (await store.GetAsync(GitHubSecretKeys.Token, CancellationToken.None)).Should().BeOfType<SecretResult.NotFound>();
        account.Status.State.Should().Be(AccountState.SignedOut);
    }

    [Fact]
    public async Task ALockedStoreReadsAsUnavailableNotSignedOut()
    {
        var store = new InMemorySecretStore { Availability = (SecretUnavailableKind.Locked, "The keyring is locked.") };
        var account = Account(store);
        await account.RefreshAsync(CancellationToken.None);

        account.Status.State.Should().Be(AccountState.Unavailable);
        account.WhyNotAvailable.Should().Contain("The keyring is locked.");
        new AccountButton(account).Current.ButtonLabel.Should().BeNull();
    }

    [Fact]
    public async Task ARowChangeAnnouncesItself()
    {
        var store = new InMemorySecretStore();
        var account = Account(store);
        await account.RefreshAsync(CancellationToken.None);
        var seen = new List<AccountState>();
        using var subscription = account.Changed.Subscribe(_ => seen.Add(account.Status.State));

        await store.SetAsync(GitHubSecretKeys.Token, "ghp_good", CancellationToken.None);
        await account.RefreshAsync(CancellationToken.None);

        seen.Should().Contain(AccountState.SignedIn);
    }

    [Fact]
    public async Task ValidateTokenSendsTheTokenAsABearerHeaderToTheUserEndpoint()
    {
        var handler = new UserEndpoint("ghp_good");

        (await Api(new InMemorySecretStore(), handler).ValidateToken("ghp_good", CancellationToken.None)).Should().BeTrue();

        handler.Requests.Should().ContainSingle().Which.Should().Be((new Uri("https://api.github.com/user"), new AuthenticationHeaderValue("Bearer", "ghp_good")));
    }

    [Fact]
    public async Task ValidateTokenIsFalseForARefusedToken()
    {
        (await Api(new InMemorySecretStore(), new UserEndpoint("ghp_good")).ValidateToken("ghp_bad", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ValidateTokenDoesNotFallBackToTheStoredToken()
    {
        // The stored token is the one being replaced; a stored good one must not make a bad paste look fine.
        var store = new InMemorySecretStore();
        await store.SetAsync(GitHubSecretKeys.Token, "ghp_good", CancellationToken.None);

        (await Api(store, new UserEndpoint("ghp_good")).ValidateToken("ghp_bad", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ValidateTokenThrowsWhenGitHubCannotAnswer()
    {
        var api = Api(new InMemorySecretStore(), new UserEndpoint("x", HttpStatusCode.ServiceUnavailable));

        await api.Invoking(a => a.ValidateToken("ghp_good", CancellationToken.None).AsTask()).Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public void TheAccountRowSitsOnTheDeviceFlowClientIdAndTheIdIsEmptyByDefault()
    {
        new GitHubAuthSettings().DeviceFlowClientId.Should().BeEmpty();
        GitHubAuthSettings.SettingsId.Should().Be("SilverModManager.Networking.GitHub.GitHubAuthSettings");
    }
}
