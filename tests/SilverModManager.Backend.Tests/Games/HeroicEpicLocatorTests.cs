using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Backend.Games.Locators;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NSubstitute;

namespace NexusMods.Backend.Tests.Games;

public class HeroicEpicLocatorTests
{
    // Fixture shapes follow Legendary's installed.json (a JSON object keyed by app_name, written by
    // legendary/lfs/lgndry.py) and metadata/<app_name>.json (the Epic catalog entry, whose "id" is the catalog
    // item id), as Heroic reads them in src/backend/storeManagers/legendary/library.ts. They are written from
    // those sources, not copied from a real Heroic install; one with an Epic game installed would confirm them.
    private const string AppName = "Fnord";
    private const string CatalogItemId = "5beededaad9743df90e8f07d92df153f";
    private const string FlatpakConfigRoot = ".var/app/com.heroicgameslauncher.hgl/config/heroic";

    [Fact]
    public void Locate_NativeHeroic_MatchesByCatalogItemIdFromMetadata()
    {
        var (fileSystem, home) = NewHome();
        var root = NativeRoot(fileSystem);
        var prefix = home.Combine("Games/Heroic/Prefixes/default/Game");
        var wine = root.Combine("tools/wine/Wine-GE/bin/wine");
        WriteInstall(fileSystem, root, home.Combine("Games/Heroic/Game"), prefix, wine);

        var result = Locate(fileSystem, Game("Game", CatalogItemId));

        result.Store.Should().Be(GameStore.EGS);
        result.Path.Should().Be(home.Combine("Games/Heroic/Game"));
        result.StoreIdentifier.Should().Be(CatalogItemId);
        result.LocatorIds.Should().BeEmpty();
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

        Locate(fileSystem, Game("Game", CatalogItemId)).Compatibility
            .Should().Be(new CompatibilityLayer(CompatibilityLayerKind.HeroicFlatpak, prefix, wine, "Wine-GE"));
    }

    [Fact]
    public void Locate_GameDeclaringTheAppName_MatchesByAppNameWhenMetadataHasADifferentId()
    {
        var (fileSystem, home) = NewHome();
        WriteInstall(fileSystem, NativeRoot(fileSystem), home.Combine("Games/Heroic/Game"), home.Combine("p"), home.Combine("wine"));

        Locate(fileSystem, Game("Game", AppName)).StoreIdentifier.Should().Be(AppName);
    }

    [Fact]
    public void Locate_MetadataMissing_MatchesByAppNameAndLogsIt()
    {
        var (fileSystem, home) = NewHome();
        var root = NativeRoot(fileSystem);
        WriteInstall(fileSystem, root, home.Combine("Games/Heroic/Game"), home.Combine("p"), home.Combine("wine"), writeMetadata: false);
        var logger = new RecordingLogger<HeroicEpicLocator>();

        var results = NewLocator(fileSystem, logger, Game("Game", AppName)).Locate().ToArray();

        results.Should().ContainSingle().Which.StoreIdentifier.Should().Be(AppName);
        logger.Count(LogLevel.Information).Should().BeGreaterThan(0);
    }

    [Fact]
    public void Locate_MetadataMissingAndGameDeclaresOnlyTheCatalogId_FindsNothing()
    {
        var (fileSystem, home) = NewHome();
        WriteInstall(fileSystem, NativeRoot(fileSystem), home.Combine("Games/Heroic/Game"), home.Combine("p"), home.Combine("wine"), writeMetadata: false);

        NewLocator(fileSystem, new RecordingLogger<HeroicEpicLocator>(), Game("Game", CatalogItemId)).Locate().Should().BeEmpty();
    }

    [Fact]
    public void Locate_DlcEntry_IsIgnored()
    {
        var (fileSystem, home) = NewHome();
        WriteInstall(fileSystem, NativeRoot(fileSystem), home.Combine("Games/Heroic/Game"), home.Combine("p"), home.Combine("wine"), isDlc: true);

        NewLocator(fileSystem, new RecordingLogger<HeroicEpicLocator>(), Game("Game", CatalogItemId)).Locate().Should().BeEmpty();
    }

    [Fact]
    public void Locate_NativeLinuxBuild_HasNoCompatibilityLayer()
    {
        var (fileSystem, home) = NewHome();
        WriteInstall(fileSystem, NativeRoot(fileSystem), home.Combine("Games/Heroic/Game"), default, default, platform: "Linux", writeSettings: false);

        var result = Locate(fileSystem, Game("Game", CatalogItemId));

        result.Platform.Should().Be(OSPlatform.Linux);
        result.Compatibility.Should().BeNull();
        result.WinePrefixPath.Should().BeNull();
    }

    [Fact]
    public void Locate_SameAppNameUnderBothRoots_YieldsNothingAndRecordsAProblemNamingBoth()
    {
        var (fileSystem, home) = NewHome();
        var native = NativeRoot(fileSystem);
        var flatpak = home.Combine(FlatpakConfigRoot);
        WriteInstall(fileSystem, native, home.Combine("Games/Native/Game"), home.Combine("p1"), home.Combine("wine"));
        WriteInstall(fileSystem, flatpak, home.Combine("Games/Flatpak/Game"), home.Combine("p2"), home.Combine("wine"));
        var logger = new RecordingLogger<HeroicEpicLocator>();
        var problems = new CollectingProblemSink();

        NewLocator(fileSystem, logger, Game("Game", CatalogItemId)).Locate(problems).Should().BeEmpty();

        problems.Problems.Should().ContainSingle().Which.Message.Should().Contain(AppName)
            .And.Contain(native.GetFullPath()).And.Contain(flatpak.GetFullPath());
        logger.Count(LogLevel.Error).Should().Be(1);
    }

    [Fact]
    public void Locate_UnsupportedGameUnderBothRoots_RecordsNoProblem()
    {
        var (fileSystem, home) = NewHome();
        WriteInstall(fileSystem, NativeRoot(fileSystem), home.Combine("Games/Native/Game"), home.Combine("p1"), home.Combine("wine"));
        WriteInstall(fileSystem, home.Combine(FlatpakConfigRoot), home.Combine("Games/Flatpak/Game"), home.Combine("p2"), home.Combine("wine"));
        var logger = new RecordingLogger<HeroicEpicLocator>();
        var problems = new CollectingProblemSink();

        NewLocator(fileSystem, logger, Game("Other", "some-other-catalog-id")).Locate(problems).Should().BeEmpty();

        problems.Problems.Should().BeEmpty();
        logger.Count(LogLevel.Error).Should().Be(0);
    }

    [Fact]
    public void Locate_UnreadableFlatpakInstalledList_StillLocatesTheNativeGame()
    {
        var (fileSystem, home) = NewHome();
        WriteInstall(fileSystem, NativeRoot(fileSystem), home.Combine("Games/Native/Game"), home.Combine("p"), home.Combine("wine"));
        Write(fileSystem, home.Combine(FlatpakConfigRoot).Combine("legendaryConfig/legendary/installed.json"), "{ not json");
        var problems = new CollectingProblemSink();

        NewLocator(fileSystem, new RecordingLogger<HeroicEpicLocator>(), Game("Game", CatalogItemId)).Locate(problems).Should().ContainSingle();

        problems.Problems.Should().BeEmpty();
    }

    [Fact]
    public void Locate_NoHeroicInstall_FindsNothing()
    {
        var (fileSystem, _) = NewHome();

        NewLocator(fileSystem, new RecordingLogger<HeroicEpicLocator>(), Game("Game", CatalogItemId)).Locate().Should().BeEmpty();
    }

    [Fact]
    public void Locate_TwoGamesClaimingOneCatalogId_MatchesTheFirstAndRecordsAProblem()
    {
        var (fileSystem, home) = NewHome();
        WriteInstall(fileSystem, NativeRoot(fileSystem), home.Combine("Games/Heroic/Game"), home.Combine("p"), home.Combine("wine"));
        var first = Game("First", CatalogItemId);
        var second = Game("Second", CatalogItemId);
        var problems = new CollectingProblemSink();

        NewLocator(fileSystem, new RecordingLogger<HeroicEpicLocator>(), first, second).Locate(problems)
            .Should().ContainSingle().Which.Game.Should().BeSameAs(first);
        problems.Problems.Should().ContainSingle().Which.Message.Should().Contain("First").And.Contain("Second");
    }

    private static IGameData Game(string name, string id)
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns(name);
        game.StoreIdentifiers.Returns(new StoreIdentifiers(GameId.From(1)) { EGSCatalogItemId = [id] });
        return game;
    }

    private static HeroicEpicLocator NewLocator(InMemoryFileSystem fileSystem, ILogger<HeroicEpicLocator> logger, params IGameData[] games)
        => new(games, new SingleLoggerFactory(logger), fileSystem);

    private static GameLocatorResult Locate(InMemoryFileSystem fileSystem, IGameData game)
        => NewLocator(fileSystem, new RecordingLogger<HeroicEpicLocator>(), game).Locate().Should().ContainSingle().Subject;

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

    private static void WriteInstall(InMemoryFileSystem fileSystem, AbsolutePath root, AbsolutePath installPath, AbsolutePath prefix, AbsolutePath wine,
        string platform = "Windows", bool isDlc = false, bool writeMetadata = true, bool writeSettings = true)
    {
        Write(fileSystem, installPath.Combine("game.exe"), "");
        var legendary = root.Combine("legendaryConfig/legendary");
        Write(fileSystem, legendary.Combine("installed.json"), $$"""
            {
              "{{AppName}}": {
                "app_name": "{{AppName}}",
                "install_path": "{{installPath.GetFullPath()}}",
                "title": "Game",
                "version": "1.2.3",
                "executable": "game.exe",
                "platform": "{{platform}}",
                "is_dlc": {{(isDlc ? "true" : "false")}},
                "can_run_offline": true,
                "install_size": 1024
              }
            }
            """);

        if (writeMetadata)
        {
            Write(fileSystem, legendary.Combine($"metadata/{AppName}.json"), $$"""
                {
                  "app_name": "{{AppName}}",
                  "app_title": "Game",
                  "metadata": { "id": "{{CatalogItemId}}", "title": "Game", "namespace": "ns" }
                }
                """);
        }

        if (!writeSettings) return;
        Write(fileSystem, root.Combine($"GamesConfig/{AppName}.json"), $$"""
            {
              "{{AppName}}": {
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
