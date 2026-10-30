using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using R3;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Backend.Jobs;
using SilverModManager.Networking.Forums.Auth;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Networking.Forums.Tests;

public class ForumAccountsTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Start);
    private readonly InMemorySecretStore _store = new();
    private readonly ForumCredentials _credentials;
    private readonly JobMonitor _monitor = new(NullLogger<JobMonitor>.Instance);

    public ForumAccountsTests() => _credentials = new ForumCredentials(_store, NullLoggerFactory.Instance);

    public void Dispose()
    {
        _credentials.Dispose();
        _monitor.Dispose();
    }

    private async Task<ForumAccounts> Create(string loversLabClientId = "ll-client")
    {
        await _credentials.PrimeAsync(CancellationToken.None);
        var oauth = new ForumOAuth(new HttpClient(), _credentials, site => ForumEndpoints.For(site) with
        {
            ClientId = site == ForumSite.LoversLab ? loversLabClientId : "",
        }, _time, NullLogger<ForumOAuth>.Instance);
        var login = new ForumLoginManager(oauth, _credentials, _time, _monitor, Substitute.For<IOSInterop>(), NullLogger<ForumLoginManager>.Instance);
        return new ForumAccounts(login, _credentials, _time);
    }

    private Task Store(ForumSite site, OAuthToken token)
        => _credentials.For(site).SetTokenAsync(token, CancellationToken.None).AsTask();

    [Fact]
    public async Task ASiteWithoutAClientIdIsNotSetUp()
    {
        var accounts = await Create();

        accounts.StatusOf(ForumSite.VectorPlexus).State.Should().Be(ForumAccountState.NotSetUp);
    }

    [Fact]
    public async Task ASiteWithAClientIdAndNoTokenIsSignedOut()
    {
        var accounts = await Create();

        accounts.StatusOf(ForumSite.LoversLab).State.Should().Be(ForumAccountState.SignedOut);
    }

    [Fact]
    public async Task AUsableTokenIsSignedInAndNamesWhereItIsKept()
    {
        var accounts = await Create();
        await Store(ForumSite.LoversLab, new OAuthToken("a", null, Start + TimeSpan.FromHours(1)));

        var status = accounts.StatusOf(ForumSite.LoversLab);

        status.State.Should().Be(ForumAccountState.SignedIn);
        status.Source.Should().Be("in-memory store");
    }

    [Fact]
    public async Task AnExpiredTokenWithNoRefreshTokenIsExpired()
    {
        var accounts = await Create();
        await Store(ForumSite.LoversLab, new OAuthToken("a", null, Start - TimeSpan.FromMinutes(1)));

        accounts.StatusOf(ForumSite.LoversLab).State.Should().Be(ForumAccountState.Expired);
    }

    [Fact]
    public async Task AnExpiredTokenThatCanBeRefreshedIsStillSignedIn()
    {
        var accounts = await Create();
        await Store(ForumSite.LoversLab, new OAuthToken("a", "r", Start - TimeSpan.FromDays(1)));

        accounts.StatusOf(ForumSite.LoversLab).State.Should().Be(ForumAccountState.SignedIn);
    }

    [Fact]
    public async Task ChangedAnnouncesTheSiteWhoseTokenAppearsOrGoes()
    {
        var accounts = await Create();
        var seen = new List<ForumSite>();
        using var subscription = accounts.Changed.Subscribe(state => seen.Add(state));

        await Store(ForumSite.LoversLab, new OAuthToken("a", "r", Start + TimeSpan.FromHours(1)));
        await accounts.SignOut(ForumSite.LoversLab, CancellationToken.None);

        seen.Should().Equal(ForumSite.LoversLab, ForumSite.LoversLab);
    }

    [Fact]
    public async Task ChangedAnnouncesAnExpiredTokenThatIsReplacedByAFreshOne()
    {
        var accounts = await Create();
        await Store(ForumSite.LoversLab, new OAuthToken("old", null, Start - TimeSpan.FromMinutes(1)));
        var seen = new List<ForumSite>();
        using var subscription = accounts.Changed.Subscribe(state => seen.Add(state));

        await Store(ForumSite.LoversLab, new OAuthToken("new", null, Start + TimeSpan.FromHours(1)));

        seen.Should().Equal(ForumSite.LoversLab);
        accounts.StatusOf(ForumSite.LoversLab).State.Should().Be(ForumAccountState.SignedIn);
    }

    [Fact]
    public async Task ChangedStaysSilentWhenARefreshReplacesAUsableTokenWithAnother()
    {
        var accounts = await Create();
        await Store(ForumSite.LoversLab, new OAuthToken("old", "r", Start + TimeSpan.FromMinutes(1)));
        var seen = new List<ForumSite>();
        using var subscription = accounts.Changed.Subscribe(state => seen.Add(state));

        await Store(ForumSite.LoversLab, new OAuthToken("new", "r", Start + TimeSpan.FromHours(1)));

        seen.Should().BeEmpty();
    }

    [Fact]
    public async Task ChangedIsSilentAboutTheCurrentStateAndTheOtherSite()
    {
        var accounts = await Create();
        await Store(ForumSite.LoversLab, new OAuthToken("a", "r", Start + TimeSpan.FromHours(1)));
        var seen = new List<ForumSite>();
        using var subscription = accounts.Changed.Subscribe(state => seen.Add(state));

        await Store(ForumSite.VectorPlexus, new OAuthToken("b", "r", Start + TimeSpan.FromHours(1)));

        seen.Should().Equal(ForumSite.VectorPlexus);
    }

    [Fact]
    public async Task SignOutForgetsTheToken()
    {
        var accounts = await Create();
        await Store(ForumSite.LoversLab, new OAuthToken("a", "r", Start + TimeSpan.FromHours(1)));

        await accounts.SignOut(ForumSite.LoversLab, CancellationToken.None);

        accounts.StatusOf(ForumSite.LoversLab).State.Should().Be(ForumAccountState.SignedOut);
    }

    [Fact]
    public void TheHostRegistersTheAccountsOverTheSameCaches()
    {
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<ForumAuthSettings>(Arg.Any<string?>()).Returns(new ForumAuthSettings());
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<ISecretStore>(_store)
            .AddSingleton(settings)
            .AddSingleton<SilverModManager.Sdk.Jobs.IJobMonitor>(_monitor)
            .AddSingleton(Substitute.For<IOSInterop>())
            .AddForums()
            .BuildServiceProvider();

        provider.GetRequiredService<IForumAccounts>().Should().BeOfType<ForumAccounts>();
        provider.GetRequiredService<IForumAccounts>().StatusOf(ForumSite.LoversLab).State.Should().Be(ForumAccountState.NotSetUp);
    }
}
