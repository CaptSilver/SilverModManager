using System.Collections.Immutable;
using FluentAssertions;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FromSoft.Tests;

public class FromSoftPathsTests
{
    private static readonly IFileSystem HostFileSystem = NexusMods.Paths.FileSystem.Shared.CreateOverlayFileSystem(
        new Dictionary<AbsolutePath, AbsolutePath>(),
        new Dictionary<KnownPath, AbsolutePath> { [KnownPath.XDG_DATA_HOME] = NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath("/fake/data") });

    private static FromSoftSettings DefaultSettings => FromSoftSettings.CreateDefault(null!);

    private static readonly FromSoftGameLayout TestSouls = TestSoulsDefinition.Layout;
    private static readonly FromSoftGameLayout EldenRing = EldenRingGame.Definition.Layout;

    private static GameInstallation InstallationAt(string path, GameStore store, string storeIdentifier)
    {
        var fs = new InMemoryFileSystem();
        var root = fs.FromUnsanitizedFullPath(path);
        var locatorResult = new GameLocatorResult
        {
            Game = null!,
            Path = root,
            Store = store,
            StoreIdentifier = storeIdentifier,
            LocatorIds = [],
            Locator = null!,
        };
        var locations = GameLocations.Create(new Dictionary<LocationId, AbsolutePath> { { LocationId.Game, root } }.ToImmutableDictionary());
        return new GameInstallation(locatorResult, locations);
    }

    [Fact]
    public void TheGameFolderIsTheLayoutsSubfolderOfTheInstallFolder()
    {
        var installation = InstallationAt("/library/steamapps/common/TestSouls", GameStore.Steam, "424242");

        FromSoftPaths.GameFolder(TestSouls, installation).ToString().Should().Be("/library/steamapps/common/TestSouls/Bin");
        FromSoftPaths.GameFolder(EldenRing, installation).ToString().Should().Be("/library/steamapps/common/TestSouls/Game");
    }

    [Fact]
    public void TheSaveFolderIsNamedAfterTheLayoutsSaveFolder()
    {
        var prefix = HostFileSystem.FromUnsanitizedFullPath("/library/steamapps/compatdata/424242/pfx");

        FromSoftPaths.SaveRoot(TestSouls, prefix).ToString()
            .Should().Be("/library/steamapps/compatdata/424242/pfx/drive_c/users/steamuser/AppData/Roaming/TestSouls");
        FromSoftPaths.SaveFolder(TestSouls, prefix, 76561198000000001).ToString()
            .Should().Be("/library/steamapps/compatdata/424242/pfx/drive_c/users/steamuser/AppData/Roaming/TestSouls/76561198000000001");
    }

    [Fact]
    public void TheSavePathsOfEldenRingAreTheOnesItsFacadeGives()
    {
        var prefix = HostFileSystem.FromUnsanitizedFullPath("/library/steamapps/compatdata/1245620/pfx");

        FromSoftPaths.SaveFolder(EldenRing, prefix, 76561198000000001).ToString()
            .Should().Be("/library/steamapps/compatdata/1245620/pfx/drive_c/users/steamuser/AppData/Roaming/EldenRing/76561198000000001");
    }

    [Fact]
    public void ASteamInstallationIsNamedByItsStoreId()
    {
        var installation = InstallationAt("/games/x", GameStore.Steam, "999");

        FromSoftPaths.InstallationFolderName(TestSouls, installation.LocatorResult).Should().Be("999");
    }

    [Fact]
    public void AnInstallationFromAnotherStoreIsNamedByTheLayoutsAppId()
    {
        var installation = InstallationAt("/games/x", GameStore.GOG, "abc");

        FromSoftPaths.InstallationFolderName(TestSouls, installation.LocatorResult).Should().Be("424242");
    }

    [Fact]
    public void TheHostSideFoldersAreUnderTheLayoutsDataFolder()
    {
        var root = FromSoftPaths.DataRoot(TestSouls, DefaultSettings, HostFileSystem);

        root.ToString().Should().Be($"/fake/data/{ApplicationConstants.DataDirectoryName}/FromSoft/TestSouls");
        FromSoftPaths.InstallationRoot(TestSouls, DefaultSettings, HostFileSystem, "424242").Should().Be(root / "424242");
        FromSoftPaths.CleanupFolder(TestSouls, DefaultSettings, HostFileSystem, "424242").Should().Be(root / "424242/cleanup");
    }

    [Fact]
    public void EldenRingsDataRootIsTheFolderItAlwaysUsed()
    {
        FromSoftPaths.DataRoot(EldenRing, DefaultSettings, HostFileSystem).ToString()
            .Should().Be($"/fake/data/{ApplicationConstants.DataDirectoryName}/FromSoft/EldenRing");
    }

    [Theory]
    [InlineData("Bin/testsouls.exe", true)]
    [InlineData("bin/TESTSOULS.EXE", true)]
    [InlineData("Game/testsouls.exe", false)]
    [InlineData("Binary/file.txt", false)]
    public void OnlyPathsUnderTheLayoutsSubfolderAreInTheGameFolder(string path, bool expected)
    {
        FromSoftPaths.IsInGameFolder(TestSouls, new GamePath(LocationId.Game, path)).Should().Be(expected);
    }

    [Fact]
    public void ThePathsOfAnotherLocationAreNeverInTheGameFolder()
    {
        FromSoftPaths.IsInGameFolder(TestSouls, new GamePath(LocationId.Me3Mods, "Bin/x.dcx")).Should().BeFalse();
    }

    [Theory]
    [InlineData("Bin/Data0.bdt", true)]
    [InlineData("Bin/Data0.BHD", true)]
    [InlineData("Bin/sd/sd.bdt", true)]
    [InlineData("Bin/movie_extra/intro.bk2", true)]
    [InlineData("Bin/testsouls.exe", false)]
    [InlineData("Bin/sdk.txt", false)]
    [InlineData("Game/Data0.bdt", false)]
    public void IndexingSkipsTheArchivesAndVideosUnderTheLayoutsSubfolder(string path, bool skipped)
    {
        FromSoftPaths.IsArchiveOrMedia(TestSouls, new GamePath(LocationId.Game, path)).Should().Be(skipped);
    }

    [Fact]
    public void AnEmptySubfolderIsRefused()
    {
        var act = () => new FromSoftGameLayout(1, "X", "", "x.exe", null, "X", SaveRootKind.RoamingAppData, SaveAccountFolderKind.SteamId64Decimal);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AWhitespaceSubfolderIsRefused()
    {
        var act = () => new FromSoftGameLayout(1, "X", "  ", "x.exe", null, "X", SaveRootKind.RoamingAppData, SaveAccountFolderKind.SteamId64Decimal);

        act.Should().Throw<ArgumentException>();
    }
}
