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
        FromSoftPaths.IsArchiveOrMedia(TestSoulsDefinition.Definition, new GamePath(LocationId.Game, path)).Should().Be(skipped);
    }

    private static FromSoftGameDefinition Definition(FromSoftGameLayout layout, string? regulation)
        => TestSoulsDefinition.Definition with { Layout = layout, Formats = TestSoulsDefinition.Definition.Formats with { RegulationFileName = regulation } };

    [Theory]
    [InlineData("Data0.bdt", false)]
    [InlineData("data0.BDT", false)]
    [InlineData("Data1.bdt", true)]
    [InlineData("param/Data0.bdt", true)]
    public void TheRegulationIsIndexedEvenWhenItsNameLooksLikeAnArchive(string inside, bool skipped)
    {
        var definition = Definition(AtTheRoot, "Data0.bdt");

        FromSoftPaths.IsArchiveOrMedia(definition, FromSoftPaths.InGameFolder(AtTheRoot, inside)).Should().Be(skipped);
    }

    [Theory]
    [InlineData(SoundFolderKind.Sd, "sd/x.fsb", true)]
    [InlineData(SoundFolderKind.Sd, "sound/x.fsb", false)]
    [InlineData(SoundFolderKind.Sound, "sound/x.fsb", true)]
    [InlineData(SoundFolderKind.Sound, "SOUND/x.fsb", true)]
    [InlineData(SoundFolderKind.Sound, "sd/x.fsb", false)]
    public void OnlyTheLayoutsOwnSoundFolderIsSkipped(SoundFolderKind sound, string inside, bool skipped)
    {
        var layout = AtTheRoot with { SoundFolder = sound };

        FromSoftPaths.IsArchiveOrMedia(Definition(layout, null), FromSoftPaths.InGameFolder(layout, inside)).Should().Be(skipped);
    }

    [Theory]
    [InlineData("Data1.bdt", true)]
    [InlineData("x.exe", false)]
    public void AtTheRootTheArchivesAreStillSkipped(string inside, bool skipped)
    {
        FromSoftPaths.IsArchiveOrMedia(Definition(AtTheRoot, null), FromSoftPaths.InGameFolder(AtTheRoot, inside)).Should().Be(skipped);
    }

    private static readonly FromSoftGameLayout AtTheRoot = new(1, "X", "", "x.exe", null, "X", SaveRootKind.RoamingAppData, SaveAccountFolderKind.SteamId64Decimal, SoundFolderKind.Sd);

    private static FromSoftGameLayout WithSubfolder(string subfolder)
        => new(1, "X", subfolder, "x.exe", null, "X", SaveRootKind.RoamingAppData, SaveAccountFolderKind.SteamId64Decimal, SoundFolderKind.Sd);

    [Fact]
    public void ALayoutMayPutTheExecutableAtTheInstallRoot()
    {
        var act = () => AtTheRoot;

        act.Should().NotThrow();
    }

    [Fact]
    public void AtTheRootTheGameFolderIsTheInstallFolder()
    {
        var installation = InstallationAt("/library/steamapps/common/Root", GameStore.Steam, "1");

        FromSoftPaths.GameFolder(AtTheRoot, installation).Should().Be(installation.Locations[LocationId.Game].Path);
    }

    [Theory]
    [InlineData("Bin", "parts/x.dcx", "Bin/parts/x.dcx")]
    [InlineData("", "parts/x.dcx", "parts/x.dcx")]
    public void InGameFolderJoinsTheSubfolderOnlyWhenThereIsOne(string subfolder, string inside, string expected)
    {
        var path = FromSoftPaths.InGameFolder(WithSubfolder(subfolder), inside);

        path.LocationId.Should().Be(LocationId.Game);
        path.Path.Path.Should().Be(expected);
    }

    [Theory]
    [InlineData("Bin", "Bin/x.dcx", "x.dcx")]
    [InlineData("Bin", "bin/x.dcx", "x.dcx")]
    [InlineData("Bin", "Other/x.dcx", null)]
    [InlineData("", "Artwork_MiniSoundtrack/a.exe", "Artwork_MiniSoundtrack/a.exe")]
    public void InsideGameFolderIsTheRemainderOrNull(string subfolder, string path, string? expected)
    {
        FromSoftPaths.InsideGameFolder(WithSubfolder(subfolder), new GamePath(LocationId.Game, path)).Should().Be(expected);
    }

    [Fact]
    public void AMe3ModsPathIsNeverInsideTheGameFolderEvenAtTheRoot()
    {
        FromSoftPaths.InsideGameFolder(AtTheRoot, new GamePath(LocationId.Me3Mods, "parts/x.dcx")).Should().BeNull();
    }

    [Fact]
    public void AtTheRootEveryGamePathIsInTheGameFolder()
    {
        FromSoftPaths.IsInGameFolder(AtTheRoot, new GamePath(LocationId.Game, "x.exe")).Should().BeTrue();
        FromSoftPaths.IsInGameFolder(AtTheRoot, new GamePath(LocationId.Me3Mods, "x.exe")).Should().BeFalse();
    }

    [Fact]
    public void AWhitespaceSubfolderIsRefused()
    {
        var act = () => WithSubfolder("  ");

        act.Should().Throw<ArgumentException>();
    }
}
