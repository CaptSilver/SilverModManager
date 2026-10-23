using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Mutagen.Bethesda;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.CreationEngine.Tests.Creations;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.Starfield;

/// <summary>
/// Starfield Creations: the catalog keys are TM_ guids, the files sit in the game-root Data folder and there is
/// no Creation Club list, so nothing is an early loader.
/// </summary>
public class StarfieldCreationsTests(ITestOutputHelper helper)
    : ASteamInstalledGameTest<StarfieldCreationsTests, CreationEngine.Starfield.Starfield>(helper)
{
    private const uint AppId = 1716740;
    private const uint GameDepotId = 1716741;
    private const ulong GameManifestId = 5000000000000000011;

    private const string FirstKey = "TM_11111111-2222-3333-4444-555555555555";
    private const string SecondKey = "TM_66666666-7777-8888-9999-000000000000";

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services).AddCreationEngine();

    protected override SteamInstall InstallGame(SteamLibraryFixture library)
    {
        var game = new SteamDepot(GameDepotId, GameManifestId,
        [
            new SteamFile("Starfield.exe", "starfield executable"),
            new SteamFile("Data/Starfield.esm", BethesdaPluginBytes.Bytes(GameRelease.Starfield, "Starfield.esm")),
        ]);
        var install = library.AddGame(AppId, "Starfield", "Starfield", 16000000, game);

        SteamLibraryFixture.WriteExtra(install, "Data/inventedtrailer.esm", BethesdaPluginBytes.Bytes(GameRelease.Starfield, "inventedtrailer.esm", "Starfield.esm"));
        SteamLibraryFixture.WriteExtra(install, "Data/inventedtrailer - main.ba2", "archive trailer");
        SteamLibraryFixture.WriteExtra(install, "Data/inventedgarage.esm", BethesdaPluginBytes.Bytes(GameRelease.Starfield, "inventedgarage.esm", "Starfield.esm"));
        SteamLibraryFixture.WriteExtra(install, "Data/inventedgarage - main.ba2", "archive garage");
        return install;
    }

    [Fact]
    public async Task CatalogEntriesBecomeItemsAndPluginsTxtStarsThem()
    {
        CreationsRecords.WriteCatalog(GameInstallation.Locations[LocationId.AppData].Path,
            (FirstKey, "Invented Trailer", ["inventedtrailer.esm", "inventedtrailer - main.ba2"]),
            (SecondKey, "Invented Garage", ["inventedgarage.esm", "inventedgarage - main.ba2"]));

        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));

        var items = CreationsLoadout.Items(Connection.Db, loadout.LoadoutId);
        items.Select(i => i.SourceId).Should().BeEquivalentTo(FirstKey, SecondKey);
        items.Should().OnlyContain(i => i.Source == "StarfieldCreation");
        var text = await GameInstallation.Locations.ToAbsolutePath(Game.PluginsFile).ReadAllTextAsync();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.Should().Contain("*inventedtrailer.esm").And.Contain("*inventedgarage.esm");
    }
}
