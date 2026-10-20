using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Backend.Games.Locators;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NSubstitute;

namespace NexusMods.Backend.Tests.Games;

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
