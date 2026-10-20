using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.NexusWebApi.Auth;
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
    private string? _savedEnvironmentKey;

    public LoginManagerTests(IConnection conn, IJobMonitor jobMonitor)
    {
        _conn = conn;
        _jobMonitor = jobMonitor;
    }

    public async ValueTask InitializeAsync()
    {
        _savedEnvironmentKey = Environment.GetEnvironmentVariable(ApiKeyMessageFactory.NexusApiKeyEnvironmentVariable);
        Environment.SetEnvironmentVariable(ApiKeyMessageFactory.NexusApiKeyEnvironmentVariable, null);
        await AuthTestSupport.ClearCredentials(_conn);
    }

    public async ValueTask DisposeAsync()
    {
        await AuthTestSupport.ClearCredentials(_conn);
        Environment.SetEnvironmentVariable(ApiKeyMessageFactory.NexusApiKeyEnvironmentVariable, _savedEnvironmentKey);
    }

    private LoginManager CreateLoginManager(IApiKeyPrompt? prompt = null, string oauthClientId = "", HttpStatusCode validateStatus = HttpStatusCode.OK)
    {
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
                if (keys.Contains("slow-key")) return Task.FromException<HttpResponseMessage>(new TaskCanceledException("The request timed out"));
                var rejected = keys.Contains("wrong-key");
                return Task.FromResult(new HttpResponseMessage(rejected ? HttpStatusCode.Unauthorized : validateStatus) { Content = new StringContent(ValidateJson) });
            });

        var oauth = AuthTestSupport.CreateOAuth(_jobMonitor, new MockHttpMessageHandler(), oauthClientId);
        var router = AuthTestSupport.CreateRouter(_conn, oauth);
        var apiClient = new NexusApiClient(NullLogger<NexusApiClient>.Instance, router, new HttpClient(rest), Substitute.For<IGraphQlClient>());
        return new LoginManager(_conn, apiClient, router, oauth, prompt ?? Substitute.For<IApiKeyPrompt>(), NullLogger<LoginManager>.Instance);
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

        await new ApiKeyMessageFactory(_conn).SetApiKey("the-key");

        var info = await loggedIn.Task.WaitAsync(TimeSpan.FromSeconds(10));
        info.Name.Should().Be("silver");
    }

    [Fact]
    public async Task KeyOnlyLoginIsNotEvictedByTheEmptyTokenStream()
    {
        using var loginManager = CreateLoginManager();
        await new ApiKeyMessageFactory(_conn).SetApiKey("the-key");

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
        ApiKey.Get(_conn.Db).Should().Be("typed-key");
        (await loginManager.GetIsUserLoggedInAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task LoginWithoutAClientIdTrimsThePastedKey()
    {
        var prompt = PromptTyping("  typed-key\n");
        using var loginManager = CreateLoginManager(prompt);

        await loginManager.LoginAsync();

        ApiKey.Get(_conn.Db).Should().Be("typed-key");
    }

    [Fact]
    public async Task CancellingTheKeyPromptLeavesTheUserSignedOut()
    {
        var prompt = PromptTyping(null);
        using var loginManager = CreateLoginManager(prompt);

        await loginManager.LoginAsync();

        ApiKey.Get(_conn.Db).Should().BeNull();
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
        ApiKey.Get(_conn.Db).Should().Be("typed-key");
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
        ApiKey.Get(_conn.Db).Should().Be("typed-key");
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
    public async Task LogoutRemovesAndExcisesTheStoredApiKey()
    {
        using var loginManager = CreateLoginManager();
        await new ApiKeyMessageFactory(_conn).SetApiKey("the-key");

        await loginManager.Logout();

        ApiKey.Get(_conn.Db).Should().BeNull();
        ApiKey.All(_conn.Db).Should().BeEmpty();
    }

    [Fact]
    public async Task ARejectedKeyIsNeverStoredAndTheDialogGetsAMessage()
    {
        string? shown = null;
        await AuthTestSupport.StoreToken(_conn, "stored-access", TimeSpan.FromHours(1));
        using var loginManager = CreateLoginManager(PromptTyping("wrong-key", error => shown = error));

        await loginManager.LoginWithApiKeyAsync();

        shown.Should().NotBeNullOrWhiteSpace("a bad key is explained in the dialog, not closed silently");
        ApiKey.All(_conn.Db).Should().BeEmpty();
        JWTToken.All(_conn.Db).Should().ContainSingle();
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
        ApiKey.Get(_conn.Db).Should().Be("good-key");
        (await second.GetIsUserLoggedInAsync()).Should().BeTrue("the earlier sign-in is untouched");
    }

    [Fact]
    public async Task ARejectedKeyIsNotStoredWhenNoOneIsSignedIn()
    {
        string? shown = null;
        using var loginManager = CreateLoginManager(PromptTyping("wrong-key", error => shown = error));

        await loginManager.LoginWithApiKeyAsync();

        shown.Should().NotBeNullOrWhiteSpace();
        ApiKey.All(_conn.Db).Should().BeEmpty();
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
        ApiKey.All(_conn.Db).Should().BeEmpty();
    }

    [Fact]
    public async Task AVerifiedKeyReplacesTheCachedUser()
    {
        // The OAuth session's user is cached; the verified key's account must take its place
        // immediately, not wait out the cache.
        await AuthTestSupport.StoreToken(_conn, "stored-access", TimeSpan.FromHours(1));
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
        ApiKey.Get(_conn.Db).Should().Be("good-key");
    }

    [Fact]
    public async Task AnExpiredTokenWithoutAClientIdIsLeftAloneAndNeedsANewSignIn()
    {
        await AuthTestSupport.StoreToken(_conn, "stale-access", TimeSpan.FromHours(-1));
        var http = Substitute.ForPartsOf<MockHttpMessageHandler>();
        var oauth = AuthTestSupport.CreateOAuth(_jobMonitor, http, oauthClientId: "");
        var factory = new OAuth2MessageFactory(_conn, oauth, NullLogger<OAuth2MessageFactory>.Instance);

        (await factory.IsAuthenticated()).Should().BeFalse("the token cannot be refreshed, so the user must sign in again or use an API key");

        await http.DidNotReceive().SendMock(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>());
        JWTToken.All(_conn.Db).Should().ContainSingle().Which.AccessToken.Should().Be("stale-access");
    }
}
