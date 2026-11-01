using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
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
}
