using System.Net;
using FluentAssertions;
using SilverModManager.Abstractions.Accounts;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Networking.ModIo.Tests.Fixtures;
using Microsoft.Extensions.Time.Testing;

namespace SilverModManager.Networking.ModIo.Tests;

/// <summary>The "mod.io" row of Settings → Accounts, over an access token the user pastes.</summary>
public class ModIoAccountTests
{
    // Plays the user: pastes a fixed value, but only after the validator has had its say, as the overlay does.
    private sealed class PastingPrompt(string? pasted) : ISecretPrompt
    {
        public string? Title { get; private set; }
        public string? RefusedWith { get; private set; }

        public async ValueTask<string?> PromptAsync(string title, string instructions, Func<string, CancellationToken, ValueTask<string?>> validate, CancellationToken cancellationToken)
        {
            Title = title;
            if (pasted is null) return null;
            RefusedWith = await validate(pasted, cancellationToken);
            return RefusedWith is null ? pasted : null;
        }
    }

    private static ModIoAccount Account(InMemorySecretStore store, PastingPrompt prompt, HttpStatusCode meStatus = HttpStatusCode.OK)
    {
        var handler = new RecordingHandler().Add(HttpMethod.Get, "/v1/me", meStatus == HttpStatusCode.OK ? ModIoJson.Me : "{}", meStatus);
        var api = new ModIoApi(new HttpClient(handler), store, new HostThrottle(), new FakeTimeProvider(), ModIoSettings.Default);
        return new ModIoAccount(store, prompt, api);
    }

    [Fact]
    public async Task WithoutATokenTheRowOffersToPasteOne()
    {
        var account = Account(new InMemorySecretStore(), new PastingPrompt(null));
        await account.RefreshAsync(CancellationToken.None);

        account.DisplayName.Should().Be("mod.io");
        account.Status.State.Should().Be(AccountState.SignedOut);
        account.Action!.Label.Should().Be("Paste access token");
    }

    [Fact]
    public async Task AStoredTokenReadsAsSignedInAndOffersToRemoveIt()
    {
        var store = new InMemorySecretStore();
        await store.SetAsync(ModIoSecretKeys.Token, "tok", CancellationToken.None);
        var account = Account(store, new PastingPrompt(null));
        await account.RefreshAsync(CancellationToken.None);

        account.Status.State.Should().Be(AccountState.SignedIn);
        account.Action!.Label.Should().Be("Remove token");
    }

    [Fact]
    public async Task AnAcceptedTokenIsStoredUnderTheModIoSecret()
    {
        var store = new InMemorySecretStore();
        var prompt = new PastingPrompt("good");
        var account = Account(store, prompt);
        await account.RefreshAsync(CancellationToken.None);

        await account.Action!.Run(CancellationToken.None);

        prompt.Title.Should().Be("mod.io");
        (await store.GetAsync(ModIoSecretKeys.Token, CancellationToken.None)).Should().BeOfType<SecretResult.Found>().Which.Value.Should().Be("good");
        account.Status.State.Should().Be(AccountState.SignedIn);
    }

    [Fact]
    public async Task APastedTokenIsStoredAsItWasChecked()
    {
        // A copy from a web page often carries a trailing newline, which no request header may hold.
        var store = new InMemorySecretStore();
        var account = Account(store, new PastingPrompt("  good\n"));
        await account.RefreshAsync(CancellationToken.None);

        await account.Action!.Run(CancellationToken.None);

        (await store.GetAsync(ModIoSecretKeys.Token, CancellationToken.None)).Should().BeOfType<SecretResult.Found>().Which.Value.Should().Be("good");
    }

    [Fact]
    public async Task ATokenModIoRejectsIsNotStored()
    {
        var store = new InMemorySecretStore();
        var prompt = new PastingPrompt("bad");
        var account = Account(store, prompt, HttpStatusCode.Unauthorized);
        await account.RefreshAsync(CancellationToken.None);

        await account.Action!.Run(CancellationToken.None);

        prompt.RefusedWith.Should().Be("mod.io did not accept this token.");
        (await store.GetAsync(ModIoSecretKeys.Token, CancellationToken.None)).Should().NotBeOfType<SecretResult.Found>();
    }

    [Fact]
    public async Task AServerErrorIsNotAVerdictOnTheToken()
    {
        var store = new InMemorySecretStore();
        var prompt = new PastingPrompt("maybe");
        var account = Account(store, prompt, HttpStatusCode.InternalServerError);
        await account.RefreshAsync(CancellationToken.None);

        await account.Action!.Run(CancellationToken.None);

        prompt.RefusedWith.Should().StartWith("Could not check the token with mod.io");
        (await store.GetAsync(ModIoSecretKeys.Token, CancellationToken.None)).Should().NotBeOfType<SecretResult.Found>();
    }

    [Fact]
    public async Task RemovingTheTokenSignsTheRowOut()
    {
        var store = new InMemorySecretStore();
        await store.SetAsync(ModIoSecretKeys.Token, "tok", CancellationToken.None);
        var account = Account(store, new PastingPrompt(null));
        await account.RefreshAsync(CancellationToken.None);

        await account.Action!.Run(CancellationToken.None);

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
        account.Action.Should().BeNull();
    }

    [Fact]
    public void TheTokenHasAnEnvironmentVariableAlias()
    {
        WellKnownSecrets.EnvironmentAliases[ModIoSecretKeys.Token].Should().Be("MODIO_TOKEN");
        ModIoSecretKeys.Token.Name.Should().Be("modio.token");
    }
}
