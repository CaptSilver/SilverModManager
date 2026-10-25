using DynamicData;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// Library rows say where they came from in the Source column, on the row and on the file underneath it.
/// </summary>
public class DataProviderSourceTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly LoadoutId NoLoadout = LoadoutId.From(EntityId.From(0xBEEF));

    private LibraryFilter Filter() => new(NoLoadout, Provider.GetRequiredService<IGameRegistry>().LocateGameInstallations().First().Game);

    private static LibraryFile.New NewLibraryFile(ITransaction tx, out EntityId id, string fileName)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(fileName + Guid.NewGuid());
        return new LibraryFile.New(tx, out id)
        {
            FileName = fileName,
            Hash = bytes.xxHash3(),
            Size = Size.From((ulong)bytes.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = fileName },
        };
    }

    private async Task ShouldBeFrom(IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> changes, EntityId id, string source)
    {
        using var rows = changes.AsObservableCache();
        await Eventually(() => rows.Lookup(id).HasValue.Should().BeTrue());
        var row = rows.Lookup(id).Value;
        SourceOf(row).Should().Be(source);

        using var children = row.ChildrenObservable.AsObservableCache();
        await Eventually(() => children.Count.Should().Be(1));
        SourceOf(children.Items.Single()).Should().Be(source);
    }

    private static string SourceOf(CompositeItemModel<EntityId> row) => row.Get<StringComponent>(SharedColumns.Source.ComponentKey).Value.Value;

    [Fact]
    public async Task ALocalFile_IsFromALocalFile()
    {
        using var tx = Connection.BeginTransaction();
        var file = NewLibraryFile(tx, out var id, "my mod.7z");
        _ = new LocalFile.New(tx, id) { LibraryFile = file, OriginalPath = "/somewhere/my mod.7z" };
        var result = await tx.Commit();

        await ShouldBeFrom(new LocalFileDataProvider(Provider).ObserveLibraryItems(Filter()), result.Remap(file).Id, ModSources.LocalFile);
    }

    [Fact]
    public async Task AManuallyCreatedArchive_IsFromALocalFile()
    {
        using var tx = Connection.BeginTransaction();
        var file = NewLibraryFile(tx, out var id, "bundled files");
        _ = new ManuallyCreatedArchive.New(tx, id)
        {
            LibraryArchive = new LibraryArchive.New(tx, id) { LibraryFile = file, IsArchive = true },
            Source = ManuallyCreatedArchive.CreationSource.CollectionBundled,
        };
        var result = await tx.Commit();

        await ShouldBeFrom(new ManuallyCreatedArchiveDataProvider(Provider).ObserveLibraryItems(Filter()), result.Remap(file).Id, ModSources.LocalFile);
    }

    [Fact]
    public async Task AWabbajackModlist_IsFromWabbajack()
    {
        using var tx = Connection.BeginTransaction();
        var file = NewLibraryFile(tx, out var id, "a list.wabbajack");
        // A game type the app doesn't know, so the list shows whatever game the filter is for.
        _ = new WabbajackModlistLibraryFile.New(tx, id)
        {
            LibraryFile = file,
            Name = "A list",
            Author = "Someone",
            Version = "1.0",
            GameType = "NoSuchGame",
            ArchiveCount = 0,
            ArchiveSize = Size.Zero,
            InstallSize = Size.Zero,
            IsNsfw = false,
        };
        var result = await tx.Commit();

        using var rows = new WabbajackModlistDataProvider(Provider).ObserveLibraryItems(Filter()).AsObservableCache();
        await Eventually(() => rows.Lookup(result.Remap(file).Id).HasValue.Should().BeTrue());
        SourceOf(rows.Lookup(result.Remap(file).Id).Value).Should().Be(ModSources.Wabbajack);
    }
}
