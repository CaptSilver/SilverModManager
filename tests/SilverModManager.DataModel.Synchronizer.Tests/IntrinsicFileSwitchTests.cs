using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A generated file outside the game folder is rendered for the loadout being switched to, even when a copy
/// from before the game was managed is on disk: the reset that precedes the switch puts that copy back.
/// </summary>
public class IntrinsicFileSwitchTests(ITestOutputHelper helper) : AIsolatedGameTest<IntrinsicFileSwitchTests, ModListingStubbedGame>(helper)
{
    private AbsolutePath Listing => GameInstallation.Locations.ToAbsolutePath(ModListingStubbedGame.ListingPath);

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        ModListingStubbedGame.ResetIngested();
        return base.AddServices(services)
            .AddGame<ModListingStubbedGame>()
            .AddUniversalGameLocator<ModListingStubbedGame>(Version.Parse("0.0.0"));
    }

    private async Task AddMod(Loadout.ReadOnly loadout, string fileName)
    {
        using var tx = Connection.BeginTransaction();
        await AddModAsync(tx, [(RelativePath)fileName], loadout.LoadoutId, fileName);
        await tx.Commit();
    }

    [Fact]
    public async Task TheIncomingLoadoutsRenderReplacesTheRestoredOriginal()
    {
        Listing.Parent.CreateDirectory();
        await Listing.WriteAllTextAsync("the file the user had before\n");

        var loadoutA = await CreateLoadout();
        await AddMod(loadoutA, "a.mod");
        var loadoutB = await CreateLoadout();
        await AddMod(loadoutB, "b.mod");

        await Synchronizer.Synchronize(Refresh(loadoutA));
        (await Listing.ReadAllTextAsync()).Should().Be("a.mod\n");

        await Synchronizer.Synchronize(Refresh(loadoutB));
        (await Listing.ReadAllTextAsync()).Should().Be("b.mod\n", "the switch writes B's listing over the copy the reset put back");
    }
}
