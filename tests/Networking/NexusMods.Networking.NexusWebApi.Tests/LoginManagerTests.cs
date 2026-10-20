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
            .Returns(_ => Task.FromResult(new HttpResponseMessage(validateStatus) { Content = new StringContent(ValidateJson) }));

        var oauth = AuthTestSupport.CreateOAuth(_jobMonitor, new MockHttpMessageHandler());
        var router = AuthTestSupport.CreateRouter(_conn, oauth);
        var apiClient = new NexusApiClient(NullLogger<NexusApiClient>.Instance, router, new HttpClient(rest), Substitute.For<IGraphQlClient>());
        return new LoginManager(_conn, apiClient, router, oauth, AuthTestSupport.CreateSettings(oauthClientId), prompt ?? Substitute.For<IApiKeyPrompt>(), NullLogger<LoginManager>.Instance);
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
        var prompt = Substitute.For<IApiKeyPrompt>();
        prompt.PromptAsync(Arg.Any<CancellationToken>()).Returns(new ValueTask<string?>("typed-key"));
        using var loginManager = CreateLoginManager(prompt);

        await loginManager.LoginAsync();

        await prompt.Received(1).PromptAsync(Arg.Any<CancellationToken>());
        ApiKey.Get(_conn.Db).Should().Be("typed-key");
        (await loginManager.GetIsUserLoggedInAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task LoginWithoutAClientIdTrimsThePastedKey()
    {
        var prompt = Substitute.For<IApiKeyPrompt>();
        prompt.PromptAsync(Arg.Any<CancellationToken>()).Returns(new ValueTask<string?>("  typed-key\n"));
        using var loginManager = CreateLoginManager(prompt);

        await loginManager.LoginAsync();

        ApiKey.Get(_conn.Db).Should().Be("typed-key");
    }

    [Fact]
    public async Task CancellingTheKeyPromptLeavesTheUserSignedOut()
    {
        var prompt = Substitute.For<IApiKeyPrompt>();
        prompt.PromptAsync(Arg.Any<CancellationToken>()).Returns(new ValueTask<string?>((string?)null));
        using var loginManager = CreateLoginManager(prompt);

        await loginManager.LoginAsync();

        ApiKey.Get(_conn.Db).Should().BeNull();
        (await loginManager.GetIsUserLoggedInAsync()).Should().BeFalse();
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
    public async Task ARejectedKeyIsRemovedAndAStoredTokenIsKept()
    {
        var prompt = Substitute.For<IApiKeyPrompt>();
        prompt.PromptAsync(Arg.Any<CancellationToken>()).Returns(new ValueTask<string?>("wrong-key"));
        await AuthTestSupport.StoreToken(_conn, "stored-access", TimeSpan.FromHours(1));
        using var loginManager = CreateLoginManager(prompt, validateStatus: HttpStatusCode.Unauthorized);

        await loginManager.LoginWithApiKeyAsync();

        ApiKey.All(_conn.Db).Should().BeEmpty();
        JWTToken.All(_conn.Db).Should().ContainSingle();
    }
}
