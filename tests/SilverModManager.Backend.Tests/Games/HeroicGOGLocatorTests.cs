using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Backend.Games.Locators;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using NSubstitute;

namespace SilverModManager.Backend.Tests.Games;

public class HeroicGOGLocatorTests
{
    private const long ProductId = 1207658930;
    private const string FlatpakConfigRoot = ".var/app/com.heroicgameslauncher.hgl/config/heroic";

    [Fact]
    public void Locate_NativeHeroic_RecordsPrefixAndRunner()
    {
        var (fileSystem, home) = NewHome();
        var prefix = home.Combine("Games/Heroic/Prefixes/default/Game");
        var wine = fileSystem.GetKnownPath(KnownPath.XDG_CONFIG_HOME).Combine("heroic/tools/wine/Wine-GE/bin/wine");
        WriteHeroicConfig(fileSystem, fileSystem.GetKnownPath(KnownPath.XDG_CONFIG_HOME).Combine("heroic"), home.Combine("Games/Heroic/Game"), prefix, wine);

        var result = Locate(fileSystem);

        result.Compatibility.Should().Be(new CompatibilityLayer(CompatibilityLayerKind.Heroic, prefix, wine, "Wine-GE"));
        result.Store.Should().Be(GameStore.GOG);
        result.Platform.Should().Be(OSPlatform.Windows);
        result.TargetOS.IsWindows.Should().BeTrue();
    }

    [Fact]
    public void Locate_LeavesStoreRootUnset()
    {
        var (fileSystem, home) = NewHome();
        var prefix = home.Combine("Games/Heroic/Prefixes/default/Game");
        var wine = fileSystem.GetKnownPath(KnownPath.XDG_CONFIG_HOME).Combine("heroic/tools/wine/Wine-GE/bin/wine");
        WriteHeroicConfig(fileSystem, fileSystem.GetKnownPath(KnownPath.XDG_CONFIG_HOME).Combine("heroic"), home.Combine("Games/Heroic/Game"), prefix, wine);

        var result = Locate(fileSystem);

        result.StoreRoot.Should().BeNull();
        result.StoreBuildId.Should().BeNull();
        result.StoreUpdatePending.Should().BeFalse();
    }

    [Fact]
    public void Locate_NativeLinuxBuild_ReportsLinuxWithoutCompatibilityLayer()
    {
        var (fileSystem, home) = NewHome();
        var configRoot = fileSystem.GetKnownPath(KnownPath.XDG_CONFIG_HOME).Combine("heroic");
        WriteHeroicConfig(fileSystem, configRoot, home.Combine("Games/Heroic/Game"), default, default, platform: "linux", writeWineSettings: false);

        var result = Locate(fileSystem);

        result.Platform.Should().Be(OSPlatform.Linux);
        result.TargetOS.IsLinux.Should().BeTrue();
        result.Compatibility.Should().BeNull();
    }

    [Fact]
    public void Locate_FlatpakHeroic_RecordsTheFlatpakKind()
    {
        var (fileSystem, home) = NewHome();
        var prefix = home.Combine("Games/Heroic/Prefixes/default/Game");
        var wine = home.Combine(FlatpakConfigRoot).Combine("tools/wine/Wine-GE/bin/wine");
        WriteHeroicConfig(fileSystem, home.Combine(FlatpakConfigRoot), home.Combine("Games/Heroic/Game"), prefix, wine);

        var result = Locate(fileSystem);

        result.Compatibility.Should().Be(new CompatibilityLayer(CompatibilityLayerKind.HeroicFlatpak, prefix, wine, "Wine-GE"));
    }

    [Fact]
    public void Locate_WindowsGame_RecordsTheWinePrefixFromHeroicsSettings()
    {
        var (fileSystem, home) = NewHome();
        var prefix = home.Combine("Games/Heroic/Prefixes/default/Game");
        var wine = fileSystem.GetKnownPath(KnownPath.XDG_CONFIG_HOME).Combine("heroic/tools/wine/Wine-GE/bin/wine");
        WriteHeroicConfig(fileSystem, fileSystem.GetKnownPath(KnownPath.XDG_CONFIG_HOME).Combine("heroic"), home.Combine("Games/Heroic/Game"), prefix, wine);

        var result = Locate(fileSystem);

        result.Platform.Should().Be(OSPlatform.Windows);
        result.WinePrefixPath.Should().Be(prefix);
    }

    [Fact]
    public void Locate_NativeLinuxBuild_HasNoWinePrefix()
    {
        var (fileSystem, home) = NewHome();
        var configRoot = fileSystem.GetKnownPath(KnownPath.XDG_CONFIG_HOME).Combine("heroic");
        WriteHeroicConfig(fileSystem, configRoot, home.Combine("Games/Heroic/Game"), default, default, platform: "linux", writeWineSettings: false);

        Locate(fileSystem).WinePrefixPath.Should().BeNull();
    }

    [Fact]
    public void Locate_SameGameUnderBothRoots_YieldsNothingAndRecordsAProblemNamingBoth()
    {
        var (fileSystem, home) = NewHome();
        var nativeRoot = fileSystem.GetKnownPath(KnownPath.XDG_CONFIG_HOME).Combine("heroic");
        var flatpakRoot = home.Combine(FlatpakConfigRoot);
        var wine = nativeRoot.Combine("tools/wine/Wine-GE/bin/wine");
        WriteHeroicConfig(fileSystem, nativeRoot, home.Combine("Games/Native/Game"), home.Combine("Prefixes/native"), wine);
        WriteHeroicConfig(fileSystem, flatpakRoot, home.Combine("Games/Flatpak/Game"), home.Combine("Prefixes/flatpak"), wine);

        var logger = new RecordingLogger<HeroicGOGLocator>();
        var problems = new CollectingProblemSink();
        var locator = NewLocator(fileSystem, logger);

        locator.Locate(problems).Should().BeEmpty();

        var problem = problems.Problems.Should().ContainSingle().Subject;
        problem.Message.Should().Contain(ProductId.ToString())
            .And.Contain(nativeRoot.GetFullPath())
            .And.Contain(flatpakRoot.GetFullPath());
        logger.Count(LogLevel.Error).Should().Be(1);
    }

    [Fact]
    public void Locate_GameUnderOnlyTheFlatpakRoot_RecordsNoProblem()
    {
        var (fileSystem, home) = NewHome();
        var prefix = home.Combine("Games/Heroic/Prefixes/default/Game");
        var wine = home.Combine(FlatpakConfigRoot).Combine("tools/wine/Wine-GE/bin/wine");
        WriteHeroicConfig(fileSystem, home.Combine(FlatpakConfigRoot), home.Combine("Games/Heroic/Game"), prefix, wine);

        var problems = new CollectingProblemSink();
        NewLocator(fileSystem, new RecordingLogger<HeroicGOGLocator>()).Locate(problems).Should().ContainSingle();

        problems.Problems.Should().BeEmpty();
    }

    [Fact]
    public void Locate_UnreadableFlatpakInstalledList_StillLocatesTheNativeGame()
    {
        var (fileSystem, home) = NewHome();
        var nativeRoot = fileSystem.GetKnownPath(KnownPath.XDG_CONFIG_HOME).Combine("heroic");
        var prefix = home.Combine("Prefixes/native");
        var wine = nativeRoot.Combine("tools/wine/Wine-GE/bin/wine");
        WriteHeroicConfig(fileSystem, nativeRoot, home.Combine("Games/Native/Game"), prefix, wine);
        Write(fileSystem, home.Combine(FlatpakConfigRoot).Combine("gog_store/installed.json"), "{ not json");

        var problems = new CollectingProblemSink();
        var result = NewLocator(fileSystem, new RecordingLogger<HeroicGOGLocator>()).Locate(problems).Should().ContainSingle().Subject;

        result.Compatibility.Should().Be(new CompatibilityLayer(CompatibilityLayerKind.Heroic, prefix, wine, "Wine-GE"));
        problems.Problems.Should().BeEmpty();
    }

    [Fact]
    public void Locate_TwoGamesWithTheSameGogId_MatchesTheFirstAndRecordsAProblem()
    {
        var (fileSystem, home) = NewHome();
        var nativeRoot = fileSystem.GetKnownPath(KnownPath.XDG_CONFIG_HOME).Combine("heroic");
        WriteHeroicConfig(fileSystem, nativeRoot, home.Combine("Games/Heroic/Game"), home.Combine("Prefixes/p"), nativeRoot.Combine("wine"));

        var first = GogGame("First");
        var second = GogGame("Second");
        var problems = new CollectingProblemSink();
        var locator = new HeroicGOGLocator([first, second], NullLoggerFactory.Instance, fileSystem);

        locator.Locate(problems).Should().ContainSingle().Which.Game.Should().BeSameAs(first);
        problems.Problems.Should().ContainSingle().Which.Message.Should().Contain("First").And.Contain("Second");
    }

    private static IGameData GogGame(string name)
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns(name);
        game.StoreIdentifiers.Returns(new StoreIdentifiers(GameId.From(1)) { GOGProductIds = [ProductId] });
        return game;
    }

    private static HeroicGOGLocator NewLocator(InMemoryFileSystem fileSystem, Microsoft.Extensions.Logging.ILogger<HeroicGOGLocator> logger)
        => new([GogGame("Game")], new SingleLoggerFactory(logger), fileSystem);

    private sealed class SingleLoggerFactory(Microsoft.Extensions.Logging.ILogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => logger;
        public void Dispose() { }
    }

    private static GameLocatorResult Locate(InMemoryFileSystem fileSystem)
    {
        var game = Substitute.For<IGameData>();
        game.StoreIdentifiers.Returns(new StoreIdentifiers(GameId.From(1)) { GOGProductIds = [ProductId] });

        var locator = new HeroicGOGLocator([game], NullLoggerFactory.Instance, fileSystem);
        return locator.Locate().Should().ContainSingle().Subject;
    }

    private static (InMemoryFileSystem FileSystem, AbsolutePath Home) NewHome()
    {
        var fileSystem = new InMemoryFileSystem(OSInformation.FakeUnix);
        return (fileSystem, fileSystem.GetKnownPath(KnownPath.HomeDirectory));
    }

    private static void WriteHeroicConfig(InMemoryFileSystem fileSystem, AbsolutePath configRoot, AbsolutePath installPath, AbsolutePath prefix, AbsolutePath wine, string platform = "windows", bool writeWineSettings = true)
    {
        Write(fileSystem, installPath.Combine("game.exe"), "");
        Write(fileSystem, configRoot.Combine("gog_store/installed.json"), $$"""
            {
              "installed": [
                {
                  "platform": "{{platform}}",
                  "executable": "",
                  "install_path": "{{installPath.GetFullPath()}}",
                  "install_size": "1 GB",
                  "is_dlc": false,
                  "version": "1.0",
                  "appName": "{{ProductId}}",
                  "installedDLCs": [],
                  "language": "en-US",
                  "versionEtag": "",
                  "buildId": "55555",
                  "pinnedVersion": false
                }
              ]
            }
            """);
        if (!writeWineSettings) return;
        Write(fileSystem, configRoot.Combine($"GamesConfig/{ProductId}.json"), $$"""
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
