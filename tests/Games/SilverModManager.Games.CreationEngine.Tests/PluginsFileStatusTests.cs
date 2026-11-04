using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;
using VR = SilverModManager.Games.CreationEngine.SkyrimVR.SkyrimVR;

namespace SilverModManager.Games.CreationEngine.Tests;

/// <summary>
/// The plugins list is generated at every apply. The Apply button must not read it as pending work when rendering it
/// again gives the bytes the last apply wrote, or any apply that left a file out would keep the button lit.
/// </summary>
public class PluginsFileStatusTests(ITestOutputHelper helper) : AIsolatedGameTest<PluginsFileStatusTests, VR>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<VR>(new Version("1.4.15"), locationsFileSystem: IsolatedKnownFolders.Create);

    [Fact]
    public async Task ARenameAfterAnApplyLeavesNothingToApplyForThePluginsList()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));

        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(loadout.Id, Loadout.Name, "renamed");
            await tx.Commit();
        }

        var fresh = Refresh(loadout);
        var metadata = fresh.Installation;
        var evaluation = await Synchronizer.EvaluateSynchronize(fresh, Synchronizer.GetLastScannedDiskState(metadata), Synchronizer.GetDiskStateForGame(metadata));

        evaluation.Needed.Should().BeFalse("the plugins list renders to what the apply wrote");
    }
}
