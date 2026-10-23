using System.Reflection;
using FluentAssertions;
using SilverModManager.Games.FromSoft.EldenRing;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FromSoft.Tests;

public class EldenRingPathsTests
{
    private static readonly IFileSystem HostFileSystem = NexusMods.Paths.FileSystem.Shared.CreateOverlayFileSystem(
        new Dictionary<AbsolutePath, AbsolutePath>(),
        new Dictionary<KnownPath, AbsolutePath> { [KnownPath.XDG_DATA_HOME] = NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath("/fake/data") });

    private static FromSoftSettings DefaultSettings => FromSoftSettings.CreateDefault(null!);

    [Fact]
    public void TheSaveFolderIsTheAccountsFolderInsideThePrefix()
    {
        var prefix = HostFileSystem.FromUnsanitizedFullPath("/library/steamapps/compatdata/1245620/pfx");

        EldenRingPaths.SaveFolder(prefix, 76561198000000001).ToString()
            .Should().Be("/library/steamapps/compatdata/1245620/pfx/drive_c/users/steamuser/AppData/Roaming/EldenRing/76561198000000001");
    }

    [Fact]
    public void TheSaveFileNamesAreTheGamesAndTheModdedOne()
    {
        EldenRingPaths.VanillaSave.Should().Be("ER0000.sl2");
        EldenRingPaths.SeamlessCoopSave.Should().Be("ER0000.co2");
        EldenRingPaths.ModdedSave.Should().Be("ER0000_silver.sl2");
    }

    [Fact]
    public void TheMe3RootIsUnderTheDataRootOfTheSettings()
    {
        EldenRingPaths.Me3Root(DefaultSettings, HostFileSystem).ToString().Should().Be($"/fake/data/{ApplicationConstants.DataDirectoryName}/FromSoft/EldenRing");
    }

    [Fact]
    public void TheMe3RootFollowsAChangedDataRoot()
    {
        var settings = DefaultSettings with { DataRoot = new ConfigurablePath(null, "/elsewhere/modding") };

        EldenRingPaths.Me3Root(settings, HostFileSystem).ToString().Should().Be("/elsewhere/modding/EldenRing");
    }

    [Fact]
    public void ModsAndTheLoaderAreSeparateFoldersPerInstallation()
    {
        var root = EldenRingPaths.Me3Root(DefaultSettings, HostFileSystem);

        EldenRingPaths.Me3Mods(DefaultSettings, HostFileSystem, "1245620").Should().Be(root / "1245620/mods");
        EldenRingPaths.Me3Host(DefaultSettings, HostFileSystem, "1245620").Should().Be(root / "1245620/me3");
    }

    [Theory]
    [InlineData("Game/eldenring.exe", true)]
    [InlineData("game/ELDENRING.EXE", true)]
    [InlineData("Game/sd/sd.bdt", true)]
    [InlineData("ArtbookOST/readme.txt", false)]
    [InlineData("GameData/file.txt", false)]
    public void OnlyPathsUnderTheGameSubfolderAreInTheGameFolder(string path, bool expected)
    {
        EldenRingPaths.IsInGameFolder(new GamePath(LocationId.Game, path)).Should().Be(expected);
    }

    [Fact]
    public void ThePathsOfAnotherLocationAreNeverInTheGameFolder()
    {
        EldenRingPaths.IsInGameFolder(new GamePath(LocationId.Me3Mods, "Game/parts/x.dcx")).Should().BeFalse();
    }

    [Theory]
    [InlineData("Game/Data0.bdt", true)]
    [InlineData("Game/Data0.bhd", true)]
    [InlineData("Game/DATA3.BDT", true)]
    [InlineData("Game/sd/sd.bdt", true)]
    [InlineData("Game/sd/anything.txt", true)]
    [InlineData("Game/movie/intro.bk2", true)]
    [InlineData("Game/movie_extra/intro.bk2", true)]
    [InlineData("Game/eldenring.exe", false)]
    [InlineData("Game/start_protected_game.exe", false)]
    [InlineData("Game/regulation.bin", false)]
    [InlineData("Game/oo2core_6_win64.dll", false)]
    [InlineData("Game/steam_api64.dll", false)]
    [InlineData("Game/mods/sd/config.ini", false)]
    [InlineData("Game/sdk.txt", false)]
    [InlineData("ArtbookOST/Data0.bdt", false)]
    public void IndexingSkipsTheArchivesAndVideosButKeepsTheSmallFiles(string path, bool skipped)
    {
        EldenRingPaths.IsArchiveOrMedia(new GamePath(LocationId.Game, path)).Should().Be(skipped);
    }

    [Fact]
    public void TheMe3LocationIdsDifferFromEveryOtherLocationId()
    {
        var all = typeof(LocationId)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(LocationId))
            .ToDictionary(f => f.Name, f => (LocationId)f.GetValue(null)!);

        foreach (var name in new[] { nameof(LocationId.Me3Mods), nameof(LocationId.Me3Host) })
        {
            foreach (var (otherName, other) in all.Where(kv => kv.Key != name))
                all[name].Should().NotBe(other, $"{name} must not collide with {otherName}");
        }
    }
}
