using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FileHashes.Tests;

/// <summary>
/// A changed appmanifest under a watched library leads to one look at each Steam game, after the debounce. A real
/// temp folder and a short debounce; the reaction runs for real over a locator stand-in that counts the looks.
/// </summary>
public sealed class SteamAppManifestWatcherTests : IDisposable
{
    private readonly RealQueryEngineHost _host = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "silver-watcher-" + Guid.NewGuid().ToString("N"));
    private readonly string _steamapps;
    private readonly IGameLocator _locator = Substitute.For<IGameLocator>();
    private readonly IGameRegistry _registry = Substitute.For<IGameRegistry>();
    private int _looks;

    public SteamAppManifestWatcherTests()
    {
        _steamapps = Path.Combine(_root, "steamapps");
        Directory.CreateDirectory(Path.Combine(_steamapps, "common", "Game"));
    }

    public void Dispose()
    {
        _host.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task ManyRewritesOfTheManifest_LeadToOneLookAtTheGameAfterTheDebounce()
    {
        var installation = Installation(GameStore.Steam, Path.Combine(_steamapps, "common", "Game"));
        _registry.LocateGameInstallations().Returns([installation]);
        _locator.TryLocate(Arg.Any<IGameData>(), out Arg.Any<GameLocatorResult?>())
            .Returns(call => { Interlocked.Increment(ref _looks); call[1] = installation.LocatorResult; return true; });
        // A debounce much longer than the burst, so an event the file system delivers late does not become a second look.
        using var watcher = Watcher(TimeSpan.FromMilliseconds(500));
        watcher.Watch();

        for (var i = 0; i < 5; i++) await File.WriteAllTextAsync(Path.Combine(_steamapps, "appmanifest_1.acf"), "x" + i, TestContext.Current.CancellationToken);

        await WaitFor(() => Volatile.Read(ref _looks) >= 1);
        await Task.Delay(1200, TestContext.Current.CancellationToken);
        Volatile.Read(ref _looks).Should().Be(1);
    }

    [Fact]
    public async Task AManifestWrittenToATempFileAndRenamedIntoPlace_LeadsToALook()
    {
        var installation = Installation(GameStore.Steam, Path.Combine(_steamapps, "common", "Game"));
        _registry.LocateGameInstallations().Returns([installation]);
        _locator.TryLocate(Arg.Any<IGameData>(), out Arg.Any<GameLocatorResult?>())
            .Returns(call => { Interlocked.Increment(ref _looks); call[1] = installation.LocatorResult; return true; });
        using var watcher = Watcher();
        watcher.Watch();

        var temp = Path.Combine(_steamapps, "appmanifest_1.acf.tmp");
        await File.WriteAllTextAsync(temp, "x", TestContext.Current.CancellationToken);
        File.Move(temp, Path.Combine(_steamapps, "appmanifest_1.acf"));

        await WaitFor(() => Volatile.Read(ref _looks) >= 1);
        Volatile.Read(ref _looks).Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task AfterStopAsync_AManifestChangeLeadsToNoLook()
    {
        var installation = Installation(GameStore.Steam, Path.Combine(_steamapps, "common", "Game"));
        _registry.LocateGameInstallations().Returns([installation]);
        _locator.TryLocate(Arg.Any<IGameData>(), out Arg.Any<GameLocatorResult?>())
            .Returns(call => { Interlocked.Increment(ref _looks); call[1] = installation.LocatorResult; return true; });
        using var watcher = Watcher();
        watcher.Watch();

        await watcher.StopAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_steamapps, "appmanifest_1.acf"), "x", TestContext.Current.CancellationToken);

        await Task.Delay(400, TestContext.Current.CancellationToken);
        Volatile.Read(ref _looks).Should().Be(0);
    }

    [Fact]
    public async Task WatchAfterStopAsync_StartsNoWatcher()
    {
        var installation = Installation(GameStore.Steam, Path.Combine(_steamapps, "common", "Game"));
        _registry.LocateGameInstallations().Returns([installation]);
        _locator.TryLocate(Arg.Any<IGameData>(), out Arg.Any<GameLocatorResult?>())
            .Returns(call => { Interlocked.Increment(ref _looks); call[1] = installation.LocatorResult; return true; });
        using var watcher = Watcher();
        await watcher.StopAsync(TestContext.Current.CancellationToken);
        watcher.Watch();

        await File.WriteAllTextAsync(Path.Combine(_steamapps, "appmanifest_1.acf"), "x", TestContext.Current.CancellationToken);

        await Task.Delay(400, TestContext.Current.CancellationToken);
        Volatile.Read(ref _looks).Should().Be(0);
    }

    [Fact]
    public async Task WhenLocatingTheRegisteredGamesThrows_StartAsyncLogsItInsteadOfLosingIt()
    {
        _registry.LocateGameInstallations().Returns(_ => throw new InvalidOperationException("registry broke"));
        var logger = new CapturingLogger<SteamAppManifestWatcher>();
        using var watcher = new SteamAppManifestWatcher(_registry, Reaction(), logger, TimeSpan.FromMilliseconds(100));

        await watcher.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => logger.Errors.Count > 0);

        logger.Errors.Should().ContainSingle().Which.Should().Contain("Could not start watching");
    }

    [Fact]
    public async Task AGameInALibraryRegisteredAfterStart_IsWatchedOnceTheRegistryHasIt()
    {
        var second = Path.Combine(_root, "second", "steamapps");
        Directory.CreateDirectory(Path.Combine(second, "common", "Other"));
        var first = Installation(GameStore.Steam, Path.Combine(_steamapps, "common", "Game"));
        var added = Installation(GameStore.Steam, Path.Combine(second, "common", "Other"));
        var installations = new[] { first };
        _registry.LocateGameInstallations().Returns(_ => [.. installations]);
        var lookedAt = new System.Collections.Concurrent.ConcurrentBag<string>();
        _locator.TryLocate(Arg.Any<IGameData>(), out Arg.Any<GameLocatorResult?>())
            .Returns(call => { call[1] = added.LocatorResult; lookedAt.Add("any"); return true; });
        using var watcher = new SteamAppManifestWatcher(_registry, Reaction(), NullLogger<SteamAppManifestWatcher>.Instance, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        await watcher.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => watcher.IsWatching(_steamapps));
        watcher.IsWatching(second).Should().BeFalse("the registry does not hold the second library yet");

        installations = [first, added];
        await WaitFor(() => watcher.IsWatching(second));
        watcher.IsWatching(second).Should().BeTrue("a rescan arms the library once the registry has it");
        lookedAt.Clear();
        await File.WriteAllTextAsync(Path.Combine(second, "appmanifest_2.acf"), "x", TestContext.Current.CancellationToken);

        await WaitFor(() => !lookedAt.IsEmpty);
        lookedAt.Should().NotBeEmpty("the library registered after start is watched too");
    }

    [Fact]
    public async Task OverlappingWatchCalls_ArmEachLibraryOnce()
    {
        var installation = Installation(GameStore.Steam, Path.Combine(_steamapps, "common", "Game"));
        _registry.LocateGameInstallations().Returns([installation]);
        using var watcher = Watcher();
        using var barrier = new Barrier(8);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            watcher.Watch();
        }, TestContext.Current.CancellationToken)));

        watcher.WatcherCount.Should().Be(1);
    }

    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public System.Collections.Concurrent.ConcurrentBag<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Error) Errors.Add(formatter(state, exception));
        }
    }

    [Fact]
    public async Task AFileThatIsNotAnAppManifest_LeadsToNoLook()
    {
        var installation = Installation(GameStore.Steam, Path.Combine(_steamapps, "common", "Game"));
        _registry.LocateGameInstallations().Returns([installation]);
        _locator.TryLocate(Arg.Any<IGameData>(), out Arg.Any<GameLocatorResult?>())
            .Returns(call => { Interlocked.Increment(ref _looks); call[1] = installation.LocatorResult; return true; });
        using var watcher = Watcher();
        watcher.Watch();

        await File.WriteAllTextAsync(Path.Combine(_steamapps, "libraryfolders.vdf"), "x", TestContext.Current.CancellationToken);

        await Task.Delay(400, TestContext.Current.CancellationToken);
        Volatile.Read(ref _looks).Should().Be(0);
    }

    [Fact]
    public async Task AGameFromAnotherStore_IsNotWatchedNorLookedAt()
    {
        var installation = Installation(GameStore.GOG, Path.Combine(_steamapps, "common", "Game"));
        _registry.LocateGameInstallations().Returns([installation]);
        using var watcher = Watcher();
        watcher.Watch();

        await File.WriteAllTextAsync(Path.Combine(_steamapps, "appmanifest_1.acf"), "x", TestContext.Current.CancellationToken);

        await Task.Delay(400, TestContext.Current.CancellationToken);
        _locator.DidNotReceiveWithAnyArgs().TryLocate(default!, out _);
    }

    private SteamAppManifestWatcher Watcher(TimeSpan? debounce = null) => new(
        _registry,
        Reaction(),
        NullLogger<SteamAppManifestWatcher>.Instance,
        debounce ?? TimeSpan.FromMilliseconds(100));

    private StoreUpdateReaction Reaction() =>
        new(_registry, Substitute.For<IFileHashesService>(), Substitute.For<IDiagnosticManager>(), _host.OpenInMemory("main"), NullLogger<StoreUpdateReaction>.Instance);

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
    }

    private GameInstallation Installation(GameStore store, string gamePath)
    {
        var path = FileSystem.Shared.FromUnsanitizedFullPath(gamePath);
        var result = new GameLocatorResult
        {
            Game = Substitute.For<SubstitutableGame>(),
            Path = path,
            Store = store,
            StoreIdentifier = "1",
            LocatorIds = [LocatorId.From("1")],
            Locator = _locator,
            StoreBuildId = "b",
        };
        return new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, path)));
    }
}
