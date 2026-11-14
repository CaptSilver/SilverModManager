using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Games.CreationEngine.Loot;
using SilverModManager.Games.CreationEngine.SortOrder;
using SilverModManager.Games.CreationEngine.Tests.SkyrimSE;
using SilverModManager.Games.CreationEngine.TestHelpers;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using NexusMods.Paths;

namespace SilverModManager.Games.CreationEngine.Tests.SortOrder;

/// <summary>
/// Sorting with the store the app registers, which keeps downloaded masterlists: it always asks the server for a
/// newer one, and an unreachable server leaves the bundled copy in use.
/// </summary>
public class MasterlistRefreshOnSortTests(ITestOutputHelper helper)
    : AIsolatedGameTest<MasterlistRefreshOnSortTests, CreationEngine.SkyrimSE.SkyrimSE>(helper)
{
    private readonly CountingFailingHandler _handler = new();

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<CreationEngine.SkyrimSE.SkyrimSE>(new Version("1.6.1170"), locationsFileSystem: IsolatedKnownFolders.Create)
        .AddSingleton(new HttpClient(_handler))
        .AddSingleton(s => new LootDataDirectory(s.GetRequiredService<TemporaryFileManager>().CreateFolder().Path));

    [Fact]
    public async Task SortAsync_AsksTheServerForTheMasterlist_AndSortsWithTheBundledCopyWhenItFails()
    {
        var loadout = await CreateLoadout();
        await SkyrimBaseline.SeedSkyrimEsm(ServiceProvider, loadout);
        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout.LoadoutId, "Mods");
            foreach (var plugin in new[] { "A.esp", "B.esp" })
                _ = AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, $"Data/{plugin}"));
            await tx.Commit();
        }
        var variety = new PluginSortOrderVariety(ServiceProvider, Game);
        var id = await variety.GetOrCreateSortOrderFor(loadout.LoadoutId, loadout.LoadoutId);
        await variety.ReconcileSortOrder(id);

        var outcome = await ((ILootSortSource)variety).SortAsync(id);

        _handler.Requests.Should().BeGreaterThan(0);
        outcome.Succeeded.Should().BeTrue();
        outcome.MasterlistRevision.Should().NotBeNullOrEmpty();
    }

    private sealed class CountingFailingHandler : HttpMessageHandler
    {
        private int _requests;
        public int Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            throw new HttpRequestException("offline");
        }
    }
}
