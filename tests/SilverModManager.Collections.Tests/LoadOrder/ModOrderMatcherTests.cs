using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using Xunit;

namespace SilverModManager.Collections.Tests.LoadOrder;

public class ModOrderMatcherTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<ModOrderMatcherTests>(helper)
{
    private static readonly NexusModsGameId GameId = Cyberpunk2077Game.NexusModsGameId.Value;

    private static Md5Value Md5Of(int n) => Md5Value.From((UInt128)n);

    /// <summary>The ids the container and its children get once the transaction is committed.</summary>
    private record Built(LoadoutItemGroupId Container, IReadOnlyDictionary<string, LoadoutItemGroupId> Groups)
    {
        public LoadoutItemGroupId this[string name] => Groups[name];
    }

    private sealed class Children(ITransaction tx, LoadoutId loadout, LoadoutItemGroupId container)
    {
        private readonly Dictionary<string, LoadoutItemGroupId> _ids = new();
        private EntityId? _revision;

        public IReadOnlyDictionary<string, LoadoutItemGroupId> Ids => _ids;

        public void Named(string name) => _ids[name] = AddGroup(name).Id;

        /// <summary>An installed group of a direct (non-Nexus) collection download that has the md5.</summary>
        public void WithMd5(string name, int md5)
        {
            var download = NewDownload(name);
            _ = new CollectionDownloadExternal.New(tx, download.Id)
            {
                CollectionDownload = download,
                Md5 = Md5Of(md5),
                Size = Size.FromLong(1),
                Uri = new Uri("https://example.invalid/mod.zip"),
            };
            AsCollectionItem(name, download.Id);
        }

        /// <summary>An installed group of a Nexus collection download with the ids, optionally linked to a library file with the md5.</summary>
        public void WithIds(string name, uint modId, uint fileId, int? libraryMd5 = null)
        {
            var download = NewDownload(name);
            var page = new NexusModsModPageMetadata.New(tx)
            {
                Uid = new ModUid(ModId.From(modId), GameId),
                Name = name,
                GameDomain = GameDomain.From("cyberpunk2077"),
                UpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
                DataUpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            };
            var file = new NexusModsFileMetadata.New(tx)
            {
                Uid = new FileUid(FileId.From(fileId), GameId),
                Name = name,
                Version = "1",
                UploadedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
                ModPageId = page,
            };
            _ = new CollectionDownloadNexusMods.New(tx, download.Id)
            {
                CollectionDownload = download,
                FileUid = new FileUid(FileId.From(fileId), GameId),
                ModUid = new ModUid(ModId.From(modId), GameId),
                FileMetadataId = file.Id,
            };
            var item = AsCollectionItem(name, download.Id);

            if (libraryMd5 is { } md5)
            {
                var libraryFile = new LibraryFile.New(tx, out var libraryId)
                {
                    FileName = $"{name}.zip",
                    Hash = name.xxHash3AsUtf8(),
                    Size = Size.FromLong(1),
                    Md5 = Md5Of(md5),
                    LibraryItem = new LibraryItem.New(tx, libraryId) { Name = $"{name}.zip" },
                };
                _ = new LibraryLinkedLoadoutItem.New(tx, item.Id)
                {
                    LibraryItemId = libraryFile.Id,
                    LoadoutItemGroup = item.GetLoadoutItemGroup(tx),
                };
            }
        }

        public void Priority(string name, ulong priority)
        {
            _ = new LoadoutItemGroupPriority.New(tx)
            {
                LoadoutId = loadout,
                TargetId = _ids[name],
                Priority = ConflictPriority.From(priority),
            };
        }

        private LoadoutItemGroup.New AddGroup(string name)
        {
            return new LoadoutItemGroup.New(tx, out var id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { LoadoutId = loadout, Name = name, ParentId = container },
            };
        }

        private NexusCollectionItemLoadoutGroup.New AsCollectionItem(string name, CollectionDownloadId download)
        {
            var group = AddGroup(name);
            _ids[name] = group.Id;
            return new NexusCollectionItemLoadoutGroup.New(tx, group.Id)
            {
                DownloadId = download,
                IsRequired = true,
                LoadoutItemGroup = group,
            };
        }

        private CollectionDownload.New NewDownload(string name)
        {
            _revision ??= NewRevision();
            return new CollectionDownload.New(tx)
            {
                Name = name,
                IsOptional = false,
                ArrayIndex = _ids.Count,
                CollectionRevisionId = _revision.Value,
            };
        }

        private EntityId NewRevision()
        {
            var author = new User.New(tx) { Name = "author", NexusId = 1 };
            var collection = new CollectionMetadata.New(tx)
            {
                CollectionId = CollectionId.From(1),
                Slug = CollectionSlug.From("slug-1"),
                Name = "pack",
                GameId = GameId,
                AuthorId = author.Id,
            };
            return new CollectionRevisionMetadata.New(tx)
            {
                RevisionId = RevisionId.From(1),
                RevisionNumber = RevisionNumber.From(1),
                CollectionId = collection.Id,
            }.Id;
        }
    }

    private async Task<Built> Build(Action<Children> children)
    {
        var loadout = await CreateLoadout();
        using var tx = Connection.BeginTransaction();
        var container = AddEmptyGroup(tx, loadout.LoadoutId, "Pack");
        var builder = new Children(tx, loadout.LoadoutId, container);
        children(builder);
        var result = await tx.Commit();
        return new Built(
            LoadoutItemGroupId.From(result[container].Value),
            builder.Ids.ToDictionary(pair => pair.Key, pair => LoadoutItemGroupId.From(result[pair.Value].Value)));
    }

    private ModOrder Match(Built built, params ModOrderSourceEntry[] entries) => ModOrderMatcher.Match(Connection.Db, built.Container, entries);

    private static ModOrderSourceEntry Entry(ModOrderReference reference, bool? enabled = null) => new(reference, enabled);
    private static ModOrderSourceEntry ByMd5(int md5) => Entry(new ModOrderReference(Md5: Md5Of(md5)));
    private static ModOrderSourceEntry ByIds(uint modId, uint fileId) => Entry(new ModOrderReference(ModId: ModId.From(modId), FileId: FileId.From(fileId)));
    private static ModOrderSourceEntry ByName(string name) => Entry(new ModOrderReference(Name: name));

    [Fact]
    public async Task MatchesByMd5BeforeIdsBeforeName()
    {
        var built = await Build(c =>
        {
            c.WithMd5("A", 1);
            c.WithIds("B", 7, 70);
            c.Named("Charlie");
        });

        var order = Match(built, ByName("Charlie"), ByIds(7, 70), ByMd5(1));

        order.WinnerToLoser.Select(e => e.Group).Should().Equal(built["Charlie"], built["B"], built["A"]);
        order.Dropped.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenMd5IdsAndNamePointAtDifferentGroupsMd5WinsThenIds()
    {
        var built = await Build(c =>
        {
            c.WithMd5("ByMd5", 1);
            c.WithIds("ByIds", 7, 70);
            c.Named("ByName");
        });

        var everything = Entry(new ModOrderReference(Md5Of(1), ModId.From(7), FileId.From(70), "ByName"));
        var idsAndName = Entry(new ModOrderReference(ModId: ModId.From(7), FileId: FileId.From(70), Name: "ByName"));
        var order = Match(built, everything, idsAndName, ByName("ByName"));

        order.WinnerToLoser.Select(e => e.Group).Should().Equal(built["ByMd5"], built["ByIds"], built["ByName"]);
        order.Dropped.Should().BeEmpty();
    }

    [Fact]
    public async Task AModIdWithoutAFileIdDoesNotMatchTheModPage()
    {
        var built = await Build(c => c.WithIds("B", 7, 70));

        var order = Match(built, Entry(new ModOrderReference(ModId: ModId.From(7), Name: "Other")));

        order.WinnerToLoser.Should().BeEmpty();
        order.Dropped.Should().ContainSingle().Which.Item.Should().Be("Other");
    }

    [Fact]
    public async Task NamesMatchIgnoringCase()
    {
        var built = await Build(c => c.Named("Charlie"));

        Match(built, ByName("cHARLIE")).WinnerToLoser.Should().ContainSingle().Which.Group.Should().Be(built["Charlie"]);
    }

    [Fact]
    public async Task TheEnabledFlagOfAnEntryTravelsWithItsGroup()
    {
        var built = await Build(c =>
        {
            c.Named("On");
            c.Named("Off");
            c.Named("Untouched");
        });

        var order = Match(built, Entry(new ModOrderReference(Name: "On"), true), Entry(new ModOrderReference(Name: "Off"), false), ByName("Untouched"));

        order.WinnerToLoser.Select(e => e.Enabled).Should().Equal(true, false, null);
    }

    [Fact]
    public async Task AnUnmatchedEntryIsDroppedWithTheOptionalDownloadWording()
    {
        var built = await Build(c => c.Named("Present"));

        var order = Match(built, ByName("Ghost"));

        order.WinnerToLoser.Should().BeEmpty();
        var drop = order.Dropped.Should().ContainSingle().Subject;
        drop.Item.Should().Be("Ghost");
        drop.Reason.Should().Contain("optional download not installed");
    }

    [Fact]
    public async Task AnUnidentifiedEntryIsDroppedNotThrown()
    {
        var built = await Build(c => c.Named("Present"));

        var order = Match(built, Entry(new ModOrderReference()));

        order.WinnerToLoser.Should().BeEmpty();
        order.Dropped.Should().ContainSingle().Which.Reason.Should().Be("carries no md5, mod id or name");
    }

    [Fact]
    public async Task OneDownloadInstalledTwicePlacesBothGroupsTogether()
    {
        var built = await Build(c =>
        {
            c.WithMd5("Twin low", 5);
            c.WithMd5("Twin high", 5);
            c.Named("Other");
            c.Priority("Twin low", 1);
            c.Priority("Twin high", 2);
        });

        var order = Match(built, ByMd5(5), ByName("Other"));

        order.WinnerToLoser.Select(e => e.Group).Should().Equal(built["Twin high"], built["Twin low"], built["Other"]);
        order.Dropped.Should().BeEmpty();
    }

    [Fact]
    public async Task AnEntryForAnAlreadyPlacedGroupIsDroppedAsListedTwice()
    {
        var built = await Build(c => c.WithMd5("A", 1));

        var order = Match(built, ByMd5(1), ByName("A"));

        order.WinnerToLoser.Select(e => e.Group).Should().Equal(built["A"]);
        var drop = order.Dropped.Should().ContainSingle().Subject;
        drop.Item.Should().Be("A");
        drop.Reason.Should().Contain("more than once");
    }

    [Fact]
    public async Task ReferenceOfANexusGroupCarriesMd5AndIds()
    {
        var built = await Build(c => c.WithIds("Nexus mod", 7, 70, libraryMd5: 9));

        var reference = ModOrderMatcher.ReferenceOf(LoadoutItemGroup.Load(Connection.Db, built["Nexus mod"]));

        reference.Should().Be(new ModOrderReference(Md5Of(9), ModId.From(7), FileId.From(70), "Nexus mod"));
    }

    [Fact]
    public async Task ReferenceOfAPlainGroupIsItsName()
    {
        var built = await Build(c => c.Named("Plain"));

        var reference = ModOrderMatcher.ReferenceOf(LoadoutItemGroup.Load(Connection.Db, built["Plain"]));

        reference.Should().Be(new ModOrderReference(Name: "Plain"));
    }

    [Fact]
    public void DisplayPrefersTheNameThenTheIdsThenTheMd5()
    {
        new ModOrderReference(Md5Of(1), ModId.From(7), FileId.From(70), "Named").Display.Should().Be("Named");
        new ModOrderReference(Md5Of(1), ModId.From(7), FileId.From(70)).Display.Should().Be("mod 7 file 70");
        new ModOrderReference(Md5Of(1)).Display.Should().Be(Md5Of(1).ToString());
    }
}
