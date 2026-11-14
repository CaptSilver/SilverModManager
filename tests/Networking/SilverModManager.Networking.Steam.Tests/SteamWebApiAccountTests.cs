using System.Net;
using FluentAssertions;
using SilverModManager.Abstractions.Accounts;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Networking.Steam.Workshop;
using Xunit;

namespace SilverModManager.Networking.Steam.Tests;

/// <summary>The "Steam (Workshop browse)" row of Settings → Accounts, over a Web API key the user pastes.</summary>
public class SteamWebApiAccountTests
{
    // Answers QueryFiles: 200 for the one key it knows, 403 for any other.
    private sealed class QueryEndpoint(string acceptedKey, HttpStatusCode otherwise = HttpStatusCode.Forbidden) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var ok = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["key"] == acceptedKey;
            return Task.FromResult(new HttpResponseMessage(ok ? HttpStatusCode.OK : otherwise) { Content = new StringContent("""{"response":{"total":1}}""") });
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

    private static SteamWebApiAccount Account(InMemorySecretStore store, PastingPrompt prompt, HttpMessageHandler? handler = null)
        => new(store, prompt, new SteamWorkshopApi(new HttpClient(handler ?? new QueryEndpoint("good"))));

    [Fact]
    public async Task WithoutAKeyTheRowOffersToAddOne()
    {
        var account = Account(new InMemorySecretStore(), new PastingPrompt(null));
        await account.RefreshAsync(CancellationToken.None);

        account.DisplayName.Should().Be("Steam (Workshop browse)");
        account.Status.State.Should().Be(AccountState.SignedOut);
        account.Action!.Label.Should().Be("Add Steam Web API key");
    }

    [Fact]
    public async Task AStoredKeyReadsAsSignedInAndOffersToRemoveIt()
    {
        var store = new InMemorySecretStore();
        await store.SetAsync(SteamSecretKeys.WebApiKey, "good", CancellationToken.None);
        var account = Account(store, new PastingPrompt(null));
        await account.RefreshAsync(CancellationToken.None);

        account.Status.Should().Be(new AccountStatus(AccountState.SignedIn, "in-memory store"));
        account.Action!.Label.Should().Be("Remove key");
    }

    [Fact]
    public async Task AnAcceptedKeyIsStoredUnderTheWebApiKeySecret()
    {
        var store = new InMemorySecretStore();
        var prompt = new PastingPrompt("good");
        var account = Account(store, prompt);
        await account.RefreshAsync(CancellationToken.None);

        await account.Action!.Run(CancellationToken.None);

        prompt.Title.Should().Be("Steam (Workshop browse)");
        prompt.Page.Should().Be(new Uri("https://steamcommunity.com/dev/apikey"), "the prompt links the page the key is made on");
        (await store.GetAsync(SteamSecretKeys.WebApiKey, CancellationToken.None)).Should().BeOfType<SecretResult.Found>().Which.Value.Should().Be("good");
        account.Status.State.Should().Be(AccountState.SignedIn);
    }

    [Fact]
    public async Task ARejectedKeyIsNotStored()
    {
        var store = new InMemorySecretStore();
        var prompt = new PastingPrompt("bad");
        var account = Account(store, prompt);
        await account.RefreshAsync(CancellationToken.None);

        await account.Action!.Run(CancellationToken.None);

        prompt.RefusedWith.Should().Be("Steam did not accept this key.");
        (await store.GetAsync(SteamSecretKeys.WebApiKey, CancellationToken.None)).Should().BeOfType<SecretResult.NotFound>();
    }

    [Fact]
    public async Task AKeySteamCannotBeAskedAboutIsReportedAndNotStored()
    {
        var store = new InMemorySecretStore();
        var prompt = new PastingPrompt("good");
        var account = Account(store, prompt, new QueryEndpoint("nobody", HttpStatusCode.ServiceUnavailable));
        await account.RefreshAsync(CancellationToken.None);

        await account.Action!.Run(CancellationToken.None);

        prompt.RefusedWith.Should().StartWith("Could not check the key with Steam");
        store.Calls.Should().NotContain(call => call.StartsWith("Set "));
    }

    [Fact]
    public async Task ACancelledPromptStoresNothing()
    {
        var store = new InMemorySecretStore();
        var account = Account(store, new PastingPrompt(null));
        await account.RefreshAsync(CancellationToken.None);

        await account.Action!.Run(CancellationToken.None);

        store.Calls.Should().NotContain(call => call.StartsWith("Set "));
    }

    [Fact]
    public async Task RemovingTheKeyDeletesIt()
    {
        var store = new InMemorySecretStore();
        await store.SetAsync(SteamSecretKeys.WebApiKey, "good", CancellationToken.None);
        var account = Account(store, new PastingPrompt(null));
        await account.RefreshAsync(CancellationToken.None);

        await account.Action!.Run(CancellationToken.None);

        (await store.GetAsync(SteamSecretKeys.WebApiKey, CancellationToken.None)).Should().BeOfType<SecretResult.NotFound>();
        account.Status.State.Should().Be(AccountState.SignedOut);
    }

    [Fact]
    public async Task ALockedStoreReadsAsUnavailableNotSignedOut()
    {
        var store = new InMemorySecretStore { Availability = (SecretUnavailableKind.Locked, "The keyring is locked.") };
        var account = Account(store, new PastingPrompt(null));
        await account.RefreshAsync(CancellationToken.None);

        account.Status.State.Should().Be(AccountState.Unavailable);
        account.WhyNotAvailable.Should().Contain("The keyring is locked.");
        new AccountButton(account).Current.ButtonLabel.Should().BeNull();
    }

    [Fact]
    public void TheKeyHasAnEnvironmentVariableAlias()
    {
        WellKnownSecrets.EnvironmentAliases[SteamSecretKeys.WebApiKey].Should().Be("STEAM_WEB_API_KEY");
        SteamSecretKeys.WebApiKey.Name.Should().Be("steam.web-api-key");
    }
}
