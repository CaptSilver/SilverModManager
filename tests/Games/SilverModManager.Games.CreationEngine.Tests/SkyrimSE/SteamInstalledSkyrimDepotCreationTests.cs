using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Games.CreationEngine.SortOrder;
using SilverModManager.Games.CreationEngine.Tests.Creations;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.SkyrimSE;

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

    protected override bool StoreVouchesForTheFiles => true;

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

    [Fact]
    public async Task ADepotShippedCreation_IsReadOnceWhenRecognisedAndKeepsBothRows()
    {
        FullHashes.Reset();
        await AppliedLoadout();

        // The game-installed row is keyed by xxHash3, which a file the store vouched for does not have until it is read.
        FullHashes.Paths.Select(p => Path.GetRelativePath(GameFolder.ToString(), p.ToString())).Should().Equal("Data/ccinvented001-garden.esm");
        GameInstalledFile.All(Connection.Db).Select(f => ((GamePath)f.Path).Path.ToString()).Should().Contain("Data/ccinvented001-garden.esm");
        Hashes.GetGameFiles(GameInstallation).Select(f => f.Path).Should().Contain(Plugin, "the depot still lists the file");

        FullHashes.Reset();
        await Synchronizer.Synchronize(Refresh(await AppliedLoadout()));
        FullHashes.Paths.Should().BeEmpty("the read is remembered");
    }

    [Fact]
    public async Task AnAttestedVanillaMaster_KeepsItsHeaderAndSorts()
    {
        var loadout = await AppliedLoadout();

        var variety = Game.SortOrderManager.GetSortOrderVarieties().OfType<IPluginSortOrderVariety>().Single();
        string[] stored = [];
        await WaitUntil(() => (stored = variety.StoredOrder(Refresh(loadout)).Select(row => row.Name).ToArray()).Length >= 4);
        stored.Should().Equal("Skyrim.esm", "Update.esm", "Dawnguard.esm", "ccinvented001-garden.esm");
        (await RunEveryEmitter(loadout)).Select(d => d.Id).Should().NotContain(new DiagnosticId("SilverModManager.Games.CreationEngine", number: 1), "every master of the Creation has a header");
    }

    private IFileHashesService Hashes => ServiceProvider.GetRequiredService<IFileHashesService>();

    // Gives up quietly at the deadline so the assertion after it reports what was stored, not a cancellation.
    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
    }
}
