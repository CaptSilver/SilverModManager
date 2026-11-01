using System.IO.Compression;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Library.Migrations;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.DataModel.SchemaVersions.Tests;

/// <summary>
/// Library items added before destinations were recorded get a game from the evidence the database holds, or are
/// marked unknown. Rows are written without any destination attribute, the way an older version left them.
/// </summary>
public class LibraryDestinationMigrationTests
{
    private static readonly GameId SkyrimSe = GameId.From("CreationEngine.SkyrimSE");
    private static readonly GameId SkyrimVr = GameId.From("CreationEngine.SkyrimVR");
    private static readonly NexusModsGameId SkyrimNexusId = NexusModsGameId.From(1704);

    private sealed class Legacy(IConnection connection)
    {
        public async Task<EntityId> AddItem(Action<ITransaction, EntityId>? extend = null)
        {
            var bytes = Guid.NewGuid().ToByteArray();
            using var tx = connection.BeginTransaction();
            var file = new LibraryFile.New(tx, out var id)
            {
                FileName = $"{Guid.NewGuid():N}.zip",
                Hash = bytes.xxHash3(),
                Size = Size.From((ulong)bytes.Length),
                LibraryItem = new LibraryItem.New(tx, id) { Name = "legacy" },
            };
            _ = file;
            extend?.Invoke(tx, id);
            var result = await tx.Commit();
            return result[id];
        }

        public async Task<Loadout.ReadOnly> CreateLoadout(GameId game)
        {
            using var tx = connection.BeginTransaction();
            var metadata = new GameInstallMetadata.New(tx)
            {
                Path = "/tmp/destination-migration-" + Guid.NewGuid(),
                Name = game.ToString(),
                Store = GameStore.Unknown,
                GameId = SkyrimNexusId,
            };
            tx.Add(metadata.Id, GameInstallMetadata.Game, game);
            var loadout = new Loadout.New(tx)
            {
                Name = "Legacy " + Guid.NewGuid(),
                ShortName = "LG",
                InstallationId = metadata,
                LoadoutKind = LoadoutKind.Default,
                Revision = 0,
                GameVersion = VanityVersion.From("Unknown"),
            };
            var result = await tx.Commit();
            return result.Remap(loadout);
        }

        public async Task Install(Loadout.ReadOnly loadout, EntityId item)
        {
            using var tx = connection.BeginTransaction();
            var group = new LoadoutItemGroup.New(tx, out var id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { Name = "mod", LoadoutId = loadout.LoadoutId },
            };
            _ = new LibraryLinkedLoadoutItem.New(tx, id) { LibraryItemId = item, LoadoutItemGroup = group };
            await tx.Commit();
        }

        public static void AsNexusItem(ITransaction tx, EntityId id, NexusModsGameId nexusId)
        {
            var page = new NexusModsModPageMetadata.New(tx)
            {
                Uid = new ModUid(ModId.From((uint)Random.Shared.Next(1, int.MaxValue)), nexusId),
                Name = "A mod",
                GameDomain = GameDomain.From("skyrimspecialedition"),
                UpdatedAt = DateTime.UnixEpoch,
                DataUpdatedAt = DateTime.UnixEpoch,
            };
            var metadata = new NexusModsFileMetadata.New(tx)
            {
                Uid = new FileUid(FileId.From((uint)Random.Shared.Next(1, int.MaxValue)), nexusId),
                Name = "A file",
                Version = "1",
                UploadedAt = DateTime.UnixEpoch,
                ModPageId = page,
            };
            tx.Add(id, NexusModsLibraryItem.FileMetadataId, metadata.Id);
            tx.Add(id, NexusModsLibraryItem.ModPageMetadataId, page.Id);
        }

        public static void AsModlist(ITransaction tx, EntityId id, string gameType)
        {
            tx.Add(id, WabbajackModlistLibraryFile.Name, "list");
            tx.Add(id, WabbajackModlistLibraryFile.Author, "");
            tx.Add(id, WabbajackModlistLibraryFile.Version, "1");
            tx.Add(id, WabbajackModlistLibraryFile.GameType, gameType);
            tx.Add(id, WabbajackModlistLibraryFile.ArchiveCount, 0);
            tx.Add(id, WabbajackModlistLibraryFile.ArchiveSize, Size.From(0));
            tx.Add(id, WabbajackModlistLibraryFile.InstallSize, Size.From(0));
            tx.Add(id, WabbajackModlistLibraryFile.IsNsfw, false);
        }
    }

    private static IReadOnlySet<GameId> GamesOf(IConnection connection, EntityId item)
        => LibraryDestinations.GamesOf(LibraryItem.Load(connection.Db, item));

    private static bool IsUnknown(IConnection connection, EntityId item) => LibraryItem.Load(connection.Db, item).Contains(LibraryItem.DestinationUnknown);

    private static bool IsShared(IConnection connection, EntityId item) => LibraryItem.Load(connection.Db, item).Contains(LibraryItem.DestinationsShared);

    private static Task Migrate(ServiceProvider provider) => provider.GetRequiredService<MigrationService>().MigrateAll();

    /// <summary>Runs the migration the way the service does, without its version bookkeeping, so it can run again.</summary>
    private static async Task RunMigrationDirectly(ServiceProvider provider)
    {
        var connection = provider.GetRequiredService<IConnection>();
        var migration = provider.GetRequiredService<_0006_AssignLibraryDestinations>();
        await migration.Prepare(connection.Db);
        using var tx = connection.BeginTransaction();
        migration.Migrate(tx, connection.Db);
        await tx.Commit();
    }

    [Fact]
    public async Task AnInstalledItem_GetsTheLoadoutsGame_NotTheNexusDomains()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var legacy = new Legacy(connection);
        await legacy.CreateLoadout(SkyrimSe);
        var vrLoadout = await legacy.CreateLoadout(SkyrimVr);
        var item = await legacy.AddItem((tx, id) => Legacy.AsNexusItem(tx, id, SkyrimNexusId));
        await legacy.Install(vrLoadout, item);

        await Migrate(provider);

        GamesOf(connection, item).Should().BeEquivalentTo([SkyrimVr]);
        IsShared(connection, item).Should().BeFalse();
        IsUnknown(connection, item).Should().BeFalse();
    }

    [Fact]
    public async Task AFetchedArchive_GetsTheModlistsGame()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var legacy = new Legacy(connection);
        var list = await legacy.AddItem((tx, id) => Legacy.AsModlist(tx, id, "SkyrimVR"));
        var fetched = await legacy.AddItem();
        using (var tx = connection.BeginTransaction())
        {
            var row = new WabbajackModlistArchive.New(tx)
            {
                ModlistId = list,
                Identity = FileIdentity.XxHash64(Xx64Hasher.Hash(Guid.NewGuid().ToByteArray())),
                Size = Size.From(1),
                Name = "fetched.7z",
            };
            tx.Add(row.Id, WabbajackModlistArchive.FetchedFiles, fetched);
            await tx.Commit();
        }

        await Migrate(provider);

        GamesOf(connection, fetched).Should().BeEquivalentTo([SkyrimVr]);
    }

    [Fact]
    public async Task AModlistFile_GetsItsOwnGame()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var legacy = new Legacy(connection);
        var list = await legacy.AddItem((tx, id) => Legacy.AsModlist(tx, id, "SkyrimVR"));

        await Migrate(provider);

        GamesOf(connection, list).Should().BeEquivalentTo([SkyrimVr]);
    }

    [Fact]
    public async Task AThunderstoreFile_GetsItsCommunitysGame()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var game = provider.GetServices<IGameData>().First(candidate => ThunderstoreGame.For(candidate) is not null);
        var item = await new Legacy(connection).AddItem((tx, id) =>
        {
            tx.Add(id, ThunderstoreLibraryFile.Namespace, "team");
            tx.Add(id, ThunderstoreLibraryFile.Name, "package");
            tx.Add(id, ThunderstoreLibraryFile.VersionNumber, "1.0.0");
            tx.Add(id, ThunderstoreLibraryFile.Community, ThunderstoreGame.For(game)!.ThunderstoreCommunity);
            tx.Add(id, ThunderstoreLibraryFile.PinnedSha256, Sha256Value.From(new byte[32]));
        });

        await Migrate(provider);

        GamesOf(connection, item).Should().Contain(game.GameId);
        IsUnknown(connection, item).Should().BeFalse();
    }

    [Fact]
    public async Task ANexusItemWithNoOtherEvidence_GetsEveryManagedGameOfTheId_Shared()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var legacy = new Legacy(connection);
        await legacy.CreateLoadout(SkyrimSe);
        await legacy.CreateLoadout(SkyrimVr);
        var item = await legacy.AddItem((tx, id) => Legacy.AsNexusItem(tx, id, SkyrimNexusId));

        await Migrate(provider);

        GamesOf(connection, item).Should().BeEquivalentTo([SkyrimSe, SkyrimVr]);
        IsShared(connection, item).Should().BeTrue();
    }

    [Fact]
    public async Task ANexusCollectionFile_GetsTheManagedGamesOfItsCollectionsId()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var legacy = new Legacy(connection);
        await legacy.CreateLoadout(SkyrimVr);
        var slug = CollectionSlug.From("migration-" + Guid.NewGuid().ToString("N"));
        using (var tx = connection.BeginTransaction())
        {
            var author = new User.New(tx) { Name = "Author", NexusId = 424242 };
            var collection = new CollectionMetadata.New(tx)
            {
                CollectionId = CollectionId.From(424242),
                Slug = slug,
                Name = "A collection",
                GameId = SkyrimNexusId,
                AuthorId = author,
            };
            _ = new CollectionRevisionMetadata.New(tx)
            {
                RevisionId = RevisionId.From(424242),
                RevisionNumber = RevisionNumber.From(2),
                CollectionId = collection,
            };
            await tx.Commit();
        }
        var file = await legacy.AddItem((tx, id) =>
        {
            tx.Add(id, NexusModsCollectionLibraryFile.CollectionSlug, slug);
            tx.Add(id, NexusModsCollectionLibraryFile.CollectionRevisionNumber, RevisionNumber.From(2));
        });

        await Migrate(provider);

        GamesOf(connection, file).Should().BeEquivalentTo([SkyrimVr]);
    }

    [Fact]
    public async Task ALocalFileWithNoEvidence_IsMarkedUnknown()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var item = await new Legacy(connection).AddItem();

        await Migrate(provider);

        IsUnknown(connection, item).Should().BeTrue();
        GamesOf(connection, item).Should().BeEmpty();
    }

    [Fact]
    public async Task AnArchiveMember_IsLeftAlone()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var library = provider.GetRequiredService<ILibraryService>();
        var path = folder.DatabaseFolder.Parent.Combine("holder.zip");
        path.Parent.CreateDirectory();
        using (var zip = new ZipArchive(File.Create(path.ToString()), ZipArchiveMode.Create))
        {
            using var entry = zip.CreateEntry("inside.txt").Open();
            entry.Write("a member of the archive"u8);
        }
        var archive = (await library.AddLocalFile(path, UnassignedAcquisition.Instance)).AsLibraryFile().Id;
        var member = LibraryArchiveFileEntry.All(connection.Db).Single(entry => entry.ParentId.Value == archive).Id;
        await ForgetDestination(connection, archive);
        await ForgetDestination(connection, member);

        await Migrate(provider);

        IsUnknown(connection, archive).Should().BeTrue("the archive is a top-level item");
        IsUnknown(connection, member).Should().BeFalse("a member shows with the archive that holds it");
        GamesOf(connection, member).Should().BeEmpty();
    }

    [Fact]
    public async Task AnItemAlreadyAssigned_IsLeftAlone()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var legacy = new Legacy(connection);
        var vrLoadout = await legacy.CreateLoadout(SkyrimVr);
        var assigned = await legacy.AddItem((tx, id) => LibraryDestinations.Write(tx, id, Destination.Of(SkyrimSe)));
        var unknown = await legacy.AddItem((tx, id) => LibraryDestinations.Write(tx, id, Destination.Unknown));
        await legacy.Install(vrLoadout, assigned);
        await legacy.Install(vrLoadout, unknown);

        await Migrate(provider);

        GamesOf(connection, assigned).Should().BeEquivalentTo([SkyrimSe], "the install is evidence for a legacy item, not a correction");
        IsUnknown(connection, unknown).Should().BeTrue();
        GamesOf(connection, unknown).Should().BeEmpty();
    }

    [Fact]
    public async Task RunningTwice_ChangesNothing()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var legacy = new Legacy(connection);
        await legacy.CreateLoadout(SkyrimSe);
        await legacy.CreateLoadout(SkyrimVr);
        var nexus = await legacy.AddItem((tx, id) => Legacy.AsNexusItem(tx, id, SkyrimNexusId));
        var local = await legacy.AddItem();
        await Migrate(provider);
        var before = Snapshot(connection, nexus, local);
        before.Should().NotBeEmpty();

        await RunMigrationDirectly(provider);

        Snapshot(connection, nexus, local).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task AFreshDatabase_NeedsNoWork()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var service = provider.GetRequiredService<MigrationService>();
        await service.InitialSetup();
        var item = await new Legacy(connection).AddItem();

        await service.MigrateAll();

        var id = _0006_AssignLibraryDestinations.IdAndName.Id;
        MigrationLogItem.All(connection.Db).Single(log => log.MigrationId == id).WasRun.Should().BeFalse();
        IsUnknown(connection, item).Should().BeFalse();
        GamesOf(connection, item).Should().BeEmpty();
    }

    private static async Task ForgetDestination(IConnection connection, EntityId item)
    {
        var current = LibraryItem.Load(connection.Db, item);
        using var tx = connection.BeginTransaction();
        foreach (var game in current.DestinationGames) tx.Retract(item, LibraryItem.DestinationGames, game);
        if (current.Contains(LibraryItem.DestinationUnknown)) tx.Retract(item, LibraryItem.DestinationUnknown, Null.Instance);
        if (current.Contains(LibraryItem.DestinationsShared)) tx.Retract(item, LibraryItem.DestinationsShared, Null.Instance);
        await tx.Commit();
    }

    private static string[] Snapshot(IConnection connection, params EntityId[] items) => items
        .Select(item => LibraryItem.Load(connection.Db, item))
        .Select(item => string.Join(",", item.DestinationGames.Select(game => game.ToString()).Order())
                        + $"|shared={item.Contains(LibraryItem.DestinationsShared)}|unknown={item.Contains(LibraryItem.DestinationUnknown)}")
        .ToArray();
}
