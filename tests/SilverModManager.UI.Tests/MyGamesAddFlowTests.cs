using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Pages.MyGames;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests;

/// <summary>
/// What the add-game flow asks the database after a game is managed. The query is a plain function so the
/// caller can run it on the pool; these tests pin its answers.
/// </summary>
public class MyGamesAddFlowTests(IServiceProvider provider) : AUiTest(provider)
{
    private async Task<Loadout.ReadOnly> SeedWithExternalFiles(int count)
    {
        var loadout = await SeedLoadoutRow(GameStore.Unknown, "add-flow-" + Guid.NewGuid().ToString("N"));
        if (count == 0) return loadout;

        using var tx = Connection.BeginTransaction();
        _ = new LoadoutOverridesGroup.New(tx, out var overrides)
        {
            OverridesForId = loadout,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, overrides)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, overrides) { Name = "Overrides", LoadoutId = loadout.Id },
            },
        };
        for (var i = 0; i < count; i++)
        {
            var path = new GamePath(LocationId.Game, $"external/file{i}.ini");
            _ = new LoadoutFile.New(tx, out var fileId)
            {
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, fileId)
                {
                    LoadoutItem = new LoadoutItem.New(tx, fileId)
                    {
                        LoadoutId = loadout.Id,
                        ParentId = LoadoutItemGroupId.From(overrides),
                        Name = path.Path.FileName,
                    },
                    TargetPath = path.ToGamePathParentTuple(loadout.LoadoutId.Value),
                },
                Hash = Hash.From(0xC0FFEE),
                Size = Size.From(10),
            };
        }
        
        await tx.Commit();
        return Loadout.Load(Connection.Db, loadout.LoadoutId);
    }

    [Fact]
    public async Task DescribeFreshLoadout_ReturnsExternalChangesAndCoverageInOnePass()
    {
        var loadout = await SeedWithExternalFiles(2);
        var installation = LocateStubbedInstallation<Cyberpunk2077Game>(Provider);

        var described = MyGamesViewModel.DescribeFreshLoadout(Connection, loadout, installation, _ => false);

        described.Changes.Should().HaveCount(2);
        described.BuildCovered.Should().BeFalse();
    }

    [Fact]
    public async Task DescribeFreshLoadout_WithoutAnOverridesGroup_HasNoChangesAndPassesTheInstallationToTheCoverageCheck()
    {
        var loadout = await SeedWithExternalFiles(0);
        var installation = LocateStubbedInstallation<Cyberpunk2077Game>(Provider);
        GameInstallation? asked = null;

        var described = MyGamesViewModel.DescribeFreshLoadout(Connection, loadout, installation, i => { asked = i; return true; });

        described.Changes.Should().BeEmpty();
        described.BuildCovered.Should().BeTrue();
        asked.Should().BeSameAs(installation);
    }
}
