using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.DataModel.SchemaVersions.Tests;

/// <summary>
/// Collections resolving the same mod at the same time each wrote their own mod page row, because the page's uid is
/// indexed but not unique. Rows with an equal uid are folded into the oldest one, and everything that pointed at a
/// younger row is pointed at it.
/// </summary>
public class DuplicateNexusPageMigrationTests
{
    private static readonly NexusModsGameId Game = NexusModsGameId.From(1704);
    private static readonly ModUid Mod = new(ModId.From(555), Game);
    private static readonly FileUid SharedFile = new(FileId.From(7001), Game);
    private static readonly FileUid OnlyOnYoungerPage = new(FileId.From(7002), Game);

    private static NexusModsModPageMetadata.New NewPage(ITransaction tx, ModUid uid, string name)
        => new(tx)
        {
            Uid = uid,
            Name = name,
            GameDomain = GameDomain.From("skyrimspecialedition"),
            UpdatedAt = DateTime.UnixEpoch,
            DataUpdatedAt = DateTime.UnixEpoch,
        };

    private static NexusModsFileMetadata.New NewFile(ITransaction tx, FileUid uid, NexusModsModPageMetadata.New page)
        => new(tx)
        {
            Uid = uid,
            Name = "A file",
            Version = "1",
            UploadedAt = DateTime.UnixEpoch,
            ModPageId = page,
        };

    private static NexusModsLibraryItem.New NewItem(ITransaction tx, EntityId file, EntityId page)
        => new(tx, out var id)
        {
            FileMetadataId = file,
            ModPageMetadataId = page,
            LibraryItem = new LibraryItem.New(tx, id) { Name = "item " + id },
        };

    private sealed record Seed(EntityId OldPage, EntityId YoungPage, EntityId OldShared, EntityId YoungShared, EntityId YoungOnly, EntityId OldItem, EntityId YoungItem, EntityId YoungOnlyItem, EntityId OtherPage);

    // Each caller wrote in its own transaction, so the rows are separate commits, oldest first.
    private static async Task<Seed> SeedDuplicates(IConnection connection)
    {
        EntityId oldPage, oldShared, oldItem;
        using (var tx = connection.BeginTransaction())
        {
            var page = NewPage(tx, Mod, "Older row");
            var file = NewFile(tx, SharedFile, page);
            var item = NewItem(tx, file.Id, page.Id);
            var result = await tx.Commit();
            (oldPage, oldShared, oldItem) = (result.Remap(page).Id, result.Remap(file).Id, result.Remap(item).Id);
        }

        EntityId youngPage, youngShared, youngOnly, youngItem, youngOnlyItem, otherPage;
        using (var tx = connection.BeginTransaction())
        {
            var page = NewPage(tx, Mod, "Younger row");
            var shared = NewFile(tx, SharedFile, page);
            var only = NewFile(tx, OnlyOnYoungerPage, page);
            var item = NewItem(tx, shared.Id, page.Id);
            var onlyItem = NewItem(tx, only.Id, page.Id);
            var other = NewPage(tx, new ModUid(ModId.From(556), Game), "A different mod");
            var result = await tx.Commit();
            (youngPage, youngShared, youngOnly) = (result.Remap(page).Id, result.Remap(shared).Id, result.Remap(only).Id);
            (youngItem, youngOnlyItem, otherPage) = (result.Remap(item).Id, result.Remap(onlyItem).Id, result.Remap(other).Id);
        }

        return new Seed(oldPage, youngPage, oldShared, youngShared, youngOnly, oldItem, youngItem, youngOnlyItem, otherPage);
    }

    [Fact]
    public async Task MigrateAll_TwoPagesForOneMod_KeepsTheOlderPageAndPointsEverythingAtIt()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var seed = await SeedDuplicates(connection);

        await provider.GetRequiredService<MigrationService>().MigrateAll();

        var db = connection.Db;
        NexusModsModPageMetadata.FindByUid(db, Mod).Select(p => p.Id).Should().Equal([seed.OldPage]);
        NexusModsModPageMetadata.Load(db, seed.YoungPage).IsValid().Should().BeFalse();
        db.ReferencesTo(seed.YoungPage).Should().BeEmpty("nothing may point at a page that no longer exists");

        NexusModsFileMetadata.FindByUid(db, SharedFile).Select(f => f.Id).Should().Equal([seed.OldShared], "a file both pages held is one file");
        NexusModsFileMetadata.Load(db, seed.YoungShared).IsValid().Should().BeFalse();
        db.ReferencesTo(seed.YoungShared).Should().BeEmpty();

        var moved = NexusModsFileMetadata.FindByUid(db, OnlyOnYoungerPage).Single();
        moved.Id.Should().Be(seed.YoungOnly);
        moved.ModPageId.Value.Should().Be(seed.OldPage, "a file only the younger page had moves to the surviving page");

        var oldItem = NexusModsLibraryItem.Load(db, seed.OldItem);
        (oldItem.FileMetadataId.Value, oldItem.ModPageMetadataId.Value).Should().Be((seed.OldShared, seed.OldPage));
        var youngItem = NexusModsLibraryItem.Load(db, seed.YoungItem);
        (youngItem.FileMetadataId.Value, youngItem.ModPageMetadataId.Value).Should().Be((seed.OldShared, seed.OldPage), "the item stays in the library, on the surviving rows");
        var youngOnlyItem = NexusModsLibraryItem.Load(db, seed.YoungOnlyItem);
        (youngOnlyItem.FileMetadataId.Value, youngOnlyItem.ModPageMetadataId.Value).Should().Be((seed.YoungOnly, seed.OldPage));

        NexusModsModPageMetadata.Load(db, seed.OtherPage).IsValid().Should().BeTrue("another mod's page is not a duplicate");
    }

    [Fact]
    public async Task MigrateAll_NoDuplicatePages_LeavesThePagesAlone()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        EntityId page;
        using (var tx = connection.BeginTransaction())
        {
            var p = NewPage(tx, Mod, "Only row");
            NewFile(tx, SharedFile, p);
            page = (await tx.Commit()).Remap(p).Id;
        }

        await provider.GetRequiredService<MigrationService>().MigrateAll();

        var db = connection.Db;
        NexusModsModPageMetadata.FindByUid(db, Mod).Select(p => p.Id).Should().Equal([page]);
        NexusModsFileMetadata.FindByUid(db, SharedFile).Single().ModPageId.Value.Should().Be(page);
    }

    [Fact]
    public async Task MigrateAll_SurvivorLacksPictures_TakesEachFromTheOldestDuplicateThatHasIt()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        EntityId survivor;
        using (var tx = connection.BeginTransaction())
        {
            var page = NewPage(tx, Mod, "No pictures");
            survivor = (await tx.Commit()).Remap(page).Id;
        }

        // Two duplicates with different values: the survivor must end up with exactly the older duplicate's.
        foreach (var n in new[] { 1, 2 })
        {
            using var tx = connection.BeginTransaction();
            var page = NewPage(tx, Mod, "Duplicate " + n);
            page.ThumbnailUri = new Uri($"https://example.invalid/thumb{n}.png");
            page.FullSizedPictureUri = new Uri($"https://example.invalid/full{n}.png");
            await tx.Commit();
        }

        await provider.GetRequiredService<MigrationService>().MigrateAll();

        var merged = NexusModsModPageMetadata.FindByUid(connection.Db, Mod).Single();
        merged.Id.Should().Be(survivor);
        var db = connection.Db;
        db.Get(survivor).Where(d => d.A == db.AttributeCache.GetAttributeId(NexusModsModPageMetadata.ThumbnailUri.Id)).Should().HaveCount(1);
        NexusModsModPageMetadata.ThumbnailUri.Get(merged).Should().Be(new Uri("https://example.invalid/thumb1.png"));
        db.Get(survivor).Where(d => d.A == db.AttributeCache.GetAttributeId(NexusModsModPageMetadata.FullSizedPictureUri.Id)).Should().HaveCount(1);
        NexusModsModPageMetadata.FullSizedPictureUri.Get(merged).Should().Be(new Uri("https://example.invalid/full1.png"));
    }
}
