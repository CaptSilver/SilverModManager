using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Backend.Games.Locators;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NSubstitute;

namespace NexusMods.Backend.Tests.Games;

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

        var wrapped = new StubLocator(Substitute.For<IGameData>(), storeRoot, "42", storeUpdatePending: true, storeLastWrite: DateTimeOffset.UnixEpoch.AddDays(5));
        var locator = new WinePrefixWrappingLocator(services, [(_, _, _, _) => wrapped]);

        var result = locator.Locate().Should().ContainSingle().Subject;

        result.StoreRoot.Should().Be(storeRoot);
        result.StoreBuildId.Should().Be("42");
        result.StoreUpdatePending.Should().BeTrue();
        result.StoreLastWrite.Should().Be(DateTimeOffset.UnixEpoch.AddDays(5));
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

    private sealed class ThrowingLocator : IGameLocator
    {
        public IEnumerable<GameLocatorResult> Locate() => throw new IOException("No such device");
    }

    private sealed class StubLocator(IGameData game, AbsolutePath? storeRoot = null, string? storeBuildId = null, bool storeUpdatePending = false, DateTimeOffset? storeLastWrite = null) : IGameLocator
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
            };
        }
    }
}
