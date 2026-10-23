using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Backend.Games.Locators;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using NSubstitute;

namespace SilverModManager.Backend.Tests.Games;

public class HeroicAmazonLocatorTests
{
    // Fixture shapes follow Nile's installed.json, a JSON array of {id, path, version, size} under
    // nile_config/nile/, as Heroic reads it in src/backend/storeManagers/nile, and Heroic's GamesConfig file
    // keyed by the product id. They are written from Heroic's and Nile's sources and have not been checked
    // against a Heroic logged in to Amazon; listing nile_config/nile/ and GamesConfig/ in one would.
    private const string ProductId = "amzn1.adg.product.00000000-0000-0000-0000-000000000001";
    private const string FlatpakConfigRoot = ".var/app/com.heroicgameslauncher.hgl/config/heroic";

    [Fact]
    public void Locate_NativeHeroic_FindsTheInstalledProduct()
    {
        var (fileSystem, home) = NewHome();
        var root = NativeRoot(fileSystem);
        var prefix = home.Combine("Games/Heroic/Prefixes/default/Game");
        var wine = root.Combine("tools/wine/Wine-GE/bin/wine");
        WriteInstall(fileSystem, root, home.Combine("Games/Heroic/Game"), prefix, wine);

        var result = Locate(fileSystem, Game("Game", ProductId));

        result.Store.Should().Be(GameStore.Amazon);
        result.Path.Should().Be(home.Combine("Games/Heroic/Game"));
        result.StoreIdentifier.Should().Be(ProductId);
        result.LocatorIds.Select(id => id.Value).Should().Equal("1.2.3");
        result.Platform.Should().Be(OSPlatform.Windows);
        result.WinePrefixPath.Should().Be(prefix);
        result.Compatibility.Should().Be(new CompatibilityLayer(CompatibilityLayerKind.Heroic, prefix, wine, "Wine-GE"));
    }

    [Fact]
    public void Locate_FlatpakHeroic_RecordsTheFlatpakKind()
    {
        var (fileSystem, home) = NewHome();
        var root = home.Combine(FlatpakConfigRoot);
        var prefix = home.Combine("Games/Heroic/Prefixes/default/Game");
        var wine = root.Combine("tools/wine/Wine-GE/bin/wine");
        WriteInstall(fileSystem, root, home.Combine("Games/Heroic/Game"), prefix, wine);

        Locate(fileSystem, Game("Game", ProductId)).Compatibility
            .Should().Be(new CompatibilityLayer(CompatibilityLayerKind.HeroicFlatpak, prefix, wine, "Wine-GE"));
    }

    [Fact]
    public void Locate_NoGamesConfig_StillLocatesWithoutACompatibilityLayer()
    {
        var (fileSystem, home) = NewHome();
        WriteInstall(fileSystem, NativeRoot(fileSystem), home.Combine("Games/Heroic/Game"), default, default, writeSettings: false);

        var result = Locate(fileSystem, Game("Game", ProductId));

        result.Compatibility.Should().BeNull();
        result.WinePrefixPath.Should().BeNull();
    }

    [Fact]
    public void Locate_ProductNoGameClaims_IsIgnored()
    {
        var (fileSystem, home) = NewHome();
        WriteInstall(fileSystem, NativeRoot(fileSystem), home.Combine("Games/Heroic/Game"), home.Combine("p"), home.Combine("wine"));

        NewLocator(fileSystem, new RecordingLogger<HeroicAmazonLocator>(), Game("Other", "amzn1.adg.product.other")).Locate().Should().BeEmpty();
    }

    [Fact]
    public void Locate_SameProductUnderBothRoots_YieldsNothingAndRecordsAProblemNamingBoth()
    {
        var (fileSystem, home) = NewHome();
        var native = NativeRoot(fileSystem);
        var flatpak = home.Combine(FlatpakConfigRoot);
        WriteInstall(fileSystem, native, home.Combine("Games/Native/Game"), home.Combine("p1"), home.Combine("wine"));
        WriteInstall(fileSystem, flatpak, home.Combine("Games/Flatpak/Game"), home.Combine("p2"), home.Combine("wine"));
        var logger = new RecordingLogger<HeroicAmazonLocator>();
        var problems = new CollectingProblemSink();

        NewLocator(fileSystem, logger, Game("Game", ProductId)).Locate(problems).Should().BeEmpty();

        problems.Problems.Should().ContainSingle().Which.Message.Should().Contain(ProductId)
            .And.Contain(native.GetFullPath()).And.Contain(flatpak.GetFullPath());
        logger.Count(LogLevel.Error).Should().Be(1);
    }

    [Fact]
    public void Locate_UnsupportedProductUnderBothRoots_RecordsNoProblem()
    {
        var (fileSystem, home) = NewHome();
        WriteInstall(fileSystem, NativeRoot(fileSystem), home.Combine("Games/Native/Game"), home.Combine("p1"), home.Combine("wine"));
        WriteInstall(fileSystem, home.Combine(FlatpakConfigRoot), home.Combine("Games/Flatpak/Game"), home.Combine("p2"), home.Combine("wine"));
        var logger = new RecordingLogger<HeroicAmazonLocator>();
        var problems = new CollectingProblemSink();

        NewLocator(fileSystem, logger, Game("Other", "amzn1.adg.product.other")).Locate(problems).Should().BeEmpty();

        problems.Problems.Should().BeEmpty();
        logger.Count(LogLevel.Error).Should().Be(0);
    }

    [Fact]
    public void Locate_UnreadableFlatpakInstalledList_StillLocatesTheNativeGame()
    {
        var (fileSystem, home) = NewHome();
        WriteInstall(fileSystem, NativeRoot(fileSystem), home.Combine("Games/Native/Game"), home.Combine("p"), home.Combine("wine"));
        Write(fileSystem, home.Combine(FlatpakConfigRoot).Combine("nile_config/nile/installed.json"), "[ not json");
        var problems = new CollectingProblemSink();

        NewLocator(fileSystem, new RecordingLogger<HeroicAmazonLocator>(), Game("Game", ProductId)).Locate(problems).Should().ContainSingle();

        problems.Problems.Should().BeEmpty();
    }

    [Fact]
    public void Locate_EntriesMissingAnIdOrPath_AreSkipped()
    {
        var (fileSystem, home) = NewHome();
        var root = NativeRoot(fileSystem);
        Write(fileSystem, root.Combine("nile_config/nile/installed.json"), $$"""
            [
              { "path": "{{home.Combine("a").GetFullPath()}}", "version": "1" },
              { "id": "{{ProductId}}", "version": "1" },
              "junk"
            ]
            """);

        NewLocator(fileSystem, new RecordingLogger<HeroicAmazonLocator>(), Game("Game", ProductId)).Locate().Should().BeEmpty();
    }

    [Fact]
    public void Locate_EntryWithoutAVersion_HasNoLocatorIds()
    {
        var (fileSystem, home) = NewHome();
        var installPath = home.Combine("Games/Heroic/Game");
        Write(fileSystem, NativeRoot(fileSystem).Combine("nile_config/nile/installed.json"), $$"""
            [
              { "id": "{{ProductId}}", "path": "{{installPath.GetFullPath()}}", "size": 1024 }
            ]
            """);

        var result = Locate(fileSystem, Game("Game", ProductId));

        result.Path.Should().Be(installPath);
        result.LocatorIds.Should().BeEmpty();
    }

    [Fact]
    public void Locate_NoHeroicInstall_FindsNothing()
    {
        var (fileSystem, _) = NewHome();

        NewLocator(fileSystem, new RecordingLogger<HeroicAmazonLocator>(), Game("Game", ProductId)).Locate().Should().BeEmpty();
    }

    [Fact]
    public void Locate_TwoGamesClaimingOneProduct_MatchesTheFirstAndRecordsAProblem()
    {
        var (fileSystem, home) = NewHome();
        WriteInstall(fileSystem, NativeRoot(fileSystem), home.Combine("Games/Heroic/Game"), home.Combine("p"), home.Combine("wine"));
        var first = Game("First", ProductId);
        var second = Game("Second", ProductId);
        var problems = new CollectingProblemSink();

        NewLocator(fileSystem, new RecordingLogger<HeroicAmazonLocator>(), first, second).Locate(problems)
            .Should().ContainSingle().Which.Game.Should().BeSameAs(first);
        problems.Problems.Should().ContainSingle().Which.Message.Should().Contain("First").And.Contain("Second");
    }

    private static IGameData Game(string name, string id)
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns(name);
        game.StoreIdentifiers.Returns(new StoreIdentifiers(GameId.From(1)) { AmazonProductIds = [id] });
        return game;
    }

    private static HeroicAmazonLocator NewLocator(InMemoryFileSystem fileSystem, ILogger<HeroicAmazonLocator> logger, params IGameData[] games)
        => new(games, new SingleLoggerFactory(logger), fileSystem);

    private static GameLocatorResult Locate(InMemoryFileSystem fileSystem, IGameData game)
        => NewLocator(fileSystem, new RecordingLogger<HeroicAmazonLocator>(), game).Locate().Should().ContainSingle().Subject;

    private sealed class SingleLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => logger;
        public void Dispose() { }
    }

    private static (InMemoryFileSystem FileSystem, AbsolutePath Home) NewHome()
    {
        var fileSystem = new InMemoryFileSystem(OSInformation.FakeUnix);
        return (fileSystem, fileSystem.GetKnownPath(KnownPath.HomeDirectory));
    }

    private static AbsolutePath NativeRoot(InMemoryFileSystem fileSystem) => fileSystem.GetKnownPath(KnownPath.XDG_CONFIG_HOME).Combine("heroic");

    private static void WriteInstall(InMemoryFileSystem fileSystem, AbsolutePath root, AbsolutePath installPath, AbsolutePath prefix, AbsolutePath wine, bool writeSettings = true)
    {
        Write(fileSystem, installPath.Combine("game.exe"), "");
        Write(fileSystem, root.Combine("nile_config/nile/installed.json"), $$"""
            [
              { "id": "{{ProductId}}", "path": "{{installPath.GetFullPath()}}", "version": "1.2.3", "size": 1024 }
            ]
            """);

        if (!writeSettings) return;
        Write(fileSystem, root.Combine($"GamesConfig/{ProductId}.json"), $$"""
            {
              "{{ProductId}}": {
                "winePrefix": "{{prefix.GetFullPath()}}",
                "wineVersion": { "bin": "{{wine.GetFullPath()}}", "name": "Wine-GE", "type": "wine" }
              }
            }
            """);
    }

    private static void Write(IFileSystem fileSystem, AbsolutePath path, string contents)
    {
        fileSystem.CreateDirectory(path.Parent);
        fileSystem.WriteAllText(path, contents);
    }
}
