using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Backend.Games.Locators;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NSubstitute;

namespace NexusMods.Backend.Tests.Games;

public class SteamLocatorTests
{
    private const uint AppId = 1091500;

    [Fact]
    public void Locate_ProtonGame_ExposesLinuxCompatabilityDataProvider()
    {
        var result = LocateSingle(withProtonPrefix: true);

        result.Platform.Should().Be(OSPlatform.Windows, "Proton runs the Windows build of the game");
        result.LinuxCompatabilityDataProvider.Should().NotBeNull();
        result.LinuxCompatabilityDataProvider!.WinePrefixDirectoryPath.FileName.Should().Be("pfx");
    }

    [Fact]
    public void Locate_GameWithoutProtonPrefix_IsNativeLinux()
    {
        var result = LocateSingle(withProtonPrefix: false);

        result.Platform.Should().Be(OSPlatform.Linux, "without a Proton prefix the game is a native Linux build");
        result.LinuxCompatabilityDataProvider.Should().BeNull();
    }

    private static GameLocatorResult LocateSingle(bool withProtonPrefix)
    {
        var fileSystem = new InMemoryFileSystem(OSInformation.FakeUnix);
        var steamDirectory = fileSystem.GetKnownPath(KnownPath.HomeDirectory).Combine(".steam/steam");
        var libraryDirectory = steamDirectory.Combine("steamapps");

        WriteFile(fileSystem, steamDirectory.Combine("steam.sh"), "");
        WriteFile(fileSystem, steamDirectory.Combine("config/libraryfolders.vdf"), LibraryFolders(steamDirectory));
        WriteFile(fileSystem, libraryDirectory.Combine("libraryfolders.vdf"), LibraryFolders(steamDirectory));
        WriteFile(fileSystem, libraryDirectory.Combine($"appmanifest_{AppId}.acf"), AppManifest());
        WriteFile(fileSystem, libraryDirectory.Combine("common/Game/game.exe"), "");
        if (withProtonPrefix) WriteFile(fileSystem, libraryDirectory.Combine($"compatdata/{AppId}/pfx/system.reg"), "");

        var game = Substitute.For<IGameData>();
        game.StoreIdentifiers.Returns(new StoreIdentifiers(GameId.From(1)) { SteamAppIds = [AppId] });

        var locator = new SteamLocator([game], NullLoggerFactory.Instance, fileSystem, registry: null);

        return locator.Locate().Should().ContainSingle().Subject;
    }

    private static void WriteFile(IFileSystem fileSystem, AbsolutePath path, string contents)
    {
        fileSystem.CreateDirectory(path.Parent);
        fileSystem.WriteAllText(path, contents);
    }

    private static string LibraryFolders(AbsolutePath steamDirectory) => $$"""
        "libraryfolders"
        {
            "0"
            {
                "path" "{{steamDirectory}}"
                "label" ""
                "contentid" "1"
                "totalsize" "0"
                "apps"
                {
                    "{{AppId}}" "1"
                }
            }
        }
        """;

    private static string AppManifest() => $$"""
        "AppState"
        {
            "appid" "{{AppId}}"
            "Universe" "1"
            "name" "Test Game"
            "StateFlags" "4"
            "installdir" "Game"
            "LastOwner" "76561198000000000"
            "BuildID" "1"
            "InstalledDepots"
            {
                "1091501"
                {
                    "manifest" "123456789"
                    "size" "1"
                }
            }
        }
        """;
}
