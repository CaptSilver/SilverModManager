using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Backend.Games.Locators;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NSubstitute;

namespace NexusMods.Backend.Tests.Games;

/// <summary>
/// A Steam installed from Flathub keeps everything under <c>~/.var/app/com.valvesoftware.Steam</c> and leaves
/// no <c>~/.steam</c> or <c>~/.local/share/Steam</c> behind, so it can only be found by that path.
/// </summary>
public class SteamLocatorFlatpakTests
{
    private const uint OblivionAppId = 22330;
    private const string FlatpakSteamRoot = ".var/app/com.valvesoftware.Steam/data/Steam";

    [Fact]
    public void Locate_FlatpakSteamAlone_FindsTheGameUnderTheFlatpakRoot()
    {
        var fileSystem = new InMemoryFileSystem(OSInformation.FakeUnix);
        var flatpakRoot = HomeOf(fileSystem).Combine(FlatpakSteamRoot);
        AddSteamWithOblivion(fileSystem, flatpakRoot, buildId: "4242424");

        var result = Locate(fileSystem).Should().ContainSingle().Subject;

        result.Path.GetFullPath().Should().Be(flatpakRoot.Combine("steamapps/common/Oblivion").GetFullPath());
        result.Path.GetFullPath().Should().Contain(".var/app");
        result.StoreRoot.Should().Be(flatpakRoot);
        result.StoreBuildId.Should().Be("4242424");
        result.StoreIdentifier.Should().Be(OblivionAppId.ToString());
    }

    private static AbsolutePath HomeOf(IFileSystem fileSystem) => fileSystem.GetKnownPath(KnownPath.HomeDirectory);

    private static GameLocatorResult[] Locate(IFileSystem fileSystem)
    {
        var game = Substitute.For<IGameData>();
        game.StoreIdentifiers.Returns(new StoreIdentifiers(GameId.From(1)) { SteamAppIds = [OblivionAppId] });
        game.HasNativeLinuxBuild.Returns(false);

        return new SteamLocator([game], NullLoggerFactory.Instance, fileSystem, registry: null).Locate().ToArray();
    }

    private static void AddSteamWithOblivion(IFileSystem fileSystem, AbsolutePath steamRoot, string buildId)
    {
        var steamapps = steamRoot.Combine("steamapps");
        WriteFile(fileSystem, steamapps.Combine("libraryfolders.vdf"), LibraryFolders(steamRoot));
        WriteFile(fileSystem, steamRoot.Combine("config/libraryfolders.vdf"), LibraryFolders(steamRoot));
        WriteFile(fileSystem, steamRoot.Combine("config/config.vdf"), "\"InstallConfigStore\"\n{\n}\n");
        WriteFile(fileSystem, steamapps.Combine($"appmanifest_{OblivionAppId}.acf"), $$"""
            "AppState"
            {
                "appid" "{{OblivionAppId}}"
                "Universe" "1"
                "name" "The Elder Scrolls IV: Oblivion"
                "StateFlags" "4"
                "installdir" "Oblivion"
                "LastOwner" "76561198000000000"
                "BuildID" "{{buildId}}"
                "InstalledDepots"
                {
                    "22331"
                    {
                        "manifest" "123456789"
                        "size" "1"
                    }
                }
            }
            """);
        WriteFile(fileSystem, steamapps.Combine("common/Oblivion/Oblivion.exe"), "");
    }

    private static void WriteFile(IFileSystem fileSystem, AbsolutePath path, string contents)
    {
        fileSystem.CreateDirectory(path.Parent);
        fileSystem.WriteAllText(path, contents);
    }

    private static string LibraryFolders(AbsolutePath steamRoot) => $$"""
        "libraryfolders"
        {
            "0"
            {
                "path" "{{steamRoot}}"
                "label" ""
                "contentid" "1"
                "totalsize" "0"
                "apps"
                {
                    "{{OblivionAppId}}" "1"
                }
            }
        }
        """;
}
