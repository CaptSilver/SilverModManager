using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Games.CreationEngine.Tests.Creations;
using NexusMods.Games.TestFramework;
using NexusMods.Games.TestFramework.Helpers;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests.SkyrimSE;

/// <summary>
/// A Creation whose plugin the store's depot also lists: the game rewrites it when it updates the Creation, so
/// its hash drifts away from the depot's. That is not a change the user made.
/// </summary>
public class SteamInstalledSkyrimDepotCreationTests(ITestOutputHelper helper)
    : ASteamInstalledGameTest<SteamInstalledSkyrimDepotCreationTests, CreationEngine.SkyrimSE.SkyrimSE>(helper)
{
    private const uint AppId = 489830;
    private const uint GameDepotId = 489831;
    private const ulong GameManifestId = 5000000000000000001;

    private static readonly GamePath Plugin = new(LocationId.Game, "Data/ccinvented001-garden.esm");

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services).AddCreationEngine();

    protected override SteamInstall InstallGame(SteamLibraryFixture library)
    {
        var game = new SteamDepot(GameDepotId, GameManifestId,
        [
            new SteamFile("SkyrimSE.exe", "skyrim executable"),
            new SteamFile("Data/Skyrim.esm", SkyrimPluginFile.Bytes("Skyrim.esm")),
            new SteamFile("Data/Update.esm", SkyrimPluginFile.Bytes("Update.esm", "Skyrim.esm")),
            new SteamFile("Data/Dawnguard.esm", SkyrimPluginFile.Bytes("Dawnguard.esm", "Skyrim.esm")),
            new SteamFile("Data/ccinvented001-garden.esm", SkyrimPluginFile.Bytes("ccinvented001-garden.esm", "Skyrim.esm")),
        ]);
        return library.AddGame(AppId, "Skyrim Special Edition", "The Elder Scrolls V: Skyrim Special Edition", 16000000, game);
    }

    private async Task<Loadout.ReadOnly> AppliedLoadout()
    {
        CreationsRecords.WriteCatalog(GameInstallation.Locations[LocationId.AppData].Path,
            ("CSV2_1", "Invented Garden", ["ccinvented001-garden.esm"]));
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));
        return Refresh(loadout);
    }

    private void GameUpdatesThePlugin() =>
        File.WriteAllBytes(GameFolder.Combine("Data/ccinvented001-garden.esm").ToString(),
            SkyrimPluginFile.Bytes("ccinvented001-garden.esm", "Skyrim.esm", "Update.esm"));

    [Fact]
    public async Task AnUpdatedDepotListedCreationIsLeftAloneAndMarkedDepotShipped()
    {
        var loadout = await AppliedLoadout();

        GameUpdatesThePlugin();
        await Synchronizer.Synchronize(Refresh(loadout));

        var tree = await Synchronizer.BuildSyncTree(Refresh(loadout));
        Synchronizer.ProcessSyncTree(tree);
        tree[Plugin].Actions.Should().Be(Actions.DoNothing);
        CreationsLoadout.ExternalChanges(Connection.Db, loadout.LoadoutId).Should().BeEmpty();
        var item = CreationsLoadout.Items(Connection.Db, loadout.LoadoutId).Should().ContainSingle().Subject;
        item.DepotShipped.Should().BeTrue();
    }

    [Fact]
    public async Task AResetKeepsTheUpdatedCopy()
    {
        var loadout = await AppliedLoadout();
        GameUpdatesThePlugin();
        await Synchronizer.Synchronize(Refresh(loadout));
        var updated = await File.ReadAllBytesAsync(GameFolder.Combine("Data/ccinvented001-garden.esm").ToString());

        await Synchronizer.ResetToOriginalGameState(GameInstallation, GameInstallation.LocatorResult.LocatorIds.ToArray());

        (await File.ReadAllBytesAsync(GameFolder.Combine("Data/ccinvented001-garden.esm").ToString())).Should().Equal(updated);
    }
}
