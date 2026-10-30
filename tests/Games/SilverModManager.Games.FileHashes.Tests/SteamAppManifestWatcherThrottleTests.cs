using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FileHashes.Tests;

/// <summary>
/// How often Steam's manifest writes lead to a look at a game. Manifest events are fed in directly and time is a
/// <see cref="FakeTimeProvider"/>, so nothing touches a Steam folder, waits on a real clock or races a file watcher.
/// The locator stand-in counts every lookup the reaction makes.
/// </summary>
public sealed class SteamAppManifestWatcherThrottleTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BusyInterval = TimeSpan.FromSeconds(30);

    private const string Steamapps = "/steam/steamapps";

    private readonly RealQueryEngineHost _host = new();
    private readonly FakeTimeProvider _time = new();
    private readonly IGameLocator _locator = Substitute.For<IGameLocator>();
    private readonly IGameRegistry _registry = Substitute.For<IGameRegistry>();
    private readonly List<GameInstallation> _installations = [];
    private readonly Dictionary<string, bool> _inProgress = [];
    private readonly List<string> _lookedUp = [];
    private SteamAppManifestWatcher? _watcher;

    public SteamAppManifestWatcherThrottleTests()
    {
        _registry.LocateGameInstallations().Returns(_ => [.. _installations]);
        _registry.TryGetMetadata(Arg.Any<GameInstallation>(), out Arg.Any<GameInstallMetadata.ReadOnly>()).Returns(true);
        _locator.TryLocate(Arg.Any<IGameData>(), out Arg.Any<GameLocatorResult?>())
            .Returns(call =>
            {
                var installation = _installations.Single(i => ReferenceEquals(i.Game, call.Arg<IGameData>()));
                lock (_lookedUp) _lookedUp.Add(installation.LocatorResult.StoreIdentifier);
                call[1] = Result(installation.LocatorResult.StoreIdentifier, _inProgress.GetValueOrDefault(installation.LocatorResult.StoreIdentifier));
                return true;
            });
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _host.Dispose();
    }

    [Fact]
    public async Task ABurstOfWritesToOneManifest_IsOneLookAfterTheDebounce()
    {
        Register("1");
        var watcher = Watcher();

        for (var i = 0; i < 40; i++)
        {
            watcher.OnManifestChanged(Manifest("1"));
            _time.Advance(TimeSpan.FromMilliseconds(40));
        }
        await Settled(watcher);
        LookedUp.Should().BeEmpty("the burst is still inside the debounce");

        _time.Advance(Debounce);
        await Settled(watcher);

        LookedUp.Should().Equal("1");
    }

    [Fact]
    public async Task NothingIsLookedUpBeforeTheDebounceEnds()
    {
        Register("1");
        var watcher = Watcher();

        watcher.OnManifestChanged(Manifest("1"));
        _time.Advance(Debounce - TimeSpan.FromMilliseconds(1));
        await Settled(watcher);

        LookedUp.Should().BeEmpty();
    }

    [Fact]
    public async Task AnEventForOneApp_LooksAtThatAppOnly()
    {
        Register("1");
        Register("2");
        Register("3");
        var watcher = Watcher();

        watcher.OnManifestChanged(Manifest("2"));
        _time.Advance(Debounce);
        await Settled(watcher);

        LookedUp.Should().Equal("2");
        _locator.ReceivedWithAnyArgs(1).TryLocate(default!, out _);
        _locator.DidNotReceive().Locate();
    }

    [Fact]
    public async Task AnAppSteamIsDownloading_IsLookedAtOncePerInterval()
    {
        Register("1");
        _inProgress["1"] = true;
        var watcher = Watcher();

        watcher.OnManifestChanged(Manifest("1"));
        _time.Advance(Debounce);
        await Settled(watcher);
        LookedUp.Should().Equal("1");

        // Steam rewrites the manifest every second for the next half minute.
        for (var i = 0; i < 29; i++)
        {
            watcher.OnManifestChanged(Manifest("1"));
            _time.Advance(TimeSpan.FromSeconds(1));
            await Settled(watcher);
        }
        LookedUp.Should().HaveCount(1, "29 seconds after the last look is inside the interval");

        watcher.OnManifestChanged(Manifest("1"));
        _time.Advance(TimeSpan.FromSeconds(1));
        await Settled(watcher);
        LookedUp.Should().HaveCount(2, "the interval has passed since the last look");
    }

    [Fact]
    public async Task EventsWhileDownloading_DoNotPushTheNextLookPastTheInterval()
    {
        Register("1");
        _inProgress["1"] = true;
        var watcher = Watcher();
        watcher.OnManifestChanged(Manifest("1"));
        _time.Advance(Debounce);
        await Settled(watcher);

        // Events keep coming; none of them may push the next look past the interval.
        var elapsed = TimeSpan.Zero;
        while (elapsed < BusyInterval)
        {
            watcher.OnManifestChanged(Manifest("1"));
            _time.Advance(TimeSpan.FromMilliseconds(500));
            elapsed += TimeSpan.FromMilliseconds(500);
        }
        await Settled(watcher);

        LookedUp.Should().HaveCount(2);
    }

    [Fact]
    public async Task AnAppThatSettles_IsLookedAtAfterTheShortDebounceAgain()
    {
        Register("1");
        _inProgress["1"] = true;
        var watcher = Watcher();
        watcher.OnManifestChanged(Manifest("1"));
        _time.Advance(Debounce);
        await Settled(watcher);

        _inProgress["1"] = false;
        watcher.OnManifestChanged(Manifest("1"));
        _time.Advance(BusyInterval);
        await Settled(watcher);
        LookedUp.Should().HaveCount(2, "the settled state is seen once the interval has passed");

        watcher.OnManifestChanged(Manifest("1"));
        _time.Advance(Debounce);
        await Settled(watcher);

        LookedUp.Should().HaveCount(3, "a settled app is back on the short debounce");
    }

    [Fact]
    public async Task EachAppHasItsOwnDebounceAndInterval()
    {
        Register("1");
        Register("2");
        _inProgress["1"] = true;
        var watcher = Watcher();
        watcher.OnManifestChanged(Manifest("1"));
        _time.Advance(Debounce);
        await Settled(watcher);

        watcher.OnManifestChanged(Manifest("2"));
        _time.Advance(Debounce);
        await Settled(watcher);

        LookedUp.Should().Equal("1", "2");
    }

    [Fact]
    public async Task AnAppTheAppDoesNotManage_TriggersNothing()
    {
        Register("1");
        _registry.TryGetMetadata(Arg.Any<GameInstallation>(), out Arg.Any<GameInstallMetadata.ReadOnly>()).Returns(false);
        var watcher = Watcher();

        watcher.OnManifestChanged(Manifest("1"));
        _time.Advance(Debounce);
        await Settled(watcher);

        LookedUp.Should().BeEmpty();
    }

    [Fact]
    public async Task AManifestOfAnAppNoGameIsRegisteredFor_TriggersNothing()
    {
        Register("1");
        var watcher = Watcher();

        watcher.OnManifestChanged(Manifest("99"));
        _time.Advance(Debounce);
        await Settled(watcher);

        LookedUp.Should().BeEmpty();
        _registry.DidNotReceiveWithAnyArgs().TryGetMetadata(default!, out _);
    }

    [Fact]
    public async Task AFileThatIsNotAnAppManifest_TriggersNothing()
    {
        Register("1");
        var watcher = Watcher();

        watcher.OnManifestChanged(Path.Combine(Steamapps, "libraryfolders.vdf"));
        watcher.OnManifestChanged(Path.Combine(Steamapps, "appmanifest_x.acf"));
        _time.Advance(Debounce);
        await Settled(watcher);

        LookedUp.Should().BeEmpty();
    }

    [Fact]
    public async Task TheLibraryRescan_LocatesNoGame()
    {
        Register("1");
        var watcher = Watcher(rescan: TimeSpan.FromSeconds(30));
        await watcher.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => watcher.IsRescanning);

        for (var i = 0; i < 20; i++) _time.Advance(TimeSpan.FromSeconds(30));

        _locator.DidNotReceiveWithAnyArgs().TryLocate(default!, out _);
        _locator.DidNotReceive().Locate();
    }

    [Fact]
    public async Task AfterTheWatcherStops_ALateTimerLooksAtNothing()
    {
        Register("1");
        var watcher = Watcher();
        watcher.OnManifestChanged(Manifest("1"));

        await watcher.StopAsync(TestContext.Current.CancellationToken);
        _time.Advance(Debounce);
        await Settled(watcher);

        LookedUp.Should().BeEmpty();
    }

    private string[] LookedUp
    {
        get { lock (_lookedUp) return [.. _lookedUp]; }
    }

    private static string Manifest(string appId) => Path.Combine(Steamapps, $"appmanifest_{appId}.acf");

    private SteamAppManifestWatcher Watcher(TimeSpan? rescan = null) => _watcher = new SteamAppManifestWatcher(
        _registry,
        new StoreUpdateReaction(_registry, Substitute.For<IFileHashesService>(), Substitute.For<IDiagnosticManager>(), _host.OpenInMemory("main"), NullLogger<StoreUpdateReaction>.Instance),
        NullLogger<SteamAppManifestWatcher>.Instance,
        Debounce,
        rescan,
        BusyInterval,
        _time);

    private static async Task Settled(SteamAppManifestWatcher watcher) => await watcher.WhenIdle();

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
    }

    private void Register(string appId)
    {
        var result = Result(appId, inProgress: false);
        var path = FileSystem.Shared.FromUnsanitizedFullPath(result.Path.ToString());
        _installations.Add(new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, path))));
    }

    private GameLocatorResult Result(string appId, bool inProgress)
    {
        // One game object per app, so the locator stand-in can tell which app it was asked about.
        var game = _installations.FirstOrDefault(i => i.LocatorResult.StoreIdentifier == appId)?.Game ?? Substitute.For<SubstitutableGame>();
        return new GameLocatorResult
        {
            Game = game,
            Path = FileSystem.Shared.FromUnsanitizedFullPath($"{Steamapps}/common/Game{appId}"),
            Store = GameStore.Steam,
            StoreIdentifier = appId,
            LocatorIds = [LocatorId.From(appId)],
            Locator = _locator,
            StoreBuildId = "b",
            StoreUpdateInProgress = inProgress,
            StoreUpdatePending = inProgress,
        };
    }
}
