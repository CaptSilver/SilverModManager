using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Abstractions.Secrets;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.NexusWebApi.Auth;
using NexusMods.Secrets.Overlay;
using NexusMods.Sdk.Jobs;
using NSubstitute;
using R3;

namespace NexusMods.Networking.NexusWebApi.Tests;

[Collection(AuthStateCollection.Name)]
public class LoginManagerTests : IAsyncLifetime
{
    private const string ValidateJson = """{"user_id":42,"key":"k","name":"silver","is_premium":false,"is_supporter":false,"email":"","profile_url":null}""";
    private const string OAuthUserJson = """{"sub":"7","name":"oauth-user","avatar":null,"membership_roles":[]}""";

    private readonly IConnection _conn;
    private readonly IJobMonitor _jobMonitor;
    private readonly InMemorySecretStore _store = new();
    private readonly List<IDisposable> _disposables = [];
    private NexusCredentialCache? _cacheInstance;
    private readonly System.Collections.Concurrent.ConcurrentBag<string> _seenKeys = [];
    private string? _savedEnvironmentKey;

    public LoginManagerTests(IConnection conn, IJobMonitor jobMonitor)
    {
        _conn = conn;
        _jobMonitor = jobMonitor;
    }

    // Created on first use, so a test fills the store before the cache exists: a cache that is already
    // listening would also react to each write while the test is still setting up.
    private NexusCredentialCache _cache => _cacheInstance ??= AuthTestSupport.CreateCache(_store);

    public async ValueTask InitializeAsync()
    {
        _savedEnvironmentKey = Environment.GetEnvironmentVariable(NexusSecretKeys.ApiKeyEnvironmentVariable);
        Environment.SetEnvironmentVariable(NexusSecretKeys.ApiKeyEnvironmentVariable, null);
        await AuthTestSupport.ClearCredentials(_store);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        _cacheInstance?.Dispose();
        await AuthTestSupport.ClearCredentials(_store);
        Environment.SetEnvironmentVariable(NexusSecretKeys.ApiKeyEnvironmentVariable, _savedEnvironmentKey);
    }

    private LoginManager CreateLoginManager(IApiKeyPrompt? prompt = null, string oauthClientId = "", HttpStatusCode validateStatus = HttpStatusCode.OK, NexusCredentialCache? cache = null)
    {
        cache ??= _cache;
        var rest = Substitute.ForPartsOf<MockHttpMessageHandler>();
        rest.SendMock(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<HttpRequestMessage>();

                // The OAuth session belongs to a different account than the keys, so a test can tell
                // which credential the signed-in user came from.
                if (request.RequestUri!.AbsolutePath.EndsWith("/oauth/userinfo", StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OAuthUserJson) });

                // The server rejects one specific key and never answers for another, whatever else is stored.
                var keys = request.Headers.TryGetValues("apikey", out var values) ? values.ToArray() : [];
                foreach (var seen in keys) _seenKeys.Add(seen);
                if (keys.Contains("slow-key")) return Task.FromException<HttpResponseMessage>(new TaskCanceledException("The request timed out"));
                var rejected = keys.Contains("wrong-key");
                return Task.FromResult(new HttpResponseMessage(rejected ? HttpStatusCode.Unauthorized : validateStatus) { Content = new StringContent(ValidateJson) });
            });

        var oauth = AuthTestSupport.CreateOAuth(_jobMonitor, new MockHttpMessageHandler(), oauthClientId);
        var router = AuthTestSupport.CreateRouter(cache, oauth);
        var apiClient = new NexusApiClient(NullLogger<NexusApiClient>.Instance, router, new HttpClient(rest), Substitute.For<IGraphQlClient>());
        var loginManager = new LoginManager(_conn, apiClient, router, oauth, cache, prompt ?? Substitute.For<IApiKeyPrompt>(), NullLogger<LoginManager>.Instance);
        _disposables.Add(loginManager);
        return loginManager;
    }

    /// <summary>
    /// A prompt where the user types <paramref name="typedKey"/> (null: cancels). Like the real
    /// dialog it only accepts the key once the validator passes, and records what the validator said.
    /// </summary>
    private static IApiKeyPrompt PromptTyping(string? typedKey, Action<string?>? onValidation = null)
    {
        var prompt = Substitute.For<IApiKeyPrompt>();
        prompt.PromptAsync(Arg.Any<Func<string, CancellationToken, ValueTask<string?>>>(), Arg.Any<CancellationToken>())
            .Returns(call => Type(call.Arg<Func<string, CancellationToken, ValueTask<string?>>>(), call.Arg<CancellationToken>()));
        return prompt;

        async ValueTask<string?> Type(Func<string, CancellationToken, ValueTask<string?>> validate, CancellationToken token)
        {
            if (typedKey is null) return null;
            var error = await validate(typedKey, token);
            onValidation?.Invoke(error);
            return error is null ? typedKey : null;
        }
    }

    [Fact]
    public async Task StoringAnApiKeyLogsTheUserIn()
    {
        using var loginManager = CreateLoginManager();
        (await loginManager.GetIsUserLoggedInAsync()).Should().BeFalse();

        var loggedIn = new TaskCompletionSource<UserInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = loginManager.UserInfoObservable.Subscribe(info => { if (info is not null) loggedIn.TrySetResult(info); });

        await new ApiKeyMessageFactory(_cache).SetApiKey("the-key");

        var info = await loggedIn.Task.WaitAsync(TimeSpan.FromSeconds(10));
        info.Name.Should().Be("silver");
    }

    [Fact]
    public async Task KeyOnlyLoginIsNotEvictedByTheEmptyTokenStream()
    {
        using var loginManager = CreateLoginManager();
        await new ApiKeyMessageFactory(_cache).SetApiKey("the-key");

        // Give both datom streams time to emit; a merged stream would let the token stream's
        // "empty" signal clear the user info after the key stream's "present" one.
        await Task.Delay(TimeSpan.FromSeconds(1));

        loginManager.UserInfo.Should().NotBeNull();
        (await loginManager.GetIsUserLoggedInAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task LoginWithoutAClientIdPromptsForAnApiKeyAndStoresIt()
    {
        var prompt = PromptTyping("typed-key");
        using var loginManager = CreateLoginManager(prompt);

        await loginManager.LoginAsync();

        await prompt.Received(1).PromptAsync(Arg.Any<Func<string, CancellationToken, ValueTask<string?>>>(), Arg.Any<CancellationToken>());
        (await AuthTestSupport.ReadKey(_store)).Should().Be("typed-key");
        (await loginManager.GetIsUserLoggedInAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task LoginWithoutAClientIdTrimsThePastedKey()
    {
        var prompt = PromptTyping("  typed-key\n");
        using var loginManager = CreateLoginManager(prompt);

        await loginManager.LoginAsync();

        (await AuthTestSupport.ReadKey(_store)).Should().Be("typed-key");
    }

    [Fact]
    public async Task CancellingTheKeyPromptLeavesTheUserSignedOut()
    {
        var prompt = PromptTyping(null);
        using var loginManager = CreateLoginManager(prompt);

        await loginManager.LoginAsync();

        (await AuthTestSupport.ReadKey(_store)).Should().BeNull();
        (await loginManager.GetIsUserLoggedInAsync()).Should().BeFalse();
    }

    /// <summary>
    /// Like <see cref="PromptTyping"/>, but the user only types once <paramref name="typed"/> completes,
    /// so several logins can be waiting on the dialog at the same time.
    /// </summary>
    private static IApiKeyPrompt PromptTypingLater(Task typed, string typedKey)
    {
        var prompt = Substitute.For<IApiKeyPrompt>();
        prompt.PromptAsync(Arg.Any<Func<string, CancellationToken, ValueTask<string?>>>(), Arg.Any<CancellationToken>())
            .Returns(call => Type(call.Arg<Func<string, CancellationToken, ValueTask<string?>>>()));
        return prompt;

        async ValueTask<string?> Type(Func<string, CancellationToken, ValueTask<string?>> validate)
        {
            await typed;
            return await validate(typedKey, CancellationToken.None) is null ? typedKey : null;
        }
    }

    [Fact]
    public async Task ConcurrentKeyLoginsShareOneDialog()
    {
        var typed = new TaskCompletionSource();
        var prompt = PromptTypingLater(typed.Task, "typed-key");
        using var loginManager = CreateLoginManager(prompt);

        var logins = Enumerable.Range(0, 3).Select(_ => loginManager.LoginWithApiKeyAsync()).ToArray();
        typed.SetResult();
        await Task.WhenAll(logins).WaitAsync(TimeSpan.FromSeconds(10));

        await prompt.Received(1).PromptAsync(Arg.Any<Func<string, CancellationToken, ValueTask<string?>>>(), Arg.Any<CancellationToken>());
        (await AuthTestSupport.ReadKey(_store)).Should().Be("typed-key");
    }

    [Fact]
    public async Task AKeyTypedAfterTheFirstCallerGaveUpIsStillStored()
    {
        // The dialog outlives whoever opened it: cancelling only stops that caller waiting.
        var typed = new TaskCompletionSource();
        var prompt = PromptTypingLater(typed.Task, "typed-key");
        using var loginManager = CreateLoginManager(prompt);
        using var firstCaller = new CancellationTokenSource();

        var first = loginManager.LoginWithApiKeyAsync(firstCaller.Token);
        var second = loginManager.LoginWithApiKeyAsync();
        await firstCaller.CancelAsync();
        typed.SetResult();

        await first.Awaiting(t => t).Should().ThrowAsync<OperationCanceledException>();
        await second.WaitAsync(TimeSpan.FromSeconds(10));
        await prompt.Received(1).PromptAsync(Arg.Any<Func<string, CancellationToken, ValueTask<string?>>>(), Arg.Any<CancellationToken>());
        (await AuthTestSupport.ReadKey(_store)).Should().Be("typed-key");
    }

    [Fact]
    public async Task AKeyLoginAfterTheDialogClosedAsksAgain()
    {
        var prompt = PromptTyping(null);
        using var loginManager = CreateLoginManager(prompt);

        await loginManager.LoginWithApiKeyAsync();
        await loginManager.LoginWithApiKeyAsync();

        await prompt.Received(2).PromptAsync(Arg.Any<Func<string, CancellationToken, ValueTask<string?>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoginWithoutAClientIdAndNoInteractivePromptFailsLoudly()
    {
        using var loginManager = CreateLoginManager(new NoInteractiveApiKeyPrompt());

        var act = async () => await loginManager.LoginAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*No interactive prompt*nexus-api-key*");
    }

    [Fact]
    public void WebApiServicesRegisterTheHeadlessPromptWhenNoUiProvidesOne()
    {
        var services = new ServiceCollection();
        services.AddNexusWebApi();
        services.Where(d => d.ServiceType == typeof(IApiKeyPrompt)).Should().ContainSingle()
            .Which.ImplementationType.Should().Be<NoInteractiveApiKeyPrompt>();
    }

    [Fact]
    public async Task LogoutRemovesTheStoredApiKeyAndToken()
    {
        using var loginManager = CreateLoginManager();
        await new ApiKeyMessageFactory(_cache).SetApiKey("the-key");
        await AuthTestSupport.StoreToken(_store, "stored-access", TimeSpan.FromHours(1));

        await loginManager.Logout();

        (await AuthTestSupport.ReadToken(_store)).Should().BeNull();

        (await AuthTestSupport.ReadKey(_store)).Should().BeNull();
    }

    [Fact]
    public async Task ARejectedKeyIsNeverStoredAndTheDialogGetsAMessage()
    {
        string? shown = null;
        await AuthTestSupport.StoreToken(_store, "stored-access", TimeSpan.FromHours(1));
        using var loginManager = CreateLoginManager(PromptTyping("wrong-key", error => shown = error));

        await loginManager.LoginWithApiKeyAsync();

        shown.Should().NotBeNullOrWhiteSpace("a bad key is explained in the dialog, not closed silently");
        (await AuthTestSupport.ReadKey(_store)).Should().BeNull();
        (await AuthTestSupport.ReadToken(_store)).Should().NotBeNull();
    }

    [Fact]
    public async Task ARejectedKeyDoesNotReplaceTheSignedInUserOrTheStoredKey()
    {
        // Signed in with a good key, so a user is cached: the old Verify returned that cached
        // user for any later key and stored the unchecked one.
        using var loginManager = CreateLoginManager(PromptTyping("good-key"));
        await loginManager.LoginWithApiKeyAsync();
        loginManager.UserInfo.Should().NotBeNull();

        string? shown = null;
        using var second = CreateLoginManager(PromptTyping("wrong-key", error => shown = error));
        await second.GetUserInfoAsync(CancellationToken.None);
        await second.LoginWithApiKeyAsync();

        shown.Should().NotBeNullOrWhiteSpace();
        (await AuthTestSupport.ReadKey(_store)).Should().Be("good-key");
        (await second.GetIsUserLoggedInAsync()).Should().BeTrue("the earlier sign-in is untouched");
    }

    [Fact]
    public async Task ARejectedKeyIsNotStoredWhenNoOneIsSignedIn()
    {
        string? shown = null;
        using var loginManager = CreateLoginManager(PromptTyping("wrong-key", error => shown = error));

        await loginManager.LoginWithApiKeyAsync();

        shown.Should().NotBeNullOrWhiteSpace();
        (await AuthTestSupport.ReadKey(_store)).Should().BeNull();
        (await loginManager.GetIsUserLoggedInAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task AKeyCheckThatTimesOutIsNotStoredAndTheDialogGetsAMessage()
    {
        // HttpClient reports a timeout as a cancellation even though nobody cancelled the dialog.
        string? shown = null;
        using var loginManager = CreateLoginManager(PromptTyping("slow-key", error => shown = error));

        await loginManager.LoginWithApiKeyAsync();

        shown.Should().NotBeNullOrWhiteSpace("a check that never got an answer is explained, not dropped");
        (await AuthTestSupport.ReadKey(_store)).Should().BeNull();
    }

    [Fact]
    public async Task AVerifiedKeyReplacesTheCachedUser()
    {
        // The OAuth session's user is cached; the verified key's account must take its place
        // immediately, not wait out the cache.
        await AuthTestSupport.StoreToken(_store, "stored-access", TimeSpan.FromHours(1));
        using var loginManager = CreateLoginManager(PromptTyping("good-key"));
        var names = new List<string?>();
        var oauthUserShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = loginManager.UserInfoObservable.Subscribe(info =>
        {
            lock (names) names.Add(info?.Name);
            if (info?.Name == "oauth-user") oauthUserShown.TrySetResult();
        });
        await oauthUserShown.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await loginManager.LoginWithApiKeyAsync();

        loginManager.UserInfo!.Name.Should().Be("silver");
        lock (names) names.Last().Should().Be("silver");
        (await AuthTestSupport.ReadKey(_store)).Should().Be("good-key");
    }

    [Fact]
    public async Task AnExpiredTokenWithoutAClientIdIsLeftAloneAndNeedsANewSignIn()
    {
        await AuthTestSupport.StoreToken(_store, "stale-access", TimeSpan.FromHours(-1));
        var http = Substitute.ForPartsOf<MockHttpMessageHandler>();
        var oauth = AuthTestSupport.CreateOAuth(_jobMonitor, http, oauthClientId: "");
        var factory = AuthTestSupport.CreateOAuthFactory(_cache, oauth);

        (await factory.IsAuthenticated()).Should().BeFalse("the token cannot be refreshed, so the user must sign in again or use an API key");

        await http.DidNotReceive().SendMock(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>());
        (await AuthTestSupport.ReadToken(_store))!.AccessToken.Should().Be("stale-access");
    }

    [Fact]
    public async Task ARefreshThatRepliesWithJsonNullLeavesTheStoredTokenAndReportsNoCredential()
    {
        await AuthTestSupport.StoreToken(_store, "stale-access", TimeSpan.FromHours(-1));
        var http = Substitute.ForPartsOf<MockHttpMessageHandler>();
        http.SendMock(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null") }));
        var oauth = AuthTestSupport.CreateOAuth(_jobMonitor, http);
        var factory = AuthTestSupport.CreateOAuthFactory(_cache, oauth);

        (await factory.IsAuthenticated()).Should().BeFalse("the token endpoint returned no token to store");

        (await AuthTestSupport.ReadToken(_store))!.AccessToken.Should().Be("stale-access");
    }

    private static async Task<UserInfo> NextUser(LoginManager loginManager)
    {
        var signedIn = new TaskCompletionSource<UserInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = loginManager.UserInfoObservable.Subscribe(info => { if (info is not null) signedIn.TrySetResult(info); });
        return await signedIn.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ALoginManagerBuiltBeforeAnythingIsPrimedSeesTheStoredKey()
    {
        await AuthTestSupport.StoreKey(_store, "the-key");

        using var loginManager = CreateLoginManager();
        var info = await NextUser(loginManager);

        info.Name.Should().Be("silver");
        loginManager.UserInfo.Should().NotBeNull();
        _cache.ApiKeySource.Should().Be(CredentialSource.Stored);
    }

    [Fact]
    public async Task ALoginManagerBuiltBeforeATokenIsPrimedSeesTheStoredToken()
    {
        await AuthTestSupport.StoreToken(_store, "stored-access", TimeSpan.FromHours(1));

        using var loginManager = CreateLoginManager();
        var info = await NextUser(loginManager);

        info.Name.Should().Be("oauth-user");
    }

    [Fact]
    public async Task AStoreThatIsUnavailableAtLoadLeavesTheUserSignedOutAndDoesNotEvictAKeySetAfterwards()
    {
        _store.Availability = (SecretUnavailableKind.Locked, "wallet is locked");
        using var loginManager = CreateLoginManager();
        (await loginManager.GetIsUserLoggedInAsync()).Should().BeFalse();
        loginManager.UserInfo.Should().BeNull();

        var written = await _cache.SetApiKeyAsync("session-key", CancellationToken.None);

        written.Should().BeOfType<SecretWriteResult.Unavailable>();
        _cache.ApiKeySource.Should().Be(CredentialSource.Memory);
        (await NextUser(loginManager)).Name.Should().Be("silver");

        // The store coming back empty must not sign the session out.
        _store.Availability = null;
        _store.Status = _store.Status with { Locked = false };
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        loginManager.UserInfo.Should().NotBeNull();
    }

    [Fact]
    public async Task AnUnlockAfterAnUnavailableLoadReloadsAndSignsIn()
    {
        await AuthTestSupport.StoreKey(_store, "the-key");
        _store.Availability = (SecretUnavailableKind.Locked, "wallet is locked");
        using var cache = AuthTestSupport.CreateCache(_store);
        using var loginManager = CreateLoginManager(cache: cache);
        (await loginManager.GetIsUserLoggedInAsync()).Should().BeFalse();

        _store.Availability = null;
        _store.Status = _store.Status with { Locked = false };

        (await NextUser(loginManager)).Name.Should().Be("silver");
    }

    [Fact]
    public async Task TheSignedInSignalFollowsSettingAndClearingTheKey()
    {
        var signal = new List<bool>();
        using var subscription = _cache.HasCredential.Subscribe(value => { lock (signal) signal.Add(value); });
        await _cache.EnsureLoadedAsync(CancellationToken.None);

        await _cache.SetApiKeyAsync("the-key", CancellationToken.None);
        _cache.HasCredential.CurrentValue.Should().BeTrue();

        await _cache.ClearAsync(CancellationToken.None);
        _cache.HasCredential.CurrentValue.Should().BeFalse();

        lock (signal) signal.Should().Equal(false, true, false);
    }

    [Fact]
    public async Task ClearingTheTokenWhileAKeyStaysDoesNotFlapTheSignedInSignal()
    {
        await AuthTestSupport.StoreKey(_store, "the-key");
        await AuthTestSupport.StoreToken(_store, "stored-access", TimeSpan.FromHours(1));
        await _cache.EnsureLoadedAsync(CancellationToken.None);
        var signal = new List<bool>();
        using var subscription = _cache.HasCredential.Subscribe(value => { lock (signal) signal.Add(value); });

        await _store.DeleteAsync(NexusSecretKeys.OAuth, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        lock (signal) signal.Should().Equal(true);
        _cache.HasCredential.CurrentValue.Should().BeTrue();
    }

    [Fact]
    public async Task AKeyPinnedByTheEnvironmentIsReportedByThePromptAndNexusIsNeverAsked()
    {
        var overlay = new OverlaySecretStore(_store, new FakeEnvironment((NexusSecretKeys.ApiKeyEnvironmentVariable, "pinned-key")), null,
            new Dictionary<SecretKey, string> { [NexusSecretKeys.ApiKey] = NexusSecretKeys.ApiKeyEnvironmentVariable });
        using var pinned = AuthTestSupport.CreateCache(overlay);
        string? shown = null;
        using var loginManager = CreateLoginManager(PromptTyping("typed-key", error => shown = error), cache: pinned);

        await loginManager.LoginWithApiKeyAsync();

        shown.Should().Be("The API key is set by environment variable NEXUS_API_KEY; unset it to store a different one");
        _seenKeys.Should().NotContain("typed-key");
        (await AuthTestSupport.ReadKey(_store)).Should().BeNull();
    }

    [Fact]
    public async Task LogoutLeavesAnEnvironmentKeyInPlace()
    {
        var overlay = new OverlaySecretStore(_store, new FakeEnvironment((NexusSecretKeys.ApiKeyEnvironmentVariable, "pinned-key")), null,
            new Dictionary<SecretKey, string> { [NexusSecretKeys.ApiKey] = NexusSecretKeys.ApiKeyEnvironmentVariable });
        using var pinned = AuthTestSupport.CreateCache(overlay);
        using var loginManager = CreateLoginManager(cache: pinned);
        (await NextUser(loginManager)).Name.Should().Be("silver");

        await loginManager.Logout();

        (await loginManager.GetIsUserLoggedInAsync()).Should().BeTrue("the variable still holds the key");
    }

    [Fact]
    public async Task AKeyStoredWhileTheStoreIsUnavailableStaysUsableByTheRouter()
    {
        _store.Availability = (SecretUnavailableKind.NoBus, "no session bus");
        var router = AuthTestSupport.CreateRouter(_cache, AuthTestSupport.CreateOAuth(_jobMonitor, new MockHttpMessageHandler()));

        var written = await new ApiKeyMessageFactory(_cache).SetApiKey("session-key");

        written.Should().BeOfType<SecretWriteResult.Unavailable>();
        _cache.ApiKeySource.Should().Be(CredentialSource.Memory);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/graphql");
        await router.ApplyAuthentication(request, CancellationToken.None);
        request.Headers.GetValues("apikey").Should().ContainSingle().Which.Should().Be("session-key");
    }
}
