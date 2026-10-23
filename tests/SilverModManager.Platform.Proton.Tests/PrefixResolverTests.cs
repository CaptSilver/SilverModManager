using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FluentAssertions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using NSubstitute;

namespace SilverModManager.Platform.Proton.Tests;

public class PrefixResolverTests
{
    private readonly InMemoryFileSystem _fs = new(OSInformation.FakeUnix);
    private readonly AbsolutePath _steamPrefix;
    private readonly AbsolutePath _heroicPrefix;
    private readonly AbsolutePath _winePrefix;

    public PrefixResolverTests()
    {
        _steamPrefix = _fs.FromUnsanitizedFullPath("/steam/steamapps/compatdata/42/pfx");
        _heroicPrefix = _fs.FromUnsanitizedFullPath("/games/heroic/prefix");
        _winePrefix = _fs.FromUnsanitizedFullPath("/home/user/.wine");
    }

    private static IPrefixResolver Resolver => new PrefixResolver();

    private void MakeReady(AbsolutePath prefix)
    {
        prefix.CreateDirectory();
        using var stream = _fs.CreateFile(prefix.Combine("system.reg"));
    }

    private GameInstallation Installation(
        GameStore? store = null,
        OSPlatform? platform = null,
        AbsolutePath? winePrefixPath = null,
        bool steamProvider = false,
        CompatibilityLayer? compatibility = null)
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Test Game");
        var gameFolder = _fs.FromUnsanitizedFullPath("/steam/steamapps/common/Test Game");

        var provider = Substitute.For<ILinuxCompatabilityDataProvider>();
        provider.WinePrefixDirectoryPath.Returns(_steamPrefix);

        var result = new GameLocatorResult
        {
            Game = game,
            Path = gameFolder,
            Store = store ?? GameStore.Steam,
            StoreIdentifier = "42",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = platform ?? OSPlatform.Windows,
            WinePrefixPath = winePrefixPath,
            LinuxCompatabilityDataProvider = steamProvider ? provider : null,
            Compatibility = compatibility,
        };

        var locations = GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, gameFolder));
        return new GameInstallation(result, locations);
    }

    [Fact]
    public void ReadySteamPrefix_ResolvesToSteamProtonWithTheAppId()
    {
        MakeReady(_steamPrefix);
        var installation = Installation(winePrefixPath: _steamPrefix, steamProvider: true,
            compatibility: new CompatibilityLayer(CompatibilityLayerKind.SteamProton, _steamPrefix));

        var resolution = Resolver.Resolve(installation);

        resolution.IsResolved.Should().BeTrue();
        resolution.Prefix.Should().Be(new ResolvedPrefix(_steamPrefix, CompatibilityLayerKind.SteamProton, ProtonAppId: 42));
    }

    [Fact]
    public void SteamPrefixThatWasNeverCreated_FailsWithTheMissingPrefixSentence()
    {
        var installation = Installation(winePrefixPath: _steamPrefix,
            compatibility: new CompatibilityLayer(CompatibilityLayerKind.SteamProton, _steamPrefix));

        var resolution = Resolver.Resolve(installation);

        resolution.IsFailed.Should().BeTrue();
        resolution.Diagnostic.Should().Be(ProtonMessages.MissingPrefix("Test Game", _steamPrefix));
    }

    [Fact]
    public void SteamPrefixWithoutAnyCompatibilityRecord_StillResolvesFromTheWinePrefixPath()
    {
        MakeReady(_steamPrefix);

        var resolution = Resolver.Resolve(Installation(winePrefixPath: _steamPrefix));

        resolution.Prefix.Should().Be(new ResolvedPrefix(_steamPrefix, CompatibilityLayerKind.SteamProton, ProtonAppId: 42));
    }

    [Theory]
    [InlineData(CompatibilityLayerKind.Heroic)]
    [InlineData(CompatibilityLayerKind.HeroicFlatpak)]
    public void HeroicPrefix_ResolvesWithItsWineBinary(CompatibilityLayerKind kind)
    {
        MakeReady(_heroicPrefix);
        var wine = _fs.FromUnsanitizedFullPath("/runners/wine-ge/bin/wine");
        var installation = Installation(store: GameStore.GOG,
            compatibility: new CompatibilityLayer(kind, _heroicPrefix, wine, "Wine-GE"));

        var resolution = Resolver.Resolve(installation);

        resolution.Prefix.Should().Be(new ResolvedPrefix(_heroicPrefix, kind, wine));
    }

    [Fact]
    public void GogGameFoundInsideAWinePrefix_ResolvesToPlainWineEvenThoughItsStoreIsGog()
    {
        MakeReady(_winePrefix);
        var installation = Installation(store: GameStore.GOG,
            compatibility: new CompatibilityLayer(CompatibilityLayerKind.PlainWine, _winePrefix));

        var resolution = Resolver.Resolve(installation);

        resolution.Prefix.Should().Be(new ResolvedPrefix(_winePrefix, CompatibilityLayerKind.PlainWine));
    }

    [Fact]
    public void NonSteamPrefixThatIsNotReady_FailsAndNamesThePrefix()
    {
        var installation = Installation(store: GameStore.GOG,
            compatibility: new CompatibilityLayer(CompatibilityLayerKind.Heroic, _heroicPrefix));

        var resolution = Resolver.Resolve(installation);

        resolution.IsFailed.Should().BeTrue();
        resolution.Diagnostic.Should().Contain("Test Game").And.Contain(_heroicPrefix.GetFullPath());
    }

    [Fact]
    public void SteamPrefixAndADifferentRecordedLayer_IsAmbiguousAndNamesBoth()
    {
        MakeReady(_steamPrefix);
        MakeReady(_winePrefix);
        var installation = Installation(winePrefixPath: _steamPrefix, steamProvider: true,
            compatibility: new CompatibilityLayer(CompatibilityLayerKind.PlainWine, _winePrefix));

        var resolution = Resolver.Resolve(installation);

        resolution.IsFailed.Should().BeTrue();
        resolution.Diagnostic.Should().Contain(_steamPrefix.GetFullPath()).And.Contain(_winePrefix.GetFullPath());
    }

    [Fact]
    public void NativeLinuxBuild_NeedsNoPrefix()
    {
        var resolution = Resolver.Resolve(Installation(platform: OSPlatform.Linux));

        resolution.IsNative.Should().BeTrue();
        resolution.IsResolved.Should().BeFalse();
        resolution.IsFailed.Should().BeFalse();
    }

    [Fact]
    public void ManuallyAddedWindowsGameWithNoRecordedPrefix_FailsSayingHowToAddOne()
    {
        var resolution = Resolver.Resolve(Installation(store: GameStore.ManuallyAdded));

        resolution.IsFailed.Should().BeTrue();
        resolution.Diagnostic.Should().Contain("Test Game").And.Contain("no Wine prefix is recorded").And.Contain("installs.yaml");
    }

    [Fact]
    public void ManuallyAddedGameWithARecordedPlainWineLayer_ResolvesToIt()
    {
        MakeReady(_winePrefix);
        var installation = Installation(store: GameStore.ManuallyAdded, winePrefixPath: _winePrefix,
            compatibility: new CompatibilityLayer(CompatibilityLayerKind.PlainWine, _winePrefix));

        var resolution = Resolver.Resolve(installation);

        resolution.IsResolved.Should().BeTrue();
        resolution.Prefix.Should().Be(new ResolvedPrefix(_winePrefix, CompatibilityLayerKind.PlainWine));
    }

    [Fact]
    public void WindowsGameWithNoRecordedPrefix_Fails()
    {
        var resolution = Resolver.Resolve(Installation(store: GameStore.GOG));

        resolution.IsFailed.Should().BeTrue();
        resolution.Diagnostic.Should().Contain("Test Game");
    }
}
