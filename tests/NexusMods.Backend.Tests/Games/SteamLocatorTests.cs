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
        var result = LocateSingle(withProtonPrefix: true).Result;

        result.Platform.Should().Be(OSPlatform.Windows, "Proton runs the Windows build of the game");
        result.LinuxCompatabilityDataProvider.Should().NotBeNull();
        result.LinuxCompatabilityDataProvider!.WinePrefixDirectoryPath.FileName.Should().Be("pfx");
    }

    [Fact]
    public void Locate_WindowsBuild_RecordsSteamProtonWithThePrefix()
    {
        var (result, _, libraryDirectory) = LocateSingle(withProtonPrefix: false);

        result.Compatibility.Should().Be(new CompatibilityLayer(CompatibilityLayerKind.SteamProton, libraryDirectory.Combine($"compatdata/{AppId}/pfx")));
    }

    [Fact]
    public void Locate_NativeLinuxBuild_RecordsNoCompatibilityLayer()
    {
        LocateSingle(withProtonPrefix: false, hasNativeLinuxBuild: true).Result.Compatibility.Should().BeNull();
    }

    [Fact]
    public void Locate_ProtonGame_ResolvesKnownFoldersInsideThePrefix()
    {
        var (result, _, libraryDirectory) = LocateSingle(withProtonPrefix: true);
        var home = libraryDirectory.Combine($"compatdata/{AppId}/pfx/drive_c/users/steamuser");
        var fileSystem = result.Path.FileSystem;

        fileSystem.GetKnownPath(KnownPath.LocalApplicationDataDirectory).Should().Be(home.Combine("AppData/Local"));
        fileSystem.GetKnownPath(KnownPath.ApplicationDataDirectory).Should().Be(home.Combine("AppData/Roaming"));
        fileSystem.GetKnownPath(KnownPath.MyDocumentsDirectory).Should().Be(home.Combine("Documents"));
        fileSystem.GetKnownPath(KnownPath.MyGamesDirectory).Should().Be(home.Combine("Documents/My Games"));
        result.WinePrefixPath.Should().Be(libraryDirectory.Combine($"compatdata/{AppId}/pfx"));
    }

    [Fact]
    public void Locate_ProtonGame_KeepsTheGameDirectory()
    {
        var (result, _, libraryDirectory) = LocateSingle(withProtonPrefix: true);

        result.Path.GetFullPath().Should().Be(libraryDirectory.Combine("common/Game").GetFullPath());
    }

    [Fact]
    public void Locate_EmptyCompatdataDirectory_IsNotAReadyPrefix()
    {
        var (result, _, libraryDirectory) = LocateSingle(withProtonPrefix: false, emptyCompatdata: true);

        result.Platform.Should().Be(OSPlatform.Windows);
        result.LinuxCompatabilityDataProvider.Should().BeNull("Proton has not written system.reg yet");
        result.WinePrefixPath.Should().Be(libraryDirectory.Combine($"compatdata/{AppId}/pfx"));
    }

    [Fact]
    public void Locate_WindowsGameWithoutProtonPrefix_ResolvesIntoThePrefixThatDoesNotExistYet()
    {
        var (result, hostFileSystem, libraryDirectory) = LocateSingle(withProtonPrefix: false);
        var expectedPrefix = libraryDirectory.Combine($"compatdata/{AppId}/pfx");

        result.Platform.Should().Be(OSPlatform.Windows);
        result.LinuxCompatabilityDataProvider.Should().BeNull();
        result.WinePrefixPath.Should().Be(expectedPrefix);

        KnownPath[] knownPaths =
        [
            KnownPath.LocalApplicationDataDirectory,
            KnownPath.ApplicationDataDirectory,
            KnownPath.MyDocumentsDirectory,
            KnownPath.MyGamesDirectory,
            KnownPath.HomeDirectory,
        ];

        foreach (var knownPath in knownPaths)
        {
            var resolved = result.Path.FileSystem.GetKnownPath(knownPath);
            resolved.GetFullPath().Should().StartWith(expectedPrefix.GetFullPath(), $"{knownPath} must not fall back to the host");
            resolved.Should().NotBe(hostFileSystem.GetKnownPath(knownPath));
        }
    }

    [Fact]
    public void Locate_ProtonGame_ExposesSteamInstallAndLocalConfigPaths()
    {
        var (result, hostFileSystem, _) = LocateSingle(withProtonPrefix: true);
        var steamDirectory = hostFileSystem.GetKnownPath(KnownPath.HomeDirectory).Combine(".steam/steam");

        var provider = result.LinuxCompatabilityDataProvider!;

        provider.SteamInstallPath.Should().Be(steamDirectory);
        // userdata is keyed by the 32-bit account id, which is the 64-bit LastOwner minus the individual-account base
        provider.SteamLocalConfigPath.Should().Be(steamDirectory.Combine("userdata/39734272/config/localconfig.vdf"));
    }

    [Fact]
    public async Task GetWineDllOverrides_ReadsTheFileAtSteamLocalConfigPath()
    {
        var (result, hostFileSystem, _) = LocateSingle(withProtonPrefix: true);
        var provider = result.LinuxCompatabilityDataProvider!;
        WriteFile(hostFileSystem, provider.SteamLocalConfigPath, LocalConfig());

        var overrides = await provider.GetWineDllOverrides(CancellationToken.None);

        overrides.Should().ContainSingle().Which.DllName.Should().Be("winhttp");
    }

    [Fact]
    public void Locate_NativeLinuxGameWithoutProtonPrefix_UsesTheHostFileSystem()
    {
        var (result, hostFileSystem, _) = LocateSingle(withProtonPrefix: false, hasNativeLinuxBuild: true);

        result.Platform.Should().Be(OSPlatform.Linux);
        result.LinuxCompatabilityDataProvider.Should().BeNull();
        result.WinePrefixPath.Should().BeNull();
        result.Path.FileSystem.GetKnownPath(KnownPath.LocalApplicationDataDirectory)
            .Should().Be(hostFileSystem.GetKnownPath(KnownPath.LocalApplicationDataDirectory));
    }

    [Fact]
    public void Locate_NativeLinuxGameWithProtonPrefix_IsTheWindowsBuild()
    {
        var result = LocateSingle(withProtonPrefix: true, hasNativeLinuxBuild: true).Result;

        result.Platform.Should().Be(OSPlatform.Windows, "a ready prefix means the game ran through Proton");
        result.WinePrefixPath.Should().NotBeNull();
    }

    private static (GameLocatorResult Result, InMemoryFileSystem HostFileSystem, AbsolutePath LibraryDirectory) LocateSingle(
        bool withProtonPrefix,
        bool emptyCompatdata = false,
        bool hasNativeLinuxBuild = false)
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
        if (emptyCompatdata) fileSystem.CreateDirectory(libraryDirectory.Combine($"compatdata/{AppId}"));

        var game = Substitute.For<IGameData>();
        game.StoreIdentifiers.Returns(new StoreIdentifiers(GameId.From(1)) { SteamAppIds = [AppId] });
        game.HasNativeLinuxBuild.Returns(hasNativeLinuxBuild);

        var locator = new SteamLocator([game], NullLoggerFactory.Instance, fileSystem, registry: null);

        return (locator.Locate().Should().ContainSingle().Subject, fileSystem, libraryDirectory);
    }

    private static void WriteFile(IFileSystem fileSystem, AbsolutePath path, string contents)
    {
        fileSystem.CreateDirectory(path.Parent);
        fileSystem.WriteAllText(path, contents);
    }

    private static string LocalConfig() => $$"""
        "UserLocalConfigStore"
        {
            "Software"
            {
                "Valve"
                {
                    "Steam"
                    {
                        "apps"
                        {
                            "{{AppId}}"
                            {
                                "LaunchOptions" "WINEDLLOVERRIDES=\"winhttp=n,b\" %command%"
                            }
                        }
                    }
                }
            }
        }
        """;

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
