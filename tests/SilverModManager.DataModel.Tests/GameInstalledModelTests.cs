using FluentAssertions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Loadouts;
using Loadout = NexusMods.Sdk.Loadouts.Loadout;

namespace NexusMods.DataModel.Tests;

public class GameInstalledModelTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<GameInstalledModelTests>(helper)
{
    private static GamePath DataPath(string name) => new(LocationId.Game, RelativePath.FromUnsanitizedInput("Data").Join(name));

    private GameInstalledGroupId AddGroup(ITransaction tx, Loadout.ReadOnly loadout)
    {
        _ = new GameInstalledGroup.New(tx, out var id)
        {
            InstalledForId = loadout,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id)
                {
                    Name = "Installed by the game",
                    LoadoutId = loadout.Id,
                },
            },
        };
        return id;
    }

    private GameInstalledItemId AddItem(ITransaction tx, Loadout.ReadOnly loadout, EntityId parent, string sourceId, string? version)
    {
        _ = new GameInstalledItem.New(tx, out var id)
        {
            Source = "SkyrimCreation",
            SourceId = sourceId,
            Version = version,
            Confidence = "GameRecord",
            CanBeClaimed = false,
            DepotShipped = true,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id)
                {
                    Name = sourceId,
                    LoadoutId = loadout.Id,
                    ParentId = parent,
                },
            },
        };
        return id;
    }

    private GameInstalledFileId AddFile(ITransaction tx, Loadout.ReadOnly loadout, EntityId parent, GamePath path, ulong hash, ulong size)
    {
        _ = new GameInstalledFile.New(tx, out var id)
        {
            Path = path.ToGamePathParentTuple(loadout.Id),
            Hash = Hash.From(hash),
            Size = Size.From(size),
            LoadoutItem = new LoadoutItem.New(tx, id)
            {
                Name = path.FileName,
                LoadoutId = loadout.Id,
                ParentId = parent,
            },
        };
        return id;
    }

    [Fact]
    public async Task GroupItemAndFile_RoundTrip()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, "Round Trip");

        using var tx = Connection.BeginTransaction();
        var group = AddGroup(tx, loadout);
        var item = AddItem(tx, loadout, group, "cc_one", "1.2.3");
        var file = AddFile(tx, loadout, item, DataPath("cc_one.esm"), 0xAB, 42);
        var result = await tx.Commit();

        var db = result.Db;
        var readGroup = GameInstalledGroup.Load(db, result[group]);
        readGroup.InstalledForId.Should().Be(loadout.LoadoutId);
        readGroup.AsLoadoutItemGroup().AsLoadoutItem().Name.Should().Be("Installed by the game");
        GameInstalledGroup.FindByInstalledFor(db, loadout.Id).Should().ContainSingle();

        var readItem = GameInstalledItem.Load(db, result[item]);
        readItem.Source.Should().Be("SkyrimCreation");
        readItem.SourceId.Should().Be("cc_one");
        readItem.Version.Value.Should().Be("1.2.3");
        readItem.Confidence.Should().Be("GameRecord");
        readItem.CanBeClaimed.Should().BeFalse();
        readItem.DepotShipped.Should().BeTrue();
        readItem.AsLoadoutItemGroup().AsLoadoutItem().ParentId.Value.Should().Be(result[group].Value);

        var readFile = GameInstalledFile.Load(db, result[file]);
        ((GamePath)readFile.Path).Should().Be(DataPath("cc_one.esm"));
        readFile.Hash.Should().Be(Hash.From(0xAB));
        readFile.Size.Should().Be(Size.From(42));
        readFile.AsLoadoutItem().ParentId.Value.Should().Be(result[item].Value);
    }

    [Fact]
    public async Task Item_WithoutVersion_ReadsBackWithoutOne()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, "No Version");

        using var tx = Connection.BeginTransaction();
        var group = AddGroup(tx, loadout);
        var item = AddItem(tx, loadout, group, "cc_two", version: null);
        var result = await tx.Commit();

        var readItem = GameInstalledItem.Load(result.Db, result[item]);
        readItem.Version.HasValue.Should().BeFalse();
        GameInstalledItem.Version.TryGetValue(readItem, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Claim_RoundTrips_AndIsNotALoadoutItem()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, "Claim");
        var claimedAt = new DateTimeOffset(2026, 10, 22, 1, 2, 3, TimeSpan.Zero);

        using var tx = Connection.BeginTransaction();
        var newClaim = new GameInstalledClaim.New(tx)
        {
            LoadoutId = loadout.LoadoutId,
            Path = DataPath("cc_three.esm").ToGamePathParentTuple(loadout.Id),
            Source = "Bg3InGameMod",
            SourceId = "Three.pak",
            ClaimedAt = claimedAt,
        };
        var id = newClaim.Id;
        var result = await tx.Commit();

        var claim = GameInstalledClaim.Load(result.Db, result[id]);
        claim.LoadoutId.Should().Be(loadout.LoadoutId);
        ((GamePath)claim.Path).Should().Be(DataPath("cc_three.esm"));
        claim.Source.Should().Be("Bg3InGameMod");
        claim.SourceId.Should().Be("Three.pak");
        claim.ClaimedAt.Should().Be(claimedAt);
        claim.Contains(LoadoutItem.Name).Should().BeFalse();
    }

    [Fact]
    public async Task GameInstalledFile_IsNotAWinningFile()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, "Not Winning");

        using var tx = Connection.BeginTransaction();
        var group = AddGroup(tx, loadout);
        var item = AddItem(tx, loadout, group, "cc_four", "1.0");
        var file = AddFile(tx, loadout, item, DataPath("cc_four.esm"), 0xCD, 7);
        var result = await tx.Commit();

        var readFile = GameInstalledFile.Load(result.Db, result[file]);
        readFile.AsLoadoutItem().TryGetAsLoadoutItemWithTargetPath(out _).Should().BeFalse();

        var winners = Connection.Query<EntityId>(
            $"SELECT Id FROM synchronizer.WinningFiles({result.Db}) WHERE Loadout = {loadout.LoadoutId} AND Id IS NOT NULL"
        ).ToList();
        winners.Should().NotContain(result[file]);
    }

    [Fact]
    public async Task GameInstalledPaths_For_ReturnsOnlyTheLoadoutsFiles()
    {
        var first = await LoadoutManager.CreateLoadout(GameInstallation, "Paths First");
        var second = await LoadoutManager.CreateLoadout(GameInstallation, "Paths Second");

        using var tx = Connection.BeginTransaction();
        var firstItem = AddItem(tx, first, AddGroup(tx, first), "cc_five", "1");
        AddFile(tx, first, firstItem, DataPath("cc_five.esm"), 0x11, 100);
        AddFile(tx, first, firstItem, DataPath("cc_five.bsa"), 0x22, 200);
        var secondItem = AddItem(tx, second, AddGroup(tx, second), "cc_six", "1");
        AddFile(tx, second, secondItem, DataPath("cc_six.esm"), 0x33, 300);
        var result = await tx.Commit();

        var paths = GameInstalledPaths.For(result.Db, first.LoadoutId);

        paths.Should().BeEquivalentTo(new Dictionary<GamePath, (Hash, Size)>
        {
            [DataPath("cc_five.esm")] = (Hash.From(0x11), Size.From(100)),
            [DataPath("cc_five.bsa")] = (Hash.From(0x22), Size.From(200)),
        });
        GameInstalledPaths.For(result.Db, second.LoadoutId).Should().ContainSingle()
            .Which.Key.Should().Be(DataPath("cc_six.esm"));
    }

    [Fact]
    public async Task DeletingTheGroup_RemovesItemsAndFiles_ButKeepsTheClaim()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, "Delete Group");

        using var setup = Connection.BeginTransaction();
        var group = AddGroup(setup, loadout);
        var item = AddItem(setup, loadout, group, "cc_seven", "1");
        var file = AddFile(setup, loadout, item, DataPath("cc_seven.esm"), 0x44, 5);
        var newClaim = new GameInstalledClaim.New(setup)
        {
            LoadoutId = loadout.LoadoutId,
            Path = DataPath("cc_other.esm").ToGamePathParentTuple(loadout.Id),
            Source = "SkyrimCreation",
            SourceId = "cc_other",
            ClaimedAt = DateTimeOffset.UnixEpoch,
        };
        var claim = newClaim.Id;
        var created = await setup.Commit();

        using var delete = Connection.BeginTransaction();
        delete.Delete(created[group], recursive: true);
        var result = await delete.Commit();

        var db = result.Db;
        db.Get(created[group]).Count.Should().Be(0);
        db.Get(created[item]).Count.Should().Be(0);
        db.Get(created[file]).Count.Should().Be(0);
        GameInstalledClaim.Load(db, created[claim]).IsValid().Should().BeTrue();
    }
}
