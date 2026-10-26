using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.DataModel.Synchronizer;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// An apply the synchronizer refuses up front is fixed in the Health Check, so the refusal carries the way there
/// whichever entry point started the apply.
/// </summary>
public class SynchronizeRefusalActionTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<SynchronizeRefusalActionTests>(helper)
{
    // A path that climbs out of its location is refused before anything is written.
    private async Task<LoadoutId> LoadoutWithATraversalPath()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        var loadout = await CreateLoadout();

        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, "Mod");
        AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, "../outside.txt"), "content");
        await tx.Commit();
        return loadout.LoadoutId;
    }

    [Fact]
    public async Task Synchronize_RefusalOpensTheHealthCheckOfTheLoadout()
    {
        var loadoutId = await LoadoutWithATraversalPath();

        var act = () => SynchronizerService.Synchronize(loadoutId);

        var refusal = (await act.Should().ThrowAsync<SynchronizerPreflightException>()).Which;
        refusal.Action.Should().Be(new OpenHealthCheck(loadoutId));
    }

    [Fact]
    public async Task SynchronizeFromLaunchSession_RefusalOpensTheHealthCheckOfTheLoadout()
    {
        var loadoutId = await LoadoutWithATraversalPath();

        var act = () => ((SynchronizerService)SynchronizerService).SynchronizeFromLaunchSession(loadoutId);

        var refusal = (await act.Should().ThrowAsync<SynchronizerPreflightException>()).Which;
        refusal.Action.Should().Be(new OpenHealthCheck(loadoutId));
    }
}
