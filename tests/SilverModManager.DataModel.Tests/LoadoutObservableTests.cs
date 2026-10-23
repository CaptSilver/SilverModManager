using FluentAssertions;

using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Loadouts;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.DataModel.Tests;

public class LoadoutObservableTests(IServiceProvider provider) : AGameTest<Cyberpunk2077Game>(provider)
{
    [Fact]
    public async Task DeletingAModShouldUpdateTheLoadout()
    {
        var loadout = await CreateLoadout();

        using var tx = Connection.BeginTransaction();

        var group = new LoadoutItemGroup.New(tx, out var groupId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, groupId)
            {
                LoadoutId = loadout,
                Name = "Test Group",
            }
        };
        
        var fileId = tx.TempId();
        var file = new LoadoutItem.New(tx, fileId)
        {
            LoadoutId = loadout,
            Name = "Test Mod",
            ParentId = groupId,
        };

        var result = await tx.Commit();

        var lastTimestamp = DateTimeOffset.UtcNow;
        var lastId = EntityId.From(0);
        using var loadouts = LoadoutQueries2.RevisionsWithChildUpdates(Connection, loadout).Subscribe(l =>
        {
            lastId = l.Id;
            lastTimestamp = DateTimeOffset.UtcNow;
        });

        fileId = result[fileId];
        groupId = result[groupId];

        await Connection.FlushQueries(); 
        lastId.Should().Be(loadout.Id);
        var originalTimestamp = lastTimestamp;

        // Delete a file and the row should update
        using var tx2 = Connection.BeginTransaction();
        tx2.Delete(fileId, false);
        var result2 = await tx2.Commit();

        await Connection.FlushQueries();
        lastId.Should().Be(loadout.Id);
        lastTimestamp.Should().BeAfter(originalTimestamp);
    }
}
