using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.FromSoft.Tests;

public class EldenRingDetectionTests(ITestOutputHelper helper) : AEldenRingTest<EldenRingDetectionTests>(helper)
{
    protected override bool UseDefaultDataRoot => true;

    [Fact]
    public void TheSteamInstallIsRegisteredAsEldenRing()
    {
        GameRegistry.LocateGameInstallations().Should().ContainSingle(i => i.Game is EldenRingGame);

        GameInstallation.Game.GameId.Should().Be(GameId.From("FromSoft.EldenRing"));
        GameInstallation.LocatorResult.Store.Should().Be(GameStore.Steam);
        GameInstallation.LocatorResult.StoreIdentifier.Should().Be("1245620");
    }

    [Fact]
    public void TheGameLocationIsTheInstallFolderAndNotItsGameSubfolder()
    {
        GameInstallation.Locations[LocationId.Game].Path.Should().Be(Install.Folder);
    }

    [Fact]
    public void ThePrimaryFileIsTheAntiCheatLauncherSteamStarts()
    {
        var primary = GameInstallation.Locations.ToAbsolutePath(Game.GetPrimaryFile(GameInstallation));

        primary.Should().Be(Install.Folder / "Game/start_protected_game.exe");
        primary.FileExists.Should().BeTrue();
    }

    [Fact]
    public void TheVersionIsReadFromTheGamesOwnExecutableNotTheLauncher()
    {
        // Any file with a version resource stands in for the game's executable; the launcher stays a stub without one.
        var standIn = NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath(typeof(EldenRingGame).Assembly.Location);
        var expected = standIn.FileInfo.GetFileVersionInfo().ProductVersion;
        File.Copy(standIn.ToString(), (Install.Folder / "Game/eldenring.exe").ToString(), overwrite: true);

        var version = ((IGameData)Game).GetLocalVersion(GameInstallation);

        version.HasValue.Should().BeTrue();
        version.Value.Should().Be(expected);
    }

    [Fact]
    public void TheMe3LocationsAreInTheHostDataFolder()
    {
        var dataHome = Library.FileSystem.GetKnownPath(KnownPath.XDG_DATA_HOME);
        var expected = dataHome / ApplicationConstants.DataDirectoryName / "FromSoft/EldenRing/1245620";

        GameInstallation.Locations[LocationId.Me3Mods].Path.Should().Be(expected / "mods");
        GameInstallation.Locations[LocationId.Me3Host].Path.Should().Be(expected / "me3");
    }

    [Fact]
    public void TheMe3LocationsDoNotDependOnWhichFileSystemTheyAreAskedThrough()
    {
        var viaTheGamesFileSystem = Game.GetLocations(GameInstallation.LocatorResult.Path.FileSystem, GameInstallation.LocatorResult);
        var viaTheHostsFileSystem = Game.GetLocations(ServiceProvider.GetRequiredService<IFileSystem>(), GameInstallation.LocatorResult);
        var viaAnotherFileSystem = Game.GetLocations(NexusMods.Paths.FileSystem.Shared, GameInstallation.LocatorResult);

        viaTheGamesFileSystem[LocationId.Me3Mods].Should().Be(viaTheHostsFileSystem[LocationId.Me3Mods]).And.Be(viaAnotherFileSystem[LocationId.Me3Mods]);
        viaTheGamesFileSystem[LocationId.Me3Host].Should().Be(viaTheHostsFileSystem[LocationId.Me3Host]).And.Be(viaAnotherFileSystem[LocationId.Me3Host]);
    }

    [Fact]
    public void SavesAreNotAMappedLocation()
    {
        Game.GetLocations(Library.FileSystem, GameInstallation.LocatorResult).Should().NotContainKey(LocationId.Saves);
    }
}
