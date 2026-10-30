using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Web;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Backend.Jobs;
using SilverModManager.Networking.Forums.Auth;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Networking.Forums.Tests;

public class ForumLoginJobTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Start);
    private readonly StubHandler _forum = new();
    private readonly InMemorySecretStore _store = new();
    private readonly ForumCredentials _credentials;
    private readonly JobMonitor _monitor = new(NullLogger<JobMonitor>.Instance);
    private readonly IOSInterop _os = Substitute.For<IOSInterop>();
    private readonly TaskCompletionSource<Uri> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HttpClient _browser = new();

    public ForumLoginJobTests()
    {
        _credentials = new ForumCredentials(_store, NullLoggerFactory.Instance);
        _os.When(o => o.OpenUri(Arg.Any<Uri>())).Do(call => _opened.TrySetResult(call.Arg<Uri>()));
    }

    public void Dispose()
    {
        _browser.Dispose();
        _credentials.Dispose();
        _monitor.Dispose();
    }

    private ForumOAuth Oauth(string clientId = "client-1", bool usePkce = true)
        => new(new HttpClient(_forum), _credentials, site => ForumEndpoints.For(site) with { ClientId = clientId, UsePkce = usePkce }, _time, NullLogger<ForumOAuth>.Instance);

    private IJobTask<ForumLoginJob, DynamicData.Kernel.Optional<OAuthToken>> Begin(ForumOAuth? oauth = null, TimeSpan? timeout = null)
        => ForumLoginJob.Create(_monitor, oauth ?? Oauth(), _os, ForumSite.LoversLab, NullLogger.Instance, timeout);

    private static string TokenReply() => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["access_token"] = "new-access",
        ["refresh_token"] = "new-refresh",
        ["expires_in"] = 3600,
    });

    private static (Uri Redirect, string State) Parse(Uri authorize)
    {
        var query = HttpUtility.ParseQueryString(authorize.Query);
        return (new Uri(query["redirect_uri"]!), query["state"]!);
    }

    private async Task<HttpResponseMessage> Callback(Uri authorize, string extra)
    {
        var (redirect, state) = Parse(authorize);
        return await _browser.GetAsync(new Uri($"{redirect.GetLeftPart(UriPartial.Path)}?{extra}&state={Uri.EscapeDataString(state)}"));
    }

    [Fact]
    public async Task TheBrowserIsSentToTheAuthorizeUrlAndTheSameAddressIsPublished()
    {
        var task = Begin();
        var opened = await _opened.Task.WaitAsync(TimeSpan.FromSeconds(10));

        opened.Host.Should().Be("api.loverslab.com");
        HttpUtility.ParseQueryString(opened.Query)["client_id"].Should().Be("client-1");
        Parse(opened).Redirect.Host.Should().Be("127.0.0.1");
        task.JobDefinition.LoginUri.Value.Should().Be(opened);

        _monitor.Cancel(task);
        await task;
    }

    [Fact]
    public async Task ACallbackWithACodeIsTradedForAToken()
    {
        _forum.Reply(HttpStatusCode.OK, TokenReply());
        var task = Begin();
        var opened = await _opened.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using var page = await Callback(opened, "code=the-code");
        var result = await task;

        page.StatusCode.Should().Be(HttpStatusCode.OK);
        result.HasValue.Should().BeTrue();
        result.Value.AccessToken.Should().Be("new-access");
        result.Value.ExpiresAt.Should().Be(Start + TimeSpan.FromHours(1));
        var exchange = _forum.Requests.Should().ContainSingle().Subject;
        exchange.Body.Should().Contain("code=the-code").And.Contain("code_verifier=");
    }

    [Fact]
    public async Task TheVerifierSentWithTheCodeMatchesTheChallengeInTheAuthorizeUrl()
    {
        _forum.Reply(HttpStatusCode.OK, TokenReply());
        var task = Begin();
        var opened = await _opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var challenge = HttpUtility.ParseQueryString(opened.Query)["code_challenge"];

        using var _ = await Callback(opened, "code=c");
        await task;

        var verifier = HttpUtility.ParseQueryString(_forum.Requests.Single().Body)["code_verifier"]!;
        var expected = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        challenge.Should().Be(expected);
        verifier.Length.Should().BeInRange(43, 128);
    }

    [Fact]
    public async Task ADeclinedCallbackEndsWithTheDeclinedMessageAndNeverAsksForAToken()
    {
        var task = Begin();
        var opened = await _opened.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using var _ = await Callback(opened, "error=access_denied");
        var act = async () => await task;

        await act.Should().ThrowAsync<SignInDeclinedException>().WithMessage("Sign-in to LoversLab was declined");
        _forum.Requests.Should().BeEmpty();
        (await _credentials.For(ForumSite.LoversLab).GetTokenAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ARefusedCodeExchangeEndsWithTheRefusalAndStoresNothing()
    {
        _forum.Reply(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}");
        var task = Begin();
        var opened = await _opened.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using var _ = await Callback(opened, "code=bad");
        var act = async () => await task;

        await act.Should().ThrowAsync<SignInRejectedException>();
        (await _credentials.For(ForumSite.LoversLab).GetTokenAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task CancellingStopsTheListenerAndYieldsNoToken()
    {
        var task = Begin();
        var opened = await _opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var (redirect, _) = Parse(opened);

        _monitor.Cancel(task);
        var result = await task;

        result.HasValue.Should().BeFalse();
        using var client = new TcpClient();
        var connect = async () => await client.ConnectAsync(IPAddress.Loopback, redirect.Port);
        await connect.Should().ThrowAsync<SocketException>();
        _forum.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task APageThatCannotBeOpenedFailsWithThatErrorAndLeavesNothingUnobserved()
    {
        Uri? authorize = null;
        var os = Substitute.For<IOSInterop>();
        os.When(o => o.OpenUri(Arg.Any<Uri>())).Do(call =>
        {
            authorize = call.Arg<Uri>();
            throw new InvalidOperationException("no browser");
        });
        // Faults left behind by other tests surface at the next collection, so flush them before listening.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var unobserved = new List<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) => unobserved.AddRange(e.Exception.Flatten().InnerExceptions);
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var task = ForumLoginJob.Create(_monitor, Oauth(), os, ForumSite.LoversLab, NullLogger.Instance);
            var act = async () => await task;
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("no browser");

            using var client = new TcpClient();
            var connect = async () => await client.ConnectAsync(IPAddress.Loopback, Parse(authorize!).Redirect.Port);
            await connect.Should().ThrowAsync<SocketException>();

            // The abandoned wait faults when the listener closes under it; that fault must have been observed.
            for (var i = 0; i < 3; i++)
            {
                await Task.Delay(50);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            unobserved.Should().NotContain(e => e is ObjectDisposedException || e is SocketException || e is OperationCanceledException);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [Fact]
    public async Task SilenceEndsWithTheTimedOutMessage()
    {
        var task = Begin(timeout: TimeSpan.FromMilliseconds(200));
        await _opened.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var act = async () => await task;

        await act.Should().ThrowAsync<SignInTimedOutException>().WithMessage("Sign-in to LoversLab timed out");
    }

    [Fact]
    public async Task WithoutPkceNeitherTheChallengeNorTheVerifierIsSent()
    {
        _forum.Reply(HttpStatusCode.OK, TokenReply());
        var task = Begin(Oauth(usePkce: false));
        var opened = await _opened.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using var _ = await Callback(opened, "code=c");
        await task;

        opened.Query.Should().NotContain("code_challenge");
        _forum.Requests.Single().Body.Should().NotContain("code_verifier");
    }

    [Fact]
    public async Task AJobWithoutAClientIdIsRefusedBeforeAnythingOpens()
    {
        var act = async () => await Begin(Oauth(clientId: ""));

        await act.Should().ThrowAsync<SignInNotSetUpException>();
        _os.DidNotReceive().OpenUri(Arg.Any<Uri>());
    }
}
