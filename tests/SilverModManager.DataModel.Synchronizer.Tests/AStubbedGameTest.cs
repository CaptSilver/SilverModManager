using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Games.TestFramework;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// Base for tests that need the stubbed game: it has Game, Preferences and Saves locations
/// and goes through the unmodified <see cref="Abstractions.Loadouts.Synchronizers.DefaultSynchronizer"/>.
/// </summary>
public abstract class AStubbedGameTest<TTest>(ITestOutputHelper helper) : AIsolatedGameTest<TTest, StubbedGame>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddGame<StubbedGame>()
            .AddUniversalGameLocator<StubbedGame>(Version.Parse("0.0.0"));
    }

    /// <summary>Moves the adopted file's row out of the Overrides into a mod group, where a deletion is not the player's to take in.</summary>
    protected async Task MoveIntoAMod(Loadout.ReadOnly loadout, string fileName)
    {
        var row = LoadoutItem.FindByLoadout(Connection.Db, loadout).OfTypeLoadoutItemWithTargetPath()
            .Single(f => ((GamePath)f.TargetPath).Path.FileName.ToString() == fileName);
        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var groupId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, groupId) { Name = "Some mod", LoadoutId = loadout.Id },
        };
        tx.Add(row.Id, LoadoutItem.Parent, groupId);
        await tx.Commit();
    }
}
