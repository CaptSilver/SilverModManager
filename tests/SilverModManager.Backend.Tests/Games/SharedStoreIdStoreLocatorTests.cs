using FluentAssertions;
using GameFinder.RegistryUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using SilverModManager.Backend.Games.Locators;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Backend.Tests.Games;

/// <summary>
/// Two games on one GOG product or Epic catalog item (Nehrim over Oblivion, Fallout 4 London over Fallout 4):
/// the install folder says which one it is, by the files only one of them has. GOG is read from a Wine prefix's
/// registry, the way the app reads it on Linux.
/// </summary>
public class SharedStoreIdStoreLocatorTests
{
    private const long ProductId = 1458058109;
    private const string CatalogItemId = "5beededaad9743df90e8f07d92df153f";
    private static readonly RelativePath IdentifyingFile = RelativePath.FromUnsanitizedInput("Data/Nehrim.esm");

    private static FakeGameData Plain(string id) => new(id, primaryFile: "game.exe") { StoreIdentifiers = Claim(id) };

    private static FakeIdentifiedGameData Identified(string id) => new(id, "game.exe", [IdentifyingFile]) { StoreIdentifiers = Claim(id) };

    private static StoreIdentifiers Claim(string id) => new(GameId.From(id)) { GOGProductIds = [ProductId], EGSCatalogItemId = [CatalogItemId] };

    [Fact]
    public void Gog_FolderWithoutTheIdentifyingFile_IsTheGameThatNamesNone()
    {
        var results = LocateGog(withFile: false, Identified("Conversion"), Plain("Base"));

        results.Should().ContainSingle().Which.Game.GameId.Should().Be(GameId.From("Base"));
    }

    [Fact]
    public void Gog_FolderWithTheIdentifyingFile_IsTheGameThatNamesIt()
    {
        var results = LocateGog(withFile: true, Plain("Base"), Identified("Conversion"));

        results.Should().ContainSingle().Which.Game.GameId.Should().Be(GameId.From("Conversion"));
    }

    [Fact]
    public void Egs_FolderWithoutTheIdentifyingFile_IsTheGameThatNamesNone()
    {
        var results = LocateEgs(withFile: false, Identified("Conversion"), Plain("Base"));

        results.Should().ContainSingle().Which.Game.GameId.Should().Be(GameId.From("Base"));
    }

    [Fact]
    public void Egs_FolderWithTheIdentifyingFile_IsTheGameThatNamesIt()
    {
        var results = LocateEgs(withFile: true, Plain("Base"), Identified("Conversion"));

        results.Should().ContainSingle().Which.Game.GameId.Should().Be(GameId.From("Conversion"));
    }

    private static GameLocatorResult[] LocateGog(bool withFile, params IGameData[] games)
    {
        var (fileSystem, bottle) = NewBottle();
        fileSystem.CreateDirectory(bottle.Combine("drive_c/GOG Games/Game"));
        fileSystem.WriteAllText(bottle.Combine("system.reg"),
            "WINE REGISTRY Version 2\n\n" +
            $"[Software\\\\GOG.com\\\\Games\\\\{ProductId}] 1700000000\n" +
            $"\"gameID\"=\"{ProductId}\"\n" +
            "\"gameName\"=\"Game\"\n" +
            "\"path\"=\"C:\\\\GOG Games\\\\Game\"\n" +
            "\"buildId\"=\"5\"\n");
        WriteGameFiles(fileSystem, bottle.Combine("drive_c/GOG Games/Game"), withFile);

        return Locate(fileSystem, (_, loggerFactory, overlay, registry) => new GOGLocator(games, loggerFactory, overlay, registry));
    }

    // GameFinder's Epic handler reads the launcher's manifest folder and has no Wine support of its own; a Windows
    // file system stands in for the prefix's C: drive.
    private static GameLocatorResult[] LocateEgs(bool withFile, params IGameData[] games)
    {
        var fileSystem = new InMemoryFileSystem(OSInformation.FakeWindows);
        var install = fileSystem.FromUnsanitizedFullPath(@"C:\Program Files\Epic Games\Game");
        WriteGameFiles(fileSystem, install, withFile);
        var manifests = fileSystem.FromUnsanitizedFullPath(@"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests");
        fileSystem.CreateDirectory(manifests);
        fileSystem.WriteAllText(manifests.Combine("A1B2C3.item"), Manifest);

        // The launcher records where its manifests are; without it the handler looks in the machine's shared data folder.
        var registry = new InMemoryRegistry();
        registry.AddKey(RegistryHive.CurrentUser, @"Software\Epic Games\EOS").AddValue("ModSdkMetadataDir", manifests.GetFullPath());

        return new EGSLocator(games, NullLoggerFactory.Instance, fileSystem, registry).Locate().ToArray();
    }

    private static void WriteGameFiles(InMemoryFileSystem fileSystem, AbsolutePath folder, bool withFile)
    {
        fileSystem.CreateDirectory(folder);
        fileSystem.WriteAllText(folder.Combine("game.exe"), "");
        if (!withFile) return;
        fileSystem.CreateDirectory(folder.Combine(IdentifyingFile).Parent);
        fileSystem.WriteAllText(folder.Combine(IdentifyingFile), "");
    }

    private static GameLocatorResult[] Locate(InMemoryFileSystem fileSystem, WinePrefixWrappingLocator.LocatorFactory factory)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IFileSystem>(fileSystem)
            .BuildServiceProvider();

        return new WinePrefixWrappingLocator(services, [factory]).Locate().ToArray();
    }

    private static (InMemoryFileSystem FileSystem, AbsolutePath Bottle) NewBottle()
    {
        var fileSystem = new InMemoryFileSystem(OSInformation.FakeUnix);
        var bottle = fileSystem.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine("bottles/bottles/Games");
        fileSystem.CreateDirectory(bottle.Combine("drive_c"));
        fileSystem.WriteAllText(bottle.Combine("bottle.yml"), "Runner: soda-9.0-1\n");
        fileSystem.WriteAllText(bottle.Combine("system.reg"), "WINE REGISTRY Version 2\n");
        fileSystem.WriteAllText(bottle.Combine("user.reg"), "WINE REGISTRY Version 2\n");
        fileSystem.WriteAllText(bottle.Combine("userdef.reg"), "WINE REGISTRY Version 2\n");
        return (fileSystem, bottle);
    }

    private static string Manifest => $$"""
        {
          "FormatVersion": 0,
          "bIsIncompleteInstall": false,
          "LaunchCommand": "",
          "LaunchExecutable": "game.exe",
          "ManifestLocation": "C:/ProgramData/Epic/EpicGamesLauncher/Data/Manifests/A1B2C3.manifest",
          "ManifestHash": "A1B2C3",
          "bIsApplication": true,
          "bIsExecutable": true,
          "bIsManaged": false,
          "bNeedsValidation": false,
          "bRequiresAuth": true,
          "bAllowMultipleInstances": false,
          "bCanRunOffline": true,
          "bAllowUriCmdArgs": true,
          "bLaunchElevated": false,
          "BaseURLs": [],
          "BuildLabel": "label",
          "AppCategories": ["public", "games", "applications"],
          "ChunkDbs": [],
          "CompatibleApps": [],
          "DisplayName": "Game",
          "InstallationGuid": "00000000-0000-0000-0000-000000000000",
          "InstallLocation": "C:\\Program Files\\Epic Games\\Game",
          "InstallSessionId": "00000000000000000000000000000000",
          "InstallTags": [],
          "InstallComponents": [],
          "HostInstallationGuid": "00000000-0000-0000-0000-000000000000",
          "PrereqIds": [],
          "PrereqSHA1Hash": "",
          "LastPrereqSucceededSHA1Hash": "",
          "StagingLocation": "",
          "TechnicalType": "",
          "VaultThumbnailUrl": "",
          "VaultTitleText": "",
          "InstallSize": 1,
          "MainWindowProcessName": "",
          "ProcessNames": [],
          "BackgroundProcessNames": [],
          "IgnoredProcessNames": [],
          "DlcProcessNames": [],
          "MandatoryAppFolderName": "Game",
          "OwnershipToken": "false",
          "CatalogNamespace": "ns",
          "CatalogItemId": "{{CatalogItemId}}",
          "AppName": "Fnord",
          "AppVersionString": "1.0",
          "MainGameCatalogNamespace": "ns",
          "MainGameCatalogItemId": "{{CatalogItemId}}",
          "MainGameAppName": "Fnord",
          "AllowedUriEnvVars": []
        }
        """;
}
