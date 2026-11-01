using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.FromSoft.Tests.Saves;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FromSoft.Tests.Fixtures;

/// <summary>
/// The checks every FromSoft game passes when Steam has installed it: the real Steam locator finds it by its app id,
/// the location is the install folder, the primary file is what Steam starts, and the loader's folders sit under the
/// data root by the game's name and the installation's app id. A game's test class names its facts and nothing else.
/// </summary>
public abstract class AFromSoftInstalledGameTests<TTest, TGame>(ITestOutputHelper helper, FromSoftGameFacts facts)
    : ASteamInstalledGameTest<TTest, TGame>(helper)
    where TGame : AFromSoftGame<TGame>, IGameData<TGame>
{
    private const ulong BuildId = 15000000;

    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services).AddFromSoftGamesWithOwnDataRoot();

    protected override SteamInstall InstallGame(SteamLibraryFixture library)
    {
        var layout = facts.Layout;
        SteamFile[] files =
        [
            new($"{layout.GameSubfolder}/{layout.GameExecutable}", "the game"),
            ..layout.Launcher is null ? Array.Empty<SteamFile>() : [new SteamFile($"{layout.GameSubfolder}/{layout.Launcher}", "anti-cheat launcher")],
        ];
        return library.AddGame(facts.SteamAppId, facts.DisplayName, facts.SteamInstallFolder, BuildId, new SteamDepot(facts.SteamAppId + 1, 8000000000000000001, files));
    }

    [Fact]
    public void TheSteamInstallIsRegisteredAsTheGame()
    {
        GameRegistry.LocateGameInstallations().Should().ContainSingle(i => i.Game is TGame);

        GameInstallation.Game.GameId.Should().Be(GameId.From(facts.GameId));
        GameInstallation.Game.DisplayName.Should().Be(facts.DisplayName);
        GameInstallation.LocatorResult.Store.Should().Be(GameStore.Steam);
        GameInstallation.LocatorResult.StoreIdentifier.Should().Be(facts.SteamAppId.ToString());
    }

    [Fact]
    public void TheGameLocationIsTheInstallFolderAndNotItsGameSubfolder()
        => GameInstallation.Locations[LocationId.Game].Path.Should().Be(Install.Folder);

    [Fact]
    public void ThePrimaryFileIsTheFileSteamStarts()
    {
        var primary = GameInstallation.Locations.ToAbsolutePath(Game.GetPrimaryFile(GameInstallation));

        primary.Should().Be(Install.Folder / $"{facts.Layout.GameSubfolder}/{facts.Layout.Launcher ?? facts.Layout.GameExecutable}");
        primary.FileExists.Should().BeTrue();
    }

    [Fact]
    public void TheVersionIsReadFromTheGamesOwnExecutableNotTheLauncher()
    {
        // Any file with a version resource stands in for the game's executable; the launcher stays a stub without one.
        var standIn = NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath(typeof(TGame).Assembly.Location);
        var expected = standIn.FileInfo.GetFileVersionInfo().ProductVersion;
        File.Copy(standIn.ToString(), (Install.Folder / $"{facts.Layout.GameSubfolder}/{facts.Layout.GameExecutable}").ToString(), overwrite: true);

        var version = ((IGameData)Game).GetLocalVersion(GameInstallation);

        version.HasValue.Should().BeTrue();
        version.Value.Should().Be(expected);
    }

    [Fact]
    public void TheMe3LocationsAreInTheHostDataFolderByGameAndAppId()
    {
        var dataRoot = ServiceProvider.GetRequiredService<Sdk.Settings.ISettingsManager>().Get<FromSoftSettings>().DataRoot.ToPath(ServiceProvider.GetRequiredService<IFileSystem>());
        var expected = dataRoot / facts.Layout.DataFolderName / facts.SteamAppId.ToString();

        GameInstallation.Locations[LocationId.Me3Mods].Path.Should().Be(expected / "mods");
        GameInstallation.Locations[LocationId.Me3Host].Path.Should().Be(expected / "me3");
    }

    [Fact]
    public void SavesAreNotAMappedLocation()
        => Game.GetLocations(Library.FileSystem, GameInstallation.LocatorResult).Should().NotContainKey(LocationId.Saves);

    [Fact]
    public async Task TheGameKeepsAKeepSavesLoadoutsSavesInASlotOfItsOwn()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(loadout.Id, Loadout.KeepSavesWithLoadout, true);
            await tx.Commit();
        }

        var plan = ((IPerLoadoutSaves)Game).PlanSaveRedirect(Refresh(loadout));

        plan.Should().Be(new SaveRedirectPlan.LoaderKey("savefile", facts.SaveNames.Modded.Replace("silver", LoadoutSaveSlot.IdOf(loadout))));
    }

    [Fact]
    public async Task AdoptingCopiesTheModdedSaveToTheLoadoutsSlot()
    {
        var loadout = await CreateLoadout();
        using var folder = new SaveFixture();
        var root = NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath(folder.SaveFolder);
        folder.Save(facts.SaveNames.Modded, "mine");
        var slot = facts.SaveNames.Modded.Replace("silver", LoadoutSaveSlot.IdOf(loadout));

        var plan = ((IPerLoadoutSaves)Game).PlanAdoptCurrentSave(loadout, root);

        plan.Should().NotBeNull();
        plan!.Copies.Should().Equal(new SaveAdoptionPlan.Copy(root / facts.SaveNames.Modded, root / slot));
    }
}
