using System.IO.Compression;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests;

public class LoadoutManagerInstallItemsTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<LoadoutManagerInstallItemsTests>(helper)
{
    private static readonly string[] ModNames = ["First mod", "Second mod", "Third mod"];

    [Fact]
    public async Task SeveralItemsInstallIntoACollectionInOneTransactionAtTheEndOfTheOrder()
    {
        var loadout = await CreateLoadout();
        var collection = await CreateCollection(loadout.LoadoutId, "Tweaks");
        await AddOrderedGroups(loadout.LoadoutId, "A", "B");
        var items = await AddModArchives(ModNames);
        var before = Connection.Db.BasisTxId;

        var installed = await LoadoutManager.InstallItems(items, loadout.LoadoutId, LoadoutItemGroupId.From(collection.Id));

        installed.Should().HaveCount(3);
        installed.Select(static group => group.AsLoadoutItem().ParentId.Value).Should().OnlyContain(parent => parent == collection.Id);
        Connection.Db.BasisTxId.Value.Should().Be(before.Value + 1, "all the installs are one transaction");

        var rows = LoadoutItemGroupPriority.FindByLoadout(Connection.Db, loadout.LoadoutId).OrderBy(static row => row.Priority).ToArray();
        rows.Select(static row => (ulong)row.Priority.Value).Should().Equal(1UL, 2UL, 3UL, 4UL, 5UL);
        rows.Select(static row => row.TargetId).Skip(2).Should().Equal(installed.Select(static group => group.LoadoutItemGroupId), "new installs keep the order they were given in");
    }

    [Fact]
    public async Task AnItemThatCannotBeInstalledInstallsNoneAndNamesItself()
    {
        var loadout = await CreateLoadout();
        var collection = await CreateCollection(loadout.LoadoutId, "Tweaks");
        var good = await AddModArchives(ModNames[..2]);
        var broken = await AddLibraryItem(await WriteFile("not-a-mod.txt"));
        var before = Connection.Db.BasisTxId;

        var install = async () => await LoadoutManager.InstallItems([good[0], broken, good[1]], loadout.LoadoutId, LoadoutItemGroupId.From(collection.Id));

        await install.Should().ThrowAsync<Exception>().WithMessage("*not-a-mod.txt*");
        Connection.Db.BasisTxId.Should().Be(before, "the failed transaction wrote nothing");
        LoadoutItem.All(Connection.Db).Where(item => LoadoutItem.Parent.TryGetValue(item, out var parent) && parent == collection.Id).Should().BeEmpty();
        LibraryLinkedLoadoutItem.All(Connection.Db).Should().BeEmpty();
    }

    // A .archive file under archive/pc/mod is what Cyberpunk's installers take; the content differs per mod so the files don't dedupe.
    private async Task<LibraryItem.ReadOnly[]> AddModArchives(params string[] names)
    {
        var items = new List<LibraryItem.ReadOnly>();
        foreach (var name in names)
        {
            var path = TemporaryFileManager.CreateFolder().Path.Combine($"{name}.zip");
            await using (var stream = path.Create())
            {
                using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
                await using var entry = zip.CreateEntry($"archive/pc/mod/{name}.archive").Open();
                await entry.WriteAsync(System.Text.Encoding.UTF8.GetBytes(name));
            }
            items.Add(await AddLibraryItem(path));
        }
        return items.ToArray();
    }

    private async Task<LibraryItem.ReadOnly> AddLibraryItem(AbsolutePath path)
        => (await ServiceProvider.GetRequiredService<ILibraryService>().AddLocalFile(path, new GameAcquisition(GameInstallation.Game.GameId))).AsLibraryFile().AsLibraryItem();

    private async Task<AbsolutePath> WriteFile(string fileName)
    {
        var path = TemporaryFileManager.CreateFolder().Path.Combine(fileName);
        await path.WriteAllTextAsync("not an archive");
        return path;
    }

    private async Task AddOrderedGroups(LoadoutId loadout, params string[] names)
    {
        using var tx = Connection.BeginTransaction();
        foreach (var (name, i) in names.Select((name, i) => (name, i)))
        {
            var group = AddEmptyGroup(tx, loadout, name);
            _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout, TargetId = group, Priority = ConflictPriority.From((ulong)(i + 1)) };
        }
        await tx.Commit();
    }
}
