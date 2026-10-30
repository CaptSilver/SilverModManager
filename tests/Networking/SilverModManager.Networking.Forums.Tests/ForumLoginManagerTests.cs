using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Backend.Jobs;
using SilverModManager.Networking.Forums.Auth;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Networking.Forums.Tests;

public class ForumLoginManagerTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Start);
    private readonly InMemorySecretStore _store = new();
    private readonly ForumCredentials _credentials;
    private readonly JobMonitor _monitor = new(NullLogger<JobMonitor>.Instance);
    private readonly IOSInterop _os = Substitute.For<IOSInterop>();
    private readonly StubHandler _forum = new();
    private readonly TaskCompletionSource<Uri> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ForumLoginManagerTests()
    {
        _credentials = new ForumCredentials(_store, NullLoggerFactory.Instance);
        _os.When(o => o.OpenUri(Arg.Any<Uri>())).Do(call => _opened.TrySetResult(call.Arg<Uri>()));
    }

    public void Dispose()
    {
        _credentials.Dispose();
        _monitor.Dispose();
    }

    private ForumLoginManager NewManager(ForumOAuth oauth)
        => new(oauth, _credentials, _time, _monitor, _os, NullLogger<ForumLoginManager>.Instance);

    private async Task<ForumLoginManager> Create(string loversLabClientId = "ll-client", string vectorPlexusClientId = "vp-client")
    {
        await _credentials.PrimeAsync(CancellationToken.None);
        var oauth = new ForumOAuth(new HttpClient(_forum), _credentials, site => ForumEndpoints.For(site) with
        {
            ClientId = site == ForumSite.LoversLab ? loversLabClientId : vectorPlexusClientId,
        }, _time, NullLogger<ForumOAuth>.Instance);
        return NewManager(oauth);
    }

    private async Task Store(ForumSite site, OAuthToken token)
        => await _credentials.For(site).SetTokenAsync(token, CancellationToken.None);

    [Fact]
    public async Task NothingStoredMeansSignedOut()
    {
        var manager = await Create();

        manager.IsSignedIn(ForumSite.LoversLab).Should().BeFalse();
        manager.IsSignedIn(ForumSite.VectorPlexus).Should().BeFalse();
    }

    [Fact]
    public async Task AValidTokenMeansSignedInForThatSiteOnly()
    {
        var manager = await Create();
        await Store(ForumSite.LoversLab, new OAuthToken("a", null, Start + TimeSpan.FromHours(1)));

        manager.IsSignedIn(ForumSite.LoversLab).Should().BeTrue();
        manager.IsSignedIn(ForumSite.VectorPlexus).Should().BeFalse();
    }

    [Fact]
    public async Task AnExpiredTokenWithARefreshTokenIsStillSignedIn()
    {
        var manager = await Create();
        await Store(ForumSite.LoversLab, new OAuthToken("a", "r", Start - TimeSpan.FromDays(1)));

        manager.IsSignedIn(ForumSite.LoversLab).Should().BeTrue();
    }

    [Fact]
    public async Task AnExpiredTokenWithoutARefreshTokenIsSignedOut()
    {
        var manager = await Create();
        await Store(ForumSite.LoversLab, new OAuthToken("a", null, Start - TimeSpan.FromMinutes(1)));

        manager.IsSignedIn(ForumSite.LoversLab).Should().BeFalse();
    }

    [Fact]
    public async Task ATokenThatCannotBeRefreshedStopsCountingOnceItExpires()
    {
        var manager = await Create();
        await Store(ForumSite.LoversLab, new OAuthToken("a", null, Start + TimeSpan.FromHours(1)));
        manager.IsSignedIn(ForumSite.LoversLab).Should().BeTrue();

        _time.Advance(TimeSpan.FromMinutes(54));
        manager.IsSignedIn(ForumSite.LoversLab).Should().BeTrue();

        _time.Advance(TimeSpan.FromMinutes(2));
        manager.IsSignedIn(ForumSite.LoversLab).Should().BeFalse("it is within five minutes of expiry");
    }

    [Fact]
    public async Task SigningOutIsReflectedAtOnce()
    {
        var manager = await Create();
        await Store(ForumSite.LoversLab, new OAuthToken("a", "r", Start + TimeSpan.FromHours(1)));

        await _credentials.For(ForumSite.LoversLab).ClearAsync(CancellationToken.None);

        manager.IsSignedIn(ForumSite.LoversLab).Should().BeFalse();
    }

    [Fact]
    public async Task SignInIsOffWhileTheClientIdIsEmpty()
    {
        var manager = await Create(loversLabClientId: "", vectorPlexusClientId: "");

        manager.CanSignIn(ForumSite.LoversLab).Should().BeFalse();
        manager.CanSignIn(ForumSite.VectorPlexus).Should().BeFalse();
    }

    [Fact]
    public async Task SignInIsOnPerSiteOnceItHasAClientId()
    {
        var manager = await Create(vectorPlexusClientId: "");

        manager.CanSignIn(ForumSite.LoversLab).Should().BeTrue();
        manager.CanSignIn(ForumSite.VectorPlexus).Should().BeFalse();
    }

    [Fact]
    public void NoSiteShipsWithSignInOn()
    {
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<ForumAuthSettings>(Arg.Any<string?>()).Returns(new ForumAuthSettings());
        var oauth = new ForumOAuth(new HttpClient(), _credentials, settings, _time, NullLogger<ForumOAuth>.Instance);
        var manager = NewManager(oauth);

        manager.CanSignIn(ForumSite.LoversLab).Should().BeFalse();
        manager.CanSignIn(ForumSite.VectorPlexus).Should().BeFalse();
    }

    [Fact]
    public async Task SignInStoresTheTokenTheForumIssuesForThatSiteOnly()
    {
        var manager = await Create();
        _forum.Reply(System.Net.HttpStatusCode.OK, """{"access_token":"issued","refresh_token":"r","expires_in":3600}""");

        var signIn = manager.SignIn(ForumSite.LoversLab, CancellationToken.None);
        var opened = await _opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var query = System.Web.HttpUtility.ParseQueryString(opened.Query);
        using var browser = new HttpClient();
        using var _ = await browser.GetAsync(new Uri($"{query["redirect_uri"]}?code=c&state={Uri.EscapeDataString(query["state"]!)}"));
        await signIn.WaitAsync(TimeSpan.FromSeconds(10));

        (await _credentials.For(ForumSite.LoversLab).GetTokenAsync(CancellationToken.None))!.AccessToken.Should().Be("issued");
        (await _credentials.For(ForumSite.VectorPlexus).GetTokenAsync(CancellationToken.None)).Should().BeNull();
        manager.IsSignedIn(ForumSite.LoversLab).Should().BeTrue();
    }

    [Fact]
    public async Task ADeclinedSignInLeavesTheSiteSignedOutAndSaysSo()
    {
        var manager = await Create();

        var signIn = manager.SignIn(ForumSite.LoversLab, CancellationToken.None);
        var opened = await _opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var query = System.Web.HttpUtility.ParseQueryString(opened.Query);
        using var browser = new HttpClient();
        using var _ = await browser.GetAsync(new Uri($"{query["redirect_uri"]}?error=access_denied&state={Uri.EscapeDataString(query["state"]!)}"));

        await signIn.Invoking(t => t.WaitAsync(TimeSpan.FromSeconds(10))).Should().ThrowAsync<SignInDeclinedException>();
        manager.IsSignedIn(ForumSite.LoversLab).Should().BeFalse();
    }

    [Fact]
    public async Task CancellingASignInLeavesTheSiteSignedOutWithoutAnError()
    {
        var manager = await Create();
        using var cts = new CancellationTokenSource();

        var signIn = manager.SignIn(ForumSite.LoversLab, cts.Token);
        await _opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        await signIn.WaitAsync(TimeSpan.FromSeconds(10));
        manager.IsSignedIn(ForumSite.LoversLab).Should().BeFalse();
    }

    [Fact]
    public async Task SignInRefusesWithoutAClientIdAndStartsNoJob()
    {
        var manager = await Create(loversLabClientId: "");

        await manager.Invoking(m => m.SignIn(ForumSite.LoversLab, CancellationToken.None)).Should()
            .ThrowAsync<SignInNotSetUpException>().WithMessage("Sign-in for LoversLab isn't set up yet");

        _os.DidNotReceive().OpenUri(Arg.Any<Uri>());
        _monitor.Snapshot().Should().BeEmpty();
        _forum.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SignOutForgetsThatSitesTokenOnly()
    {
        var manager = await Create();
        await Store(ForumSite.LoversLab, new OAuthToken("a", "r", Start + TimeSpan.FromHours(1)));
        await Store(ForumSite.VectorPlexus, new OAuthToken("b", "r", Start + TimeSpan.FromHours(1)));

        await manager.SignOut(ForumSite.LoversLab, CancellationToken.None);

        manager.IsSignedIn(ForumSite.LoversLab).Should().BeFalse();
        manager.IsSignedIn(ForumSite.VectorPlexus).Should().BeTrue();
    }

    [Fact]
    public void TheHostWiresTheLoginStateToTheSameCaches()
    {
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<ForumAuthSettings>(Arg.Any<string?>()).Returns(new ForumAuthSettings());
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<ISecretStore>(_store)
            .AddSingleton(settings)
            .AddSingleton<IJobMonitor>(_monitor)
            .AddSingleton(_os)
            .AddForums();
        using var provider = services.BuildServiceProvider();

        var state = provider.GetRequiredService<IForumLoginState>();

        state.Should().BeOfType<ForumLoginManager>();
        state.CanSignIn(ForumSite.LoversLab).Should().BeFalse();
        provider.GetRequiredService<IForumCredentials>().Should().BeSameAs(provider.GetRequiredService<ForumCredentials>());
        provider.GetServices<ISecretPrimable>().Should().ContainSingle().Which.Should().BeSameAs(provider.GetRequiredService<ForumCredentials>());
    }
}
