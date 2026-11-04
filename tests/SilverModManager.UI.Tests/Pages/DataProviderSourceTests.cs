using DynamicData;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.App.UI.Pages.LibraryPage.Collections;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using NSubstitute;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Models;
using Microsoft.Extensions.Logging;
using SilverModManager.Sdk.Hashes;
using SilverModManager.UI.Tests.Windows.Xdnd;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// Library rows say where they came from in the Source column, on the row and on the file underneath it.
/// </summary>
public class DataProviderSourceTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly LoadoutId NoLoadout = LoadoutId.From(EntityId.From(0xBEEF));

    private LibraryFilter Filter() => new(NoLoadout, Provider.GetRequiredService<IGameRegistry>().LocateGameInstallations().First().Game);

    /// <summary>A library file for the filter's game, so the providers' slice holds it.</summary>
    private LibraryFile.New NewLibraryFile(ITransaction tx, out EntityId id, string fileName)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(fileName + Guid.NewGuid());
        var file = new LibraryFile.New(tx, out id)
        {
            FileName = fileName,
            Hash = bytes.xxHash3(),
            Size = Size.From((ulong)bytes.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = fileName },
        };
        LibraryDestinations.Write(tx, id, Destination.Of(Filter().Game.GameId));
        return file;
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

    /// <summary>Hands out the given logger for the local-file provider and everything else from the real container.</summary>
    private sealed class LoggerOverride(IServiceProvider inner, ILogger<LocalFileDataProvider> logger) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ILogger<LocalFileDataProvider>) ? logger : inner.GetService(serviceType);
    }

    [Fact]
    public async Task ARowForAFetchedFile_TracesItsId_AndThatItWasFetchedForAModlist()
    {
        using var tx = Connection.BeginTransaction();
        var file = NewLibraryFile(tx, out var id, "fetched.7z");
        _ = new LocalFile.New(tx, id) { LibraryFile = file, OriginalPath = "/somewhere/fetched.7z" };
        var row = new WabbajackModlistArchive.New(tx)
        {
            ModlistId = id,
            Identity = FileIdentity.XxHash64(Hash.From(0xFEEDUL)),
            Size = Size.Zero,
            Name = "fetched.7z",
        };
        tx.Add(row.Id, WabbajackModlistArchive.FetchedFiles, id);
        var result = await tx.Commit();
        var fileId = result.Remap(file).Id;

        var logger = new RecordingLoggerOf<LocalFileDataProvider>();
        var provider = new LocalFileDataProvider(new LoggerOverride(Provider, logger));

        await ShouldBeFrom(provider.ObserveLibraryItems(Filter()), fileId, ModSources.Wabbajack);

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Trace
            && e.Message.Contains(fileId.ToString()) && e.Message.Contains("fetchedForModlist=True"));
    }

    private async Task<WabbajackModlistCardViewModel> ModlistCard(string fileName, string? galleryUrl)
    {
        using var tx = Connection.BeginTransaction();
        var file = NewLibraryFile(tx, out var id, fileName);
        // A game type the app doesn't know, so the list shows whatever game the filter is for.
        var modlist = new WabbajackModlistLibraryFile.New(tx, id)
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
        if (galleryUrl is not null) tx.Add(modlist.Id, WabbajackModlistLibraryFile.GalleryDownloadUrl, galleryUrl);
        var result = await tx.Commit();

        var loadout = Filter().LoadoutId;
        return new WabbajackModlistCardViewModel(Provider, Substitute.For<IWindowManager>(), WorkspaceId.From(Guid.NewGuid()),
            WabbajackModlistLibraryFile.Load(Connection.Db, result.Remap(file).Id), loadout);
    }

    [Fact]
    public async Task AWabbajackModlistFromTheGallery_IsFromWabbajack()
    {
        var card = await ModlistCard("gallery list.wabbajack", "https://authored-files.wabbajack.org/A_list.wabbajack_ff");

        card.Source.Should().Be(ModSources.Wabbajack);
        card.Kind.Should().Be("Wabbajack modlist");
    }

    [Fact]
    public async Task AWabbajackModlistAddedFromDisk_IsFromALocalFile()
    {
        var card = await ModlistCard("disk list.wabbajack", galleryUrl: null);

        card.Source.Should().Be(ModSources.LocalFile);
        card.Kind.Should().Be("Wabbajack modlist", "a list added from disk is still recognisably a modlist");
    }
}
