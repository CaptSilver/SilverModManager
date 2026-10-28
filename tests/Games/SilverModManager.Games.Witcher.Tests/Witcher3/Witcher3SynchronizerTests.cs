using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Games.Witcher.Witcher3.Models;
using SilverModManager.Games.Witcher.Witcher3.SortOrder;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Witcher.Tests.Witcher3;

public class Witcher3SynchronizerTests(ITestOutputHelper helper)
    : AIsolatedGameTest<Witcher3SynchronizerTests, Witcher3Game>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddWitcherGames()
        .AddUniversalGameLocator<Witcher3Game>(new Version("4.0"), locationsFileSystem: IsolatedKnownFolders.Create);

    [Fact]
    public async Task AnApplyBuildsTheLoadOrder_SoAReplacedModsSettingsGetsItsNotice()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));

        // The Load Order of a loadout nobody has reconciled yet does not exist; Vortex wrote the file since the last apply.
        var variety = Game.SortOrderManager.GetSortOrderVarieties().OfType<WitcherModSortOrderVariety>().Single();
        await variety.DeleteSortOrder(variety.GetSortOrderIdFor(loadout.LoadoutId, Connection.Db).Value);
        variety.GetSortOrderIdFor(loadout.LoadoutId, Connection.Db).HasValue.Should().BeFalse();
        await GameInstallation.Locations.ToAbsolutePath(WitcherConstants.W3ModsSettings)
            .WriteAllBytesAsync(new UTF8Encoding(false).GetBytes("[mod0000_MergedFiles]\nPriority=1\n[modFromVortex]\nPriority=2\nVK=1\n"));

        await Synchronizer.Synchronize(Refresh(loadout));

        WitcherModsSettingsNotice.All(Connection.Db).Should().ContainSingle();
    }
}
