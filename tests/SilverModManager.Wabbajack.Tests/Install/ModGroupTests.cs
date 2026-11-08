using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Tests.Install;

public class ModGroupTests(ITestOutputHelper helper) : InstallTestBase<ModGroupTests>(helper)
{
    [Fact]
    public async Task AModGroupRecordsItsFolderName()
    {
        var loadout = await CreateLoadout();
        using var tx = Connection.BeginTransaction();
        var group = new WabbajackModGroup.New(tx, out var id)
        {
            Folder = "SkyUI 5.2SE",
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { Name = "SkyUI", LoadoutId = loadout.LoadoutId },
            },
        };
        var result = await tx.Commit();

        var model = WabbajackModGroup.Load(Connection.Db, result.Remap(group).Id);
        model.Folder.Should().Be("SkyUI 5.2SE");
        WabbajackModGroup.FindByFolder(Connection.Db, "SkyUI 5.2SE").Select(g => g.Id).Should().Equal(model.Id);
    }
}
