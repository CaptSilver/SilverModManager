using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Backend.Games.Locators;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using NSubstitute;

namespace SilverModManager.Backend.Tests.Games;

public class WinePrefixWrappingLocatorTests
{
    [Fact]
    public void Locate_StampsEachWrappedResultWithThePrefixItWasFoundIn()
    {
        var fileSystem = new InMemoryFileSystem(OSInformation.FakeUnix);
        var prefix = fileSystem.GetKnownPath(KnownPath.HomeDirectory).Combine(".wine");
        fileSystem.CreateDirectory(prefix.Combine("drive_c"));
        fileSystem.WriteAllText(prefix.Combine("system.reg"), "WINE REGISTRY Version 2\n");
        fileSystem.WriteAllText(prefix.Combine("user.reg"), "WINE REGISTRY Version 2\n");
        fileSystem.WriteAllText(prefix.Combine("userdef.reg"), "WINE REGISTRY Version 2\n");

        var game = Substitute.For<IGameData>();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IFileSystem>(fileSystem)
            .BuildServiceProvider();

        var wrapped = new StubLocator(game);
        var locator = new WinePrefixWrappingLocator(services, [(_, _, _, _) => wrapped]);

        var result = locator.Locate().Should().ContainSingle().Subject;

        result.WinePrefixPath.Should().Be(prefix);
        result.Compatibility.Should().NotBeNull();
        result.Compatibility!.Kind.Should().Be(CompatibilityLayerKind.PlainWine);
        result.Compatibility.Prefix.Should().Be(prefix);
        result.Store.Should().Be(GameStore.GOG, "wrapping must not change where the game was bought");
        result.Locator.Should().BeSameAs(wrapped);
    }

    [Fact]
    public void Wrap_ForwardsStoreRootStoreBuildIdAndStoreUpdatePending()
    {
        var fileSystem = new InMemoryFileSystem(OSInformation.FakeUnix);
        var prefix = fileSystem.GetKnownPath(KnownPath.HomeDirectory).Combine(".wine");
        fileSystem.CreateDirectory(prefix.Combine("drive_c"));
        fileSystem.WriteAllText(prefix.Combine("system.reg"), "WINE REGISTRY Version 2\n");
        fileSystem.WriteAllText(prefix.Combine("user.reg"), "WINE REGISTRY Version 2\n");
        fileSystem.WriteAllText(prefix.Combine("userdef.reg"), "WINE REGISTRY Version 2\n");

        var storeRoot = fileSystem.GetKnownPath(KnownPath.HomeDirectory).Combine("store");
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IFileSystem>(fileSystem)
            .BuildServiceProvider();

        var wrapped = new StubLocator(Substitute.For<IGameData>(), storeRoot, "42", storeUpdatePending: true, storeLastWrite: DateTimeOffset.UnixEpoch.AddDays(5), storeUpdatesInBackground: true);
        var locator = new WinePrefixWrappingLocator(services, [(_, _, _, _) => wrapped]);

        var result = locator.Locate().Should().ContainSingle().Subject;

        result.StoreRoot.Should().Be(storeRoot);
        result.StoreBuildId.Should().Be("42");
        result.StoreUpdatePending.Should().BeTrue();
        result.StoreLastWrite.Should().Be(DateTimeOffset.UnixEpoch.AddDays(5));
        result.StoreUpdatesInBackground.Should().BeTrue();
    }

    [Fact]
    public void Locate_OneWrappedLocatorThrows_KeepsTheResultsOfTheOthers()
    {
        // The wrapped locators read through the prefix's overlay, which can sit on a drive that is gone.
        var fileSystem = new InMemoryFileSystem(OSInformation.FakeUnix);
        var prefix = fileSystem.GetKnownPath(KnownPath.HomeDirectory).Combine(".wine");
        fileSystem.CreateDirectory(prefix.Combine("drive_c"));
        fileSystem.WriteAllText(prefix.Combine("system.reg"), "WINE REGISTRY Version 2\n");
        fileSystem.WriteAllText(prefix.Combine("user.reg"), "WINE REGISTRY Version 2\n");
        fileSystem.WriteAllText(prefix.Combine("userdef.reg"), "WINE REGISTRY Version 2\n");

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IFileSystem>(fileSystem)
            .BuildServiceProvider();

        var working = new StubLocator(Substitute.For<IGameData>());
        var locator = new WinePrefixWrappingLocator(services, [
            (_, _, _, _) => throw new IOException("No such device"),
            (_, _, _, _) => new ThrowingLocator(),
            (_, _, _, _) => working,
        ]);

        var results = locator.Locate();

        results.Should().ContainSingle().Which.Locator.Should().BeSameAs(working);
    }

    // Bottles keeps bottles in <data>/bottles/<name>/ with a bottle.yml holding a top-level `Runner:` key,
    // and unpacks runners into <data>/runners/<Runner>/ (Bottles 51.x, bottles/backend/managers/data.py and
    // bottles/backend/models/config.py). That layout has not been checked against a real Bottles install;
    // a bottle whose runner is installed but reported without a wine binary would show it is wrong.
    [Theory]
    [InlineData("xdg", true)]
    [InlineData("xdg", false)]
    [InlineData(".var/app/com.usebottles.bottles/data/bottles", true)]
    public void Locate_BottlesPrefix_ReportsPlainWineWithTheBottleRunner(string dataRoot, bool runnerInstalled)
    {
        var fileSystem = new InMemoryFileSystem(OSInformation.FakeUnix);
        var root = dataRoot == "xdg"
            ? fileSystem.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine("bottles")
            : fileSystem.GetKnownPath(KnownPath.HomeDirectory).Combine(dataRoot);
        var bottle = root.Combine("bottles/Games");
        fileSystem.CreateDirectory(bottle.Combine("drive_c"));
        fileSystem.WriteAllText(bottle.Combine("bottle.yml"), "Name: Games\nRunner: soda-9.0-1\nArch: win64\n");
        fileSystem.WriteAllText(bottle.Combine("system.reg"), "WINE REGISTRY Version 2\n");
        fileSystem.WriteAllText(bottle.Combine("user.reg"), "WINE REGISTRY Version 2\n");
        fileSystem.WriteAllText(bottle.Combine("userdef.reg"), "WINE REGISTRY Version 2\n");

        var wine = root.Combine("runners/soda-9.0-1/bin/wine");
        if (runnerInstalled) fileSystem.WriteAllText(wine, "");

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IFileSystem>(fileSystem)
            .BuildServiceProvider();

        var wrapped = new StubLocator(Substitute.For<IGameData>());
        var locator = new WinePrefixWrappingLocator(services, [(_, _, _, _) => wrapped]);

        var result = locator.Locate().Should().ContainSingle().Subject;

        result.WinePrefixPath.Should().Be(bottle);
        result.Compatibility.Should().Be(new CompatibilityLayer(
            CompatibilityLayerKind.PlainWine,
            bottle,
            runnerInstalled ? wine : null,
            "soda-9.0-1"
        ));
    }

    [Fact]
    public void Locate_GogGameInABottle_IsFoundThroughTheBottleRegistry()
    {
        const long productId = 1207658924;
        var fileSystem = new InMemoryFileSystem(OSInformation.FakeUnix);
        var bottle = fileSystem.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine("bottles/bottles/Games");
        fileSystem.CreateDirectory(bottle.Combine("drive_c/GOG Games/Witcher"));
        fileSystem.WriteAllText(bottle.Combine("bottle.yml"), "Runner: soda-9.0-1\n");
        fileSystem.WriteAllText(bottle.Combine("system.reg"),
            "WINE REGISTRY Version 2\n\n" +
            $"[Software\\\\GOG.com\\\\Games\\\\{productId}] 1700000000\n" +
            $"\"gameID\"=\"{productId}\"\n" +
            "\"gameName\"=\"The Witcher\"\n" +
            "\"path\"=\"C:\\\\GOG Games\\\\Witcher\"\n" +
            "\"buildId\"=\"5\"\n");
        fileSystem.WriteAllText(bottle.Combine("user.reg"), "WINE REGISTRY Version 2\n");
        fileSystem.WriteAllText(bottle.Combine("userdef.reg"), "WINE REGISTRY Version 2\n");

        var game = Substitute.For<IGameData>();
        game.StoreIdentifiers.Returns(new StoreIdentifiers(GameId.From(1)) { GOGProductIds = [productId] });
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IFileSystem>(fileSystem)
            .AddSingleton(game)
            .BuildServiceProvider();

        var locator = new WinePrefixWrappingLocator(services, [
            (provider, loggerFactory, overlay, registry) => new GOGLocator(provider.GetServices<IGameData>(), loggerFactory, overlay, registry),
        ]);

        var result = locator.Locate().Should().ContainSingle().Subject;

        result.Path.Should().Be(bottle.Combine("drive_c/GOG Games/Witcher"));
        result.Game.Should().BeSameAs(game);
        result.Store.Should().Be(GameStore.GOG);
        result.StoreIdentifier.Should().Be(productId.ToString());
        result.WinePrefixPath.Should().Be(bottle);
        result.Compatibility.Should().Be(new CompatibilityLayer(CompatibilityLayerKind.PlainWine, bottle, null, "soda-9.0-1"));
    }

    [Fact]
    public void Locate_BottleWithoutRunnerKey_StillReportsPlainWineWithoutABinary()
    {
        var fileSystem = new InMemoryFileSystem(OSInformation.FakeUnix);
        var bottle = fileSystem.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine("bottles/bottles/Games");
        fileSystem.CreateDirectory(bottle.Combine("drive_c"));
        fileSystem.WriteAllText(bottle.Combine("bottle.yml"), "Name: Games\n");
        fileSystem.WriteAllText(bottle.Combine("system.reg"), "WINE REGISTRY Version 2\n");
        fileSystem.WriteAllText(bottle.Combine("user.reg"), "WINE REGISTRY Version 2\n");
        fileSystem.WriteAllText(bottle.Combine("userdef.reg"), "WINE REGISTRY Version 2\n");

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IFileSystem>(fileSystem)
            .BuildServiceProvider();

        var locator = new WinePrefixWrappingLocator(services, [(_, _, _, _) => new StubLocator(Substitute.For<IGameData>())]);

        var result = locator.Locate().Should().ContainSingle().Subject;
        result.Compatibility.Should().Be(new CompatibilityLayer(CompatibilityLayerKind.PlainWine, bottle, null, null));
    }

    private sealed class ThrowingLocator : IGameLocator
    {
        public IEnumerable<GameLocatorResult> Locate() => throw new IOException("No such device");
    }

    private sealed class StubLocator(IGameData game, AbsolutePath? storeRoot = null, string? storeBuildId = null, bool storeUpdatePending = false, DateTimeOffset? storeLastWrite = null, bool storeUpdatesInBackground = false) : IGameLocator
    {
        public IEnumerable<GameLocatorResult> Locate()
        {
            yield return new GameLocatorResult
            {
                Game = game,
                Path = new InMemoryFileSystem(OSInformation.FakeUnix).FromUnsanitizedFullPath("/game"),
                Store = GameStore.GOG,
                StoreIdentifier = "1",
                LocatorIds = ImmutableArray<LocatorId>.Empty,
                Locator = this,
                StoreRoot = storeRoot,
                StoreBuildId = storeBuildId,
                StoreUpdatePending = storeUpdatePending,
                StoreLastWrite = storeLastWrite,
                StoreUpdatesInBackground = storeUpdatesInBackground,
            };
        }
    }
}
