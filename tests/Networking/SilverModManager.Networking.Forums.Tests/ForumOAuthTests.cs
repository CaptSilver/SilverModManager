using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Networking.Forums.Auth;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Networking.Forums.Tests;

public class ForumOAuthTests
{
    private static readonly Uri Redirect = new("http://127.0.0.1:50000/callback");
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Start);
    private readonly StubHandler _handler = new();
    private readonly Dictionary<ForumSite, FakeTokenCache> _caches = new()
    {
        [ForumSite.LoversLab] = new FakeTokenCache(),
        [ForumSite.VectorPlexus] = new FakeTokenCache(),
    };

    private ForumOAuth Create(bool usePkce = true, string clientId = "client-123")
        => new(new HttpClient(_handler), new FakeCredentials(_caches), site => ForumEndpoints.For(site) with { ClientId = clientId, UsePkce = usePkce },
            _time, NullLogger<ForumOAuth>.Instance);

    private static string TokenReply(string access = "new-access", string? refresh = "new-refresh", int expiresIn = 3600)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["access_token"] = access,
            ["refresh_token"] = refresh,
            ["expires_in"] = expiresIn,
            ["token_type"] = "Bearer",
        });

    private OAuthToken Expired(string refresh = "old-refresh") => new("old-access", refresh, Start - TimeSpan.FromMinutes(1));

    private OAuthToken Valid() => new("valid-access", "valid-refresh", Start + TimeSpan.FromHours(1));

    private static Dictionary<string, string> Form(string body)
        => HttpUtility.ParseQueryString(body).AllKeys.ToDictionary(k => k!, k => HttpUtility.ParseQueryString(body)[k]!);

    [Theory]
    [InlineData(ForumSite.LoversLab, "Sign-in for LoversLab isn't set up yet")]
    [InlineData(ForumSite.VectorPlexus, "Sign-in for VectorPlexus isn't set up yet")]
    public async Task EverythingRefusesWithoutAClientIdAndSendsNothing(ForumSite site, string message)
    {
        var oauth = Create(clientId: "");
        _caches[site].Token = Expired();

        oauth.Invoking(o => o.RequireClientId(site)).Should().Throw<SignInNotSetUpException>().WithMessage(message);
        oauth.Invoking(o => o.BuildAuthorizeUri(site, Redirect, "s", "c")).Should().Throw<SignInNotSetUpException>();
        await oauth.Invoking(o => o.ExchangeCode(site, "code", "verifier", Redirect, CancellationToken.None)).Should().ThrowAsync<SignInNotSetUpException>();
        await oauth.Invoking(o => o.Refresh(site, "refresh", CancellationToken.None)).Should().ThrowAsync<SignInNotSetUpException>();
        await oauth.Invoking(o => o.GetUsableToken(site, CancellationToken.None)).Should().ThrowAsync<SignInNotSetUpException>();

        _handler.Requests.Should().BeEmpty();
        _caches[site].Token.Should().NotBeNull("an unconfigured site must not wipe a token");
    }

    [Fact]
    public void AClientIdFromTheSettingsSwitchTheSiteOn()
    {
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<ForumAuthSettings>(Arg.Any<string?>()).Returns(new ForumAuthSettings { LoversLabClientId = "staging-id" });
        var oauth = new ForumOAuth(new HttpClient(_handler), new FakeCredentials(_caches), settings, _time, NullLogger<ForumOAuth>.Instance);

        oauth.RequireClientId(ForumSite.LoversLab).ClientId.Should().Be("staging-id");
        oauth.Invoking(o => o.RequireClientId(ForumSite.VectorPlexus)).Should().Throw<SignInNotSetUpException>();
    }

    [Fact]
    public void TheAuthorizeUriUsesTheSitesEndpointAndClientId()
    {
        var uri = Create().BuildAuthorizeUri(ForumSite.LoversLab, Redirect, "the-state", "the-challenge");

        uri.GetLeftPart(UriPartial.Path).Should().Be(ForumEndpoints.For(ForumSite.LoversLab).Authorize.AbsoluteUri);
        var query = HttpUtility.ParseQueryString(uri.Query);
        query["client_id"].Should().Be("client-123");
        query["state"].Should().Be("the-state");
        query["redirect_uri"].Should().Be(Redirect.AbsoluteUri);
    }

    [Fact]
    public async Task TheCodeExchangePostsTheFormWithTheVerifier()
    {
        _handler.Reply(HttpStatusCode.OK, TokenReply(expiresIn: 7200));

        var token = await Create().ExchangeCode(ForumSite.LoversLab, "the-code", "the-verifier", Redirect, CancellationToken.None);

        var request = _handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.Should().Be(ForumEndpoints.For(ForumSite.LoversLab).Token);
        request.ContentType.Should().Be("application/x-www-form-urlencoded");
        request.Authorization.Should().BeNull();
        Form(request.Body).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = "client-123",
            ["redirect_uri"] = Redirect.AbsoluteUri,
            ["code"] = "the-code",
            ["code_verifier"] = "the-verifier",
        });
        token.AccessToken.Should().Be("new-access");
        token.RefreshToken.Should().Be("new-refresh");
        token.ExpiresAt.Should().Be(Start + TimeSpan.FromSeconds(7200));
    }

    [Fact]
    public async Task TheCodeExchangeLeavesOutTheVerifierWhenPkceIsOff()
    {
        _handler.Reply(HttpStatusCode.OK, TokenReply());

        await Create(usePkce: false).ExchangeCode(ForumSite.VectorPlexus, "the-code", "the-verifier", Redirect, CancellationToken.None);

        var request = _handler.Requests.Should().ContainSingle().Subject;
        request.Uri.Should().Be(ForumEndpoints.For(ForumSite.VectorPlexus).Token);
        Form(request.Body).Should().NotContainKey("code_verifier");
    }

    [Fact]
    public async Task ARefusedCodeExchangeIsASignInRefusalThatQuotesNothing()
    {
        _handler.Reply(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"the-code was used"}""");

        var failure = await Create().Invoking(o => o.ExchangeCode(ForumSite.LoversLab, "the-code", "the-verifier", Redirect, CancellationToken.None))
            .Should().ThrowAsync<SignInRejectedException>();

        failure.Which.Message.Should().Be("LoversLab refused the sign-in");
    }

    [Fact]
    public async Task AReplyWithoutAnAccessTokenIsARefusal()
    {
        _handler.Reply(HttpStatusCode.OK, """{"expires_in":3600}""");

        await Create().Invoking(o => o.ExchangeCode(ForumSite.LoversLab, "c", "v", Redirect, CancellationToken.None))
            .Should().ThrowAsync<SignInRejectedException>();
    }

    [Fact]
    public async Task TheRefreshPostsTheRefreshGrantWithoutASecret()
    {
        _handler.Reply(HttpStatusCode.OK, TokenReply());

        var token = await Create().Refresh(ForumSite.LoversLab, "old-refresh", CancellationToken.None);

        var request = _handler.Requests.Should().ContainSingle().Subject;
        request.Uri.Should().Be(ForumEndpoints.For(ForumSite.LoversLab).Token);
        Form(request.Body).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = "client-123",
            ["refresh_token"] = "old-refresh",
        });
        token!.AccessToken.Should().Be("new-access");
        token.RefreshToken.Should().Be("new-refresh");
        token.ExpiresAt.Should().Be(Start + TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task ARefreshReplyWithoutANewRefreshTokenKeepsTheOldOne()
    {
        _handler.Reply(HttpStatusCode.OK, TokenReply(refresh: null));

        var token = await Create().Refresh(ForumSite.LoversLab, "old-refresh", CancellationToken.None);

        token!.RefreshToken.Should().Be("old-refresh");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ARefusedRefreshReturnsNull(HttpStatusCode status)
    {
        _handler.Reply(status, """{"error":"invalid_grant"}""");

        (await Create().Refresh(ForumSite.LoversLab, "old-refresh", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task AUsableTokenIsReturnedWithoutAnyRequest()
    {
        _caches[ForumSite.LoversLab].Token = Valid();

        var token = await Create().GetUsableToken(ForumSite.LoversLab, CancellationToken.None);

        token.AccessToken.Should().Be("valid-access");
        _handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AnExpiredTokenIsRefreshedAndStored()
    {
        _caches[ForumSite.LoversLab].Token = Expired();
        _handler.Reply(HttpStatusCode.OK, TokenReply());

        var token = await Create().GetUsableToken(ForumSite.LoversLab, CancellationToken.None);

        token.AccessToken.Should().Be("new-access");
        _caches[ForumSite.LoversLab].Token.Should().Be(token);
        _handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ATokenInsideTheExpiryMarginIsRefreshed()
    {
        _caches[ForumSite.LoversLab].Token = new OAuthToken("soon-gone", "old-refresh", Start + TimeSpan.FromMinutes(4));
        _handler.Reply(HttpStatusCode.OK, TokenReply());

        var token = await Create().GetUsableToken(ForumSite.LoversLab, CancellationToken.None);

        token.AccessToken.Should().Be("new-access");
    }

    [Fact]
    public async Task ARefusedRefreshClearsTheTokenAndAsksForANewSignIn()
    {
        _caches[ForumSite.LoversLab].Token = Expired();
        _handler.Reply(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");

        var failure = await Create().Invoking(o => o.GetUsableToken(ForumSite.LoversLab, CancellationToken.None))
            .Should().ThrowAsync<SignInAgainException>();

        failure.Which.Message.Should().Be("Your LoversLab session expired. Sign in again, then download again.");
        _caches[ForumSite.LoversLab].Token.Should().BeNull();
    }

    [Fact]
    public async Task AnExpiredTokenWithoutARefreshTokenClearsAndAsksForANewSignInWithoutARequest()
    {
        _caches[ForumSite.VectorPlexus].Token = new OAuthToken("old-access", null, Start - TimeSpan.FromHours(1));

        var failure = await Create().Invoking(o => o.GetUsableToken(ForumSite.VectorPlexus, CancellationToken.None))
            .Should().ThrowAsync<SignInAgainException>();

        failure.Which.Message.Should().Be("Your VectorPlexus session expired. Sign in again, then download again.");
        _caches[ForumSite.VectorPlexus].Token.Should().BeNull();
        _handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task NoStoredTokenMeansNotSignedIn()
    {
        await Create().Invoking(o => o.GetUsableToken(ForumSite.LoversLab, CancellationToken.None))
            .Should().ThrowAsync<NotSignedInException>().WithMessage("You're not signed in to LoversLab");

        _handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ANetworkFailureDuringRefreshKeepsTheToken()
    {
        var expired = Expired();
        _caches[ForumSite.LoversLab].Token = expired;
        _handler.Fail(new HttpRequestException("connection reset"));

        await Create().Invoking(o => o.GetUsableToken(ForumSite.LoversLab, CancellationToken.None))
            .Should().ThrowAsync<HttpRequestException>();

        _caches[ForumSite.LoversLab].Token.Should().Be(expired);
    }

    [Fact]
    public async Task ConcurrentCallsOnAnExpiredTokenSendOneRefreshAndAllGetTheNewToken()
    {
        _caches[ForumSite.LoversLab].Token = Expired();
        _handler.Reply(HttpStatusCode.OK, TokenReply(), delay: TimeSpan.FromMilliseconds(100));
        var oauth = Create();

        var tokens = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => oauth.GetUsableToken(ForumSite.LoversLab, CancellationToken.None))));

        _handler.Requests.Should().ContainSingle("only the first caller refreshes; the rest find its token");
        tokens.Should().OnlyContain(t => t.AccessToken == "new-access");
        _caches[ForumSite.LoversLab].Token!.AccessToken.Should().Be("new-access");
    }

    [Fact]
    public async Task AnotherSitesRefreshIsNotHeldUpBySlowOne()
    {
        _caches[ForumSite.LoversLab].Token = Expired();
        _caches[ForumSite.VectorPlexus].Token = Expired();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.ReplyWhen(request => request.Uri == ForumEndpoints.For(ForumSite.LoversLab).Token, HttpStatusCode.OK, TokenReply("ll-access"), release.Task);
        _handler.Reply(HttpStatusCode.OK, TokenReply("vp-access"));
        var oauth = Create();

        var slow = oauth.GetUsableToken(ForumSite.LoversLab, CancellationToken.None);
        var other = await oauth.GetUsableToken(ForumSite.VectorPlexus, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        other.AccessToken.Should().Be("vp-access");
        slow.IsCompleted.Should().BeFalse();
        release.SetResult();
        (await slow.WaitAsync(TimeSpan.FromSeconds(10))).AccessToken.Should().Be("ll-access");
    }

    private sealed class FakeCredentials(Dictionary<ForumSite, FakeTokenCache> caches) : IForumCredentials
    {
        public IForumTokenCache For(ForumSite site) => caches[site];
    }

    private sealed class FakeTokenCache : IForumTokenCache
    {
        private readonly object _lock = new();
        private OAuthToken? _token;

        public OAuthToken? Token
        {
            get { lock (_lock) return _token; }
            set { lock (_lock) _token = value; }
        }

        public OAuthToken? Current => Token;

        public ValueTask<OAuthToken?> GetTokenAsync(CancellationToken cancellationToken) => new(Token);

        public ValueTask<SecretWriteResult> SetTokenAsync(OAuthToken token, CancellationToken cancellationToken)
        {
            Token = token;
            return new(new SecretWriteResult.Written());
        }

        public ValueTask<SecretWriteResult> ClearAsync(CancellationToken cancellationToken)
        {
            Token = null;
            return new(new SecretWriteResult.Written());
        }

        public ValueTask<OAuthToken?> ReloadTokenAsync(CancellationToken cancellationToken) => new(Token);
    }
}

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body, string? ContentType, string? Authorization, string? UserAgent = null);

/// <summary>
/// A forum that answers from a script and keeps what it was asked, with the body read before the client disposes it.
/// </summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly object _lock = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly List<(Func<RecordedRequest, bool> When, HttpStatusCode Status, string Body, Task? Gate, TimeSpan Delay)> _replies = [];
    private readonly Queue<(HttpStatusCode Status, string Body, int? RetryAfterSeconds)> _once = new();
    private Exception? _failure;

    public IReadOnlyList<RecordedRequest> Requests
    {
        get { lock (_lock) return _requests.ToArray(); }
    }

    public void Reply(HttpStatusCode status, string body, TimeSpan delay = default)
        => _replies.Add((_ => true, status, body, null, delay));

    public void ReplyWhen(Func<RecordedRequest, bool> when, HttpStatusCode status, string body, Task gate)
        => _replies.Insert(0, (when, status, body, gate, default));

    /// <summary>
    /// Answers the next request that no <see cref="ReplyWhen"/> rule claims, ahead of the standing reply; used to
    /// script "429 once, then fine".
    /// </summary>
    public void ReplyOnce(HttpStatusCode status, string body, int? retryAfterSeconds = null)
    {
        lock (_lock) _once.Enqueue((status, body, retryAfterSeconds));
    }

    public void Fail(Exception failure) => _failure = failure;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, body, request.Content?.Headers.ContentType?.MediaType, request.Headers.Authorization?.ToString(), request.Headers.UserAgent.ToString());
        lock (_lock) _requests.Add(recorded);

        if (_failure is not null) throw _failure;
        var claimed = _replies.FirstOrDefault(r => r.Gate is not null && r.When(recorded));
        if (claimed.When is null)
        {
            (HttpStatusCode Status, string Body, int? RetryAfterSeconds)? scripted = null;
            lock (_lock) if (_once.Count > 0) scripted = _once.Dequeue();
            if (scripted is { } once)
            {
                var scriptedResponse = new HttpResponseMessage(once.Status) { Content = new StringContent(once.Body, Encoding.UTF8, "application/json") };
                if (once.RetryAfterSeconds is { } seconds) scriptedResponse.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
                return scriptedResponse;
            }
        }

        var reply = _replies.First(r => r.When(recorded));
        if (reply.Gate is not null) await reply.Gate;
        if (reply.Delay > TimeSpan.Zero) await Task.Delay(reply.Delay, cancellationToken);
        return new HttpResponseMessage(reply.Status) { Content = new StringContent(reply.Body, Encoding.UTF8, "application/json") };
    }
}
