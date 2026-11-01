using System.Text;
using SilverModManager.Sdk.Hashes;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using SilverModManager.Abstractions.Downloads;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Paths.Utilities;
using SilverModManager.Sdk;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using Xunit;
using SilverModManager.Sdk.Library;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.DataModel.Tests;

public class LibraryServiceTests : ACyberpunkIsolatedGameTest<LibraryServiceTests>
{
    private readonly ILibraryService _libraryService;
    private readonly IFileStore _fileStore;
    private readonly IConnection _connection;

    public LibraryServiceTests(ITestOutputHelper helper) : base(helper)
    {
        _libraryService = ServiceProvider.GetRequiredService<ILibraryService>();
        _fileStore = ServiceProvider.GetRequiredService<IFileStore>();
        _connection = ServiceProvider.GetRequiredService<IConnection>();
    }

    [Fact]
    public async Task Test_Issue3156()
    {
        const string fileName = "zip-with-encoding.zip";
        var archivePath = FileSystem.GetKnownPath(KnownPath.CurrentDirectory).Combine("Resources").Combine(fileName);
        archivePath.FileExists.Should().BeTrue();

        // The entry names are GBK bytes without the zip's UTF-8 flag. The zip spec says such names are CP437, so
        // they import under their CP437 spelling instead of failing the whole archive.
        var localFile = await _libraryService.AddLocalFile(archivePath, new GameAcquisition(GameInstallation.Game.GameId));
        localFile.AsLibraryFile().TryGetAsLibraryArchive(out var libraryArchive).Should().BeTrue();
        libraryArchive.IsValid().Should().BeTrue();

        var paths = libraryArchive.Children.Select(x => x.Path.ToString()).ToArray();
        paths.Should().Contain("[FS]Ningqi's Forever Spring Costume/manifest.json");
        paths.Should().Contain("[FS]Ningqi's Forever Spring Costume/Hairs/│√╛╒╗¿┬├/│√╛╒╗¿┬├╨í╛φ╖ó/hair.json");
        paths.Should().NotContain(p => p.Contains('�'));
    }

    [Fact]
    public async Task Test_Issue3003()
    {
        const string fileName = "zip-with-spaces.zip";
        var archivePath = FileSystem.GetKnownPath(KnownPath.CurrentDirectory).Combine("Resources").Combine(fileName);
        archivePath.FileExists.Should().BeTrue();

        var localFile = await _libraryService.AddLocalFile(archivePath, new GameAcquisition(GameInstallation.Game.GameId));
        localFile.AsLibraryFile().TryGetAsLibraryArchive(out var libraryArchive).Should().BeTrue();
        libraryArchive.IsValid().Should().BeTrue();

        libraryArchive.Children.Should().Contain(x => x.Path.ToString() == "bar/foo/bar.txt");
    }

    [Fact]
    public async Task KnownNestedArchiveIsExtracted()
    {
        // Archives with known extensions should be extracted and have their contents analyzed
        
        var nestedArchivePath = FileSystem.GetKnownPath(KnownPath.CurrentDirectory).Combine("Resources").Combine("nested_archive.zip");
        var libraryItem = await _libraryService.AddLocalFile(nestedArchivePath, new GameAcquisition(GameInstallation.Game.GameId));
        libraryItem.AsLibraryFile().TryGetAsLibraryArchive(out var archive).Should().BeTrue();
        
        // find the nested archive
        var nestedArchive = archive.Children.FirstOrOptional(x => x.Path.FileName == "someNestedArchive.7z");
        nestedArchive.HasValue.Should().BeTrue();
        
        // Check if the nested archive has associated Archive data
        var nestedLibraryFile = nestedArchive.Value.AsLibraryFile();
        nestedLibraryFile.TryGetAsLibraryArchive(out var nestedLibraryArchive).Should().BeTrue();
        
        // print the contents of the parent and nested archive
        StringBuilder verifyText = new();
        verifyText.AppendLine("Parent Archive:");
        verifyText.Append(PrintArchiveContents(archive));
        verifyText.AppendLine("Nested Archive:");
        verifyText.Append(PrintArchiveContents(nestedLibraryArchive));
        
        await Verify(verifyText.ToString());
    }
    
    
    [Fact]
    public async Task UnknownNestedArchiveIsNotExtracted()
    {
        // Archives with unknown extensions (likely to be valid mod files) should not be extracted and analyzed
        
        var nestedArchivePath = FileSystem.GetKnownPath(KnownPath.CurrentDirectory).Combine("Resources").Combine("nested_game_archive.zip");
        var libraryItem = await _libraryService.AddLocalFile(nestedArchivePath, new GameAcquisition(GameInstallation.Game.GameId));
        libraryItem.AsLibraryFile().TryGetAsLibraryArchive(out var archive).Should().BeTrue();
        
        // find the nested archive
        var nestedArchive = archive.Children.FirstOrOptional(x => x.Path.FileName == "SomeModFile.archive");
        nestedArchive.HasValue.Should().BeTrue();
        
        // Check if the nested archive has associated Archive data
        var nestedLibraryFile = nestedArchive.Value.AsLibraryFile();
        nestedLibraryFile.TryGetAsLibraryArchive(out _).Should().BeFalse();
        
        // File should be available in the file store
        var isStored = await _fileStore.HaveFile(FileIdentity.XxHash3(nestedLibraryFile.Hash));
        isStored.Should().BeTrue();
        
        // print the contents of the parent archive
        await Verify(PrintArchiveContents(archive));
    }
    
    
    private async ValueTask<string> PrintArchiveContents(LibraryArchive.ReadOnly archive)
    {
        var result = new StringBuilder();
        foreach (var entry in archive.Children.OrderBy(x => x.Path))
        {
            result.AppendLine($"{entry.Path} - {entry.AsLibraryFile().Size} -  {entry.AsLibraryFile().Hash} - Stored : {await _fileStore.HaveFile(FileIdentity.XxHash3(entry.AsLibraryFile().Hash))}");
        }

        return result.ToString();
    }

    [Fact]
    public async Task LoadoutsWithLibraryItem_ShouldReturnCorrectLoadouts()
    {
        // Arrange the DB
        // Create a new loadout
        var loadout = await CreateTestLoadout("Test Loadout");
        var collection = await CreateCollectionInLoadout(loadout, "Test Collection");
        
        // Act
        // Create a new library item (e.g., add a local file)
        var nestedArchivePath = FileSystem.GetKnownPath(KnownPath.CurrentDirectory)
            .Combine("Resources")
            .Combine("nested_archive.zip");
        var libraryItem = (await _libraryService.AddLocalFile(nestedArchivePath, new GameAcquisition(GameInstallation.Game.GameId))).AsLibraryFile().AsLibraryItem();

        // Add it to our new loadout.
        await LoadoutManager.InstallItem(libraryItem, loadout.LoadoutId, parent: collection.AsLoadoutItemGroup().LoadoutItemGroupId);
        var loadouts = _libraryService.LoadoutsWithLibraryItem(libraryItem);

        // Assert that we have a single item.
        loadouts.Should().ContainSingle()
            .Which.loadout.LoadoutId.Should().Be(loadout.LoadoutId);
    }

    [Fact]
    public async Task ReplaceLinkedItemsInAllLoadouts_ShouldReplaceItemsAcrossLoadouts()
    {
        // Arrange
        // Create two loadouts
        var loadout1 = await CreateTestLoadout("Loadout1");
        var loadout2 = await CreateTestLoadout("Loadout2");
        var collection1 = await CreateCollectionInLoadout(loadout1, "Test Collection 1");
        var collection2 = await CreateCollectionInLoadout(loadout2, "Test Collection 2");
        
        // Create test files to use as our library items
        var firstArchivePath = FileSystem.GetKnownPath(KnownPath.CurrentDirectory)
            .Combine("Resources")
            .Combine("nested_archive.zip");
        var secondArchivePath = FileSystem.GetKnownPath(KnownPath.CurrentDirectory)
            .Combine("Resources")
            .Combine("nested_game_archive.zip");

        // Add files to library
        var oldItem1 = (await _libraryService.AddLocalFile(firstArchivePath, new GameAcquisition(GameInstallation.Game.GameId))).AsLibraryFile().AsLibraryItem();
        var oldItem2 = (await _libraryService.AddLocalFile(secondArchivePath, new GameAcquisition(GameInstallation.Game.GameId))).AsLibraryFile().AsLibraryItem();

        // Install old items in both loadouts
        await LoadoutManager.InstallItem(oldItem1, loadout1.LoadoutId, parent: collection1.AsLoadoutItemGroup().LoadoutItemGroupId);
        await LoadoutManager.InstallItem(oldItem2, loadout1.LoadoutId, parent: collection1.AsLoadoutItemGroup().LoadoutItemGroupId);
        await LoadoutManager.InstallItem(oldItem1, loadout2.LoadoutId, parent: collection2.AsLoadoutItemGroup().LoadoutItemGroupId);

        // Act
        // Replace both items across all loadouts
        var newItem1 = (await _libraryService.AddLocalFile(firstArchivePath, new GameAcquisition(GameInstallation.Game.GameId))).AsLibraryFile().AsLibraryItem(); // Same content but different LibraryItem instance
        var newItem2 = (await _libraryService.AddLocalFile(secondArchivePath, new GameAcquisition(GameInstallation.Game.GameId))).AsLibraryFile().AsLibraryItem();
        var replacements = new List<(LibraryItem.ReadOnly oldItem, LibraryItem.ReadOnly newItem)>
        {
            (oldItem1, newItem1),
            (oldItem2, newItem2),
        };

        var options = new ReplaceLibraryItemsOptions
        {
            IgnoreReadOnlyCollections = false,
        };
        var result = await _libraryService.ReplaceLinkedItemsInAllLoadouts(replacements, options);

        // Assert
        result.Should().Be(LibraryItemReplacementResult.Success);

        // Verify that the old items are no longer in any loadout
        var loadoutsWithOldItem1 = _libraryService.LoadoutsWithLibraryItem(oldItem1);
        var loadoutsWithOldItem2 = _libraryService.LoadoutsWithLibraryItem(oldItem2);

        loadoutsWithOldItem1.Should().BeEmpty();
        loadoutsWithOldItem2.Should().BeEmpty();

        // Verify that new items are now in the appropriate loadouts
        var loadoutsWithNewItem1 = _libraryService.LoadoutsWithLibraryItem(newItem1);
        var loadoutsWithNewItem2 = _libraryService.LoadoutsWithLibraryItem(newItem2);

        loadoutsWithNewItem1.Should().HaveCount(2)
            .And.Contain(l => l.loadout.LoadoutId == loadout1.LoadoutId)
            .And.Contain(l => l.loadout.LoadoutId == loadout2.LoadoutId);

        loadoutsWithNewItem2.Should().ContainSingle()
            .Which.loadout.LoadoutId.Should().Be(loadout1.LoadoutId);
    }

    [Fact]
    public async Task ReplaceLinkedItemsInAllLoadouts_ShouldRespectReadOnlyCollectionFilter()
    {
        // Arrange
        // Create two loadouts
        var loadout1 = await CreateTestLoadout("Loadout1");
        var loadout2 = await CreateTestLoadout("Loadout2");
        
        // Create a mutable collection in loadout1 and a read-only collection in loadout2
        var mutableCollection = await CreateCollectionInLoadout(loadout1, "Mutable Collection");
        var readOnlyCollection = await CreateCollectionInLoadout(loadout2, "ReadOnly Collection", isReadOnly: true);
        
        // Create test files to use as our library items
        var firstArchivePath = FileSystem.GetKnownPath(KnownPath.CurrentDirectory)
            .Combine("Resources")
            .Combine("nested_archive.zip");
        var secondArchivePath = FileSystem.GetKnownPath(KnownPath.CurrentDirectory)
            .Combine("Resources")
            .Combine("nested_game_archive.zip");

        // Add files to library
        var oldItem1 = (await _libraryService.AddLocalFile(firstArchivePath, new GameAcquisition(GameInstallation.Game.GameId))).AsLibraryFile().AsLibraryItem();
        var oldItem2 = (await _libraryService.AddLocalFile(secondArchivePath, new GameAcquisition(GameInstallation.Game.GameId))).AsLibraryFile().AsLibraryItem();

        // Install old items in both loadouts - one in mutable collection, one in read-only collection
        await LoadoutManager.InstallItem(oldItem1, loadout1.LoadoutId, parent: mutableCollection.AsLoadoutItemGroup().LoadoutItemGroupId);
        await LoadoutManager.InstallItem(oldItem2, loadout2.LoadoutId, parent: readOnlyCollection.AsLoadoutItemGroup().LoadoutItemGroupId);

        // Act
        // Create new items to replace the old ones
        var newItem1 = (await _libraryService.AddLocalFile(firstArchivePath, new GameAcquisition(GameInstallation.Game.GameId))).AsLibraryFile().AsLibraryItem(); // Same content but different LibraryItem instance
        var newItem2 = (await _libraryService.AddLocalFile(secondArchivePath, new GameAcquisition(GameInstallation.Game.GameId))).AsLibraryFile().AsLibraryItem();
        var replacements = new List<(LibraryItem.ReadOnly oldItem, LibraryItem.ReadOnly newItem)>
        {
            (oldItem1, newItem1),
            (oldItem2, newItem2),
        };

        // Set IgnoreReadOnlyCollections to true to ignore read-only collections
        var options = new ReplaceLibraryItemsOptions
        {
            IgnoreReadOnlyCollections = true,
        };
        var result = await _libraryService.ReplaceLinkedItemsInAllLoadouts(replacements, options);

        // Assert
        result.Should().Be(LibraryItemReplacementResult.Success);

        // Verify that the old item in mutable collection is no longer in any loadout
        var loadoutsWithOldItem1 = _libraryService.LoadoutsWithLibraryItem(oldItem1);
        loadoutsWithOldItem1.Should().BeEmpty("because item in mutable collection should be replaced");

        // Verify that the old item in read-only collection is still there (was not replaced)
        var loadoutsWithOldItem2 = _libraryService.LoadoutsWithLibraryItem(oldItem2);
        loadoutsWithOldItem2.Should().ContainSingle("because item in read-only collection should not be replaced")
            .Which.loadout.LoadoutId.Should().Be(loadout2.LoadoutId);

        // Verify that new item1 is now in mutable collection loadout
        var loadoutsWithNewItem1 = _libraryService.LoadoutsWithLibraryItem(newItem1);
        loadoutsWithNewItem1.Should().ContainSingle("because it should only replace the item in the mutable collection")
            .Which.loadout.LoadoutId.Should().Be(loadout1.LoadoutId);

        // Verify that new item2 is not in any loadout (because it wasn't installed due to read-only filter)
        var loadoutsWithNewItem2 = _libraryService.LoadoutsWithLibraryItem(newItem2);
        loadoutsWithNewItem2.Should().BeEmpty("because the item in read-only collection should not be replaced");
    }

    private async Task<Loadout.ReadOnly> CreateTestLoadout(string name)
    {
        using var tx = _connection.BeginTransaction();

        var metadata = new GameInstallMetadata.New(tx)
        {
            Path = GameInstallation.LocatorResult.Path.ToString(),
            Name = GameInstallation.Game.DisplayName,
            Store = GameInstallation.LocatorResult.Store,
            Game = GameInstallation.Game.GameId,
            GameId = GameInstallation.Game.NexusModsGameId.Value,
        };

        var loadoutNew = new Loadout.New(tx)
        {
            Name = name,
            ShortName = name,
            InstallationId = metadata,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
        };

        var result = await tx.Commit();
        return result.Remap(loadoutNew);
    }

    private async Task<CollectionGroup.ReadOnly> CreateCollectionInLoadout(
        LoadoutId loadoutId,
        string name = "My Mods",
        bool isReadOnly = false)
    {
        using var tx = _connection.BeginTransaction();

        var group = new CollectionGroup.New(tx, out var collectionId)
        {
            IsReadOnly = isReadOnly,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, collectionId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, collectionId)
                {
                    Name = name,
                    LoadoutId = loadoutId,
                },
            },
        };

        var result = await tx.Commit();
        return result.Remap(group);
    }

    private static readonly GameId DestinationGameA = GameId.From("library-service-destination-a");
    private static readonly GameId DestinationGameB = GameId.From("library-service-destination-b");
    private const string RecognisedFileName = "recognised-by-test.txt";

    /// <summary>Claims one file name for a game, the way a modlist names its game.</summary>
    private sealed class NamedGameRecogniser : ILibraryFileRecogniser
    {
        public ValueTask<Destination?> Recognise(ITransaction tx, LibraryFile.New file, AbsolutePath? extractedFolder, CancellationToken cancellationToken)
            => ValueTask.FromResult<Destination?>(file.FileName.ToString() == RecognisedFileName ? Destination.Of(DestinationGameB) : null);
    }

    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services).AddSingleton<ILibraryFileRecogniser, NamedGameRecogniser>();

    private async Task<AbsolutePath> WriteFile(string fileName)
    {
        var folder = TemporaryFileManager.CreateFolder();
        var path = folder.Path.Combine(fileName);
        await path.WriteAllTextAsync(Guid.NewGuid().ToString());
        return path;
    }

    private LibraryItem.ReadOnly Reload(EntityId id) => LibraryItem.Load(_connection.Db, id);

    [Fact]
    public async Task AddLocalFile_WithGameAcquisition_RecordsThatGame()
    {
        var file = await _libraryService.AddLocalFile(await WriteFile("game.txt"), new GameAcquisition(DestinationGameA));

        var item = Reload(file.Id);
        LibraryDestinations.GamesOf(item).Should().BeEquivalentTo([DestinationGameA]);
        item.Contains(LibraryItem.DestinationUnknown).Should().BeFalse();
    }

    [Fact]
    public async Task AddLocalFile_WithLoadoutAcquisition_RecordsTheLoadoutsGame()
    {
        var loadout = await CreateLoadout();

        var file = await _libraryService.AddLocalFile(await WriteFile("loadout.txt"), new LoadoutAcquisition(loadout.LoadoutId));

        LibraryDestinations.GamesOf(Reload(file.Id)).Should().BeEquivalentTo([GameInstallation.Game.GameId]);
    }

    [Fact]
    public async Task AddLocalFile_Unassigned_MarksUnknown()
    {
        var file = await _libraryService.AddLocalFile(await WriteFile("unassigned.txt"), UnassignedAcquisition.Instance);

        var item = Reload(file.Id);
        LibraryDestinations.GamesOf(item).Should().BeEmpty();
        item.Contains(LibraryItem.DestinationUnknown).Should().BeTrue();
    }

    [Fact]
    public async Task AddDownload_RecordsTheGame_InTheSameTransactionAsTheFile()
    {
        await using var destination = TemporaryFileManager.CreateFile();
        await destination.Path.WriteAllTextAsync(Guid.NewGuid().ToString());
        IJobTask<IDownloadJob, AbsolutePath> downloadJob = ServiceProvider.GetRequiredService<IJobMonitor>()
            .Begin<CompletedDownloadJob, AbsolutePath>(new CompletedDownloadJob(destination.Path));

        var file = await _libraryService.AddDownload(downloadJob, new GameAcquisition(DestinationGameA));

        LibraryDestinations.GamesOf(Reload(file.Id)).Should().BeEquivalentTo([DestinationGameA]);
        _connection.Db.Get(file.Id).Select(datom => datom.T).Distinct()
            .Should().HaveCount(1, "the file and its destination are one transaction");
    }

    [Fact]
    public async Task AddLocalFile_NestedArchiveMembers_GetNoDestination()
    {
        var archivePath = FileSystem.GetKnownPath(KnownPath.CurrentDirectory).Combine("Resources").Combine("nested_archive.zip");

        var file = await _libraryService.AddLocalFile(archivePath, new GameAcquisition(DestinationGameA));

        file.AsLibraryFile().TryGetAsLibraryArchive(out var archive).Should().BeTrue();
        archive.Children.Should().NotBeEmpty();
        foreach (var member in archive.Children)
        {
            var memberItem = member.AsLibraryFile().AsLibraryItem();
            memberItem.DestinationGames.Should().BeEmpty();
            memberItem.Contains(LibraryItem.DestinationUnknown).Should().BeFalse();
        }
        LibraryDestinations.ItemsFor(_connection.Db, DestinationGameA).Select(item => item.Id)
            .Should().BeEquivalentTo([file.Id]);
    }

    [Fact]
    public async Task AddLocalFile_ARecogniserThatNamesAGame_Wins_AndIsTheOnlyValue()
    {
        var file = await _libraryService.AddLocalFile(await WriteFile(RecognisedFileName), new GameAcquisition(DestinationGameA));

        var item = Reload(file.Id);
        LibraryDestinations.GamesOf(item).Should().BeEquivalentTo([DestinationGameB]);
        item.Contains(LibraryItem.DestinationUnknown).Should().BeFalse();
        _connection.Db.Datoms(file.Id).Count(datom => datom.A == _connection.AttributeCache.GetAttributeId(LibraryItem.DestinationGames.Id))
            .Should().Be(1);
    }

    [Fact]
    public async Task Adopt_UnionsTheGames_IntoAnExistingItem()
    {
        var file = await _libraryService.AddLocalFile(await WriteFile("adopt.txt"), new GameAcquisition(DestinationGameA));

        await _libraryService.Adopt(Reload(file.Id), new GameAcquisition(DestinationGameB));

        LibraryDestinations.GamesOf(Reload(file.Id)).Should().BeEquivalentTo([DestinationGameA, DestinationGameB]);
    }

    /// <summary>A download that has already finished, so the library step can run without a network.</summary>
    private sealed class CompletedDownloadJob(AbsolutePath path) : IDownloadJob, IJobDefinitionWithStart<CompletedDownloadJob, AbsolutePath>
    {
        public AbsolutePath Destination => path;
        public ValueTask<AbsolutePath> StartAsync(IJobContext<CompletedDownloadJob> context) => ValueTask.FromResult(path);
        public ValueTask AddMetadata(ITransaction tx, LibraryFile.New libraryFile) => ValueTask.CompletedTask;
    }
}
