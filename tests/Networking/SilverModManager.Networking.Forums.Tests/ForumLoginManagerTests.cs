using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Networking.Forums.Auth;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Networking.Forums.Tests;

public class ForumLoginManagerTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Start);
    private readonly InMemorySecretStore _store = new();
    private readonly ForumCredentials _credentials;

    public ForumLoginManagerTests() => _credentials = new ForumCredentials(_store, NullLoggerFactory.Instance);

    public void Dispose() => _credentials.Dispose();

    private async Task<ForumLoginManager> Create(string loversLabClientId = "ll-client", string vectorPlexusClientId = "vp-client")
    {
        await _credentials.PrimeAsync(CancellationToken.None);
        var oauth = new ForumOAuth(new HttpClient(), _credentials, site => ForumEndpoints.For(site) with
        {
            ClientId = site == ForumSite.LoversLab ? loversLabClientId : vectorPlexusClientId,
        }, _time, NullLogger<ForumOAuth>.Instance);
        return new ForumLoginManager(oauth, _credentials, _time);
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
        var manager = new ForumLoginManager(oauth, _credentials, _time);

        manager.CanSignIn(ForumSite.LoversLab).Should().BeFalse();
        manager.CanSignIn(ForumSite.VectorPlexus).Should().BeFalse();
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
            .AddForums();
        using var provider = services.BuildServiceProvider();

        var state = provider.GetRequiredService<IForumLoginState>();

        state.Should().BeOfType<ForumLoginManager>();
        state.CanSignIn(ForumSite.LoversLab).Should().BeFalse();
        provider.GetRequiredService<IForumCredentials>().Should().BeSameAs(provider.GetRequiredService<ForumCredentials>());
        provider.GetServices<ISecretPrimable>().Should().ContainSingle().Which.Should().BeSameAs(provider.GetRequiredService<ForumCredentials>());
    }
}
