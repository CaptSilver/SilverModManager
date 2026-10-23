using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Backend.Games.Locators;
using NexusMods.Paths;

namespace SilverModManager.Backend.Tests.Games;

public class HeroicConfigTests
{
    private static (InMemoryFileSystem FileSystem, AbsolutePath Home) NewHome()
    {
        var fileSystem = new InMemoryFileSystem(OSInformation.FakeUnix);
        return (fileSystem, fileSystem.GetKnownPath(KnownPath.HomeDirectory));
    }

    private static void Write(IFileSystem fileSystem, AbsolutePath path, string contents)
    {
        fileSystem.CreateDirectory(path.Parent);
        fileSystem.WriteAllText(path, contents);
    }

    [Fact]
    public void Roots_ListTheNativeInstallBeforeTheFlatpakOne()
    {
        var (fileSystem, home) = NewHome();

        var roots = HeroicConfigRoots.All(fileSystem);

        roots.Select(r => r.Path).Should().Equal(
            fileSystem.GetKnownPath(KnownPath.XDG_CONFIG_HOME).Combine("heroic"),
            home.Combine(".var/app/com.heroicgameslauncher.hgl/config/heroic"));
        roots.Select(r => r.IsFlatpak).Should().Equal(false, true);
    }

    [Fact]
    public void GameSettings_ReadsPrefixAndRunner()
    {
        var (fileSystem, _) = NewHome();
        var root = HeroicConfigRoots.All(fileSystem)[0];
        Write(fileSystem, root.Path.Combine("GamesConfig/42.json"), """
            { "42": { "winePrefix": "/games/prefix", "wineVersion": { "bin": "/tools/wine", "name": "Wine-GE", "type": "wine" } } }
            """);

        var settings = HeroicGameSettings.Read(root, "42", NullLogger.Instance);

        settings.Should().NotBeNull();
        settings!.WinePrefix.Should().Be("/games/prefix");
        settings.WineBin.Should().Be("/tools/wine");
        settings.WineName.Should().Be("Wine-GE");
        settings.WineType.Should().Be("wine");
    }

    [Fact]
    public void GameSettings_NoFile_IsNull()
    {
        var (fileSystem, _) = NewHome();

        HeroicGameSettings.Read(HeroicConfigRoots.All(fileSystem)[0], "42", NullLogger.Instance).Should().BeNull();
    }

    [Fact]
    public void GameSettings_UnreadableFile_IsNull()
    {
        var (fileSystem, _) = NewHome();
        var root = HeroicConfigRoots.All(fileSystem)[0];
        Write(fileSystem, root.Path.Combine("GamesConfig/42.json"), "{ not json");
        var logger = new RecordingLogger<HeroicConfigTests>();

        HeroicGameSettings.Read(root, "42", logger).Should().BeNull();
        logger.Count(LogLevel.Warning).Should().Be(1);
    }

    [Fact]
    public void InstalledGogGames_ListsAppNamesWithInstallPaths()
    {
        var (fileSystem, _) = NewHome();
        var root = HeroicConfigRoots.All(fileSystem)[0];
        Write(fileSystem, root.Path.Combine("gog_store/installed.json"), """
            { "installed": [ { "appName": "1", "install_path": "/a" }, { "appName": "2", "install_path": "/b" } ] }
            """);

        root.InstalledGogGames(NullLogger.Instance).Should().Equal(new KeyValuePair<string, string>("1", "/a"), new KeyValuePair<string, string>("2", "/b"));
    }

    [Fact]
    public void InstalledGogGames_NoFile_IsEmpty()
    {
        var (fileSystem, _) = NewHome();

        HeroicConfigRoots.All(fileSystem)[0].InstalledGogGames(NullLogger.Instance).Should().BeEmpty();
    }

    [Fact]
    public void InstalledGogGames_UnreadableFile_IsEmptyAndWarns()
    {
        var (fileSystem, _) = NewHome();
        var root = HeroicConfigRoots.All(fileSystem)[0];
        Write(fileSystem, root.Path.Combine("gog_store/installed.json"), "{ not json");
        var logger = new RecordingLogger<HeroicConfigTests>();

        root.InstalledGogGames(logger).Should().BeEmpty();
        logger.Count(LogLevel.Warning).Should().Be(1);
    }
}
