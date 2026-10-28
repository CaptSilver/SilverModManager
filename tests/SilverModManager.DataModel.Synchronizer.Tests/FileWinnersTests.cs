using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using SilverModManager.StandardGameLocators.TestHelpers;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// <see cref="FileWinners"/> restates the SQL macro that picks the deployed file of a path. These tests run both on one
/// fixture so the two cannot drift apart unnoticed.
/// </summary>
public class FileWinnersTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<FileWinnersTests>(helper)
{
    private static GamePath P(string name) => new(LocationId.Game, $"archive/pc/mod/{name}.archive");

    private async Task<Loadout.ReadOnly> Fixture()
    {
        var loadout = await CreateLoadout();
        LoadoutItemGroupId low, high, other;
        using (var tx = Connection.BeginTransaction())
        {
            var l = AddEmptyGroup(tx, loadout.LoadoutId, "Low");
            var h = AddEmptyGroup(tx, loadout.LoadoutId, "High");
            var o = AddEmptyGroup(tx, loadout.LoadoutId, "Other");
            _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = l, Priority = ConflictPriority.From(1) };
            _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = h, Priority = ConflictPriority.From(3) };
            _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = o, Priority = ConflictPriority.From(2) };
            var result = await tx.Commit();
            (low, high, other) = (result[l], result[h], result[o]);
        }

        LoadoutItemGroupId inner;
        using (var tx = Connection.BeginTransaction())
        {
            // A group of High's with no priority row of its own: it ranks zero, whatever High has.
            var group = new LoadoutItemGroup.New(tx, out var id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { LoadoutId = loadout.LoadoutId, Name = "High inner", ParentId = high },
            };
            inner = (await tx.Commit())[group.Id];
        }

        using (var tx = Connection.BeginTransaction())
        {
            foreach (var group in new[] { low, high, other })
            {
                AddFile(tx, loadout.LoadoutId, group, P("plain"), $"plain {group}");
                AddFile(tx, loadout.LoadoutId, group, P("pinned-low"), $"pinned-low {group}");
                AddFile(tx, loadout.LoadoutId, group, P("pinned-high"), $"pinned-high {group}");
            }
            AddFile(tx, loadout.LoadoutId, inner, P("nested"), "nested inner");
            AddFile(tx, loadout.LoadoutId, low, P("nested"), "nested low");
            await tx.Commit();
        }

        await LoadoutManager.PinFileWinner(loadout.LoadoutId, P("pinned-low"), low);
        await LoadoutManager.PinFileWinner(loadout.LoadoutId, P("pinned-high"), high);
        return Refresh(loadout);
    }

    [Fact]
    public async Task ItPicksTheSameGroupAsTheSynchronizersWinnerQueryForEveryPath()
    {
        var loadout = await Fixture();
        var db = Connection.Db;

        var sql = Connection.Query<(EntityId Id, EntityId Parent)>($"SELECT Id, Parent FROM synchronizer.WinningLeafLoadoutItem({db}) WHERE Loadout = {loadout.LoadoutId}")
            .ToDictionary(r => (GamePath)LoadoutItemWithTargetPath.Load(db, r.Id).TargetPath, r => r.Parent);

        var winners = FileWinners.Of(db, loadout.LoadoutId);
        var mine = LoadoutItem.FindByLoadout(db, loadout).OfTypeLoadoutItemWithTargetPath()
            .Select(i => (Path: (GamePath)i.TargetPath, Parent: LoadoutItemGroupId.From(i.AsLoadoutItem().ParentId.Value)))
            .GroupBy(x => x.Path)
            .ToDictionary(g => g.Key, g => g.MaxBy(x => winners.RankOf(x.Path, x.Parent)).Parent.Value);

        sql.Should().HaveCount(4);
        mine.Should().BeEquivalentTo(sql);
    }

    // The game files of the stub are rows of the local store, the way a baseline would hold them.
    private async Task<(string Path, MultiHash Hashes)[]> SeedGameFiles(Loadout.ReadOnly loadout, params string[] files)
    {
        var local = ((StubbedFileHasherService)ServiceProvider.GetRequiredService<IFileHashesService>()).Local.Connection;
        var install = loadout.Installation;
        var seeded = new List<(string, MultiHash)>();

        using var tx = local.BeginTransaction();
        var pathIds = new List<EntityId>();
        foreach (var file in files)
        {
            var hashes = await MultiHasher.HashStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("vanilla " + file)));
            var relation = new HashRelation.New(tx)
            {
                MinimalHash = hashes.MinimalHash,
                XxHash3 = hashes.XxHash3,
                Sha1 = hashes.Sha1,
                XxHash64 = hashes.XxHash64,
                Md5 = hashes.Md5,
                Size = hashes.Size,
                Crc32 = hashes.Crc32,
            };
            pathIds.Add(new PathHashRelation.New(tx) { Path = RelativePath.FromUnsanitizedInput(file), HashId = relation }.Id);
            seeded.Add((file, hashes));
        }

        _ = new LocalBaseline.New(tx)
        {
            Store = install.Store,
            GamePath = install.Path,
            // A baseline counts only for the locator ids it was built for.
            LocatorIds = loadout.LocatorIds.Select(id => id.Value).ToArray(),
            Kind = LocalBaselineKind.Snapshot,
            VersionName = "",
            BuiltAt = DateTimeOffset.UnixEpoch,
            FilesIds = pathIds.ToArray(),
        };
        await tx.Commit();
        return seeded.ToArray();
    }

    [Fact]
    public async Task GameLayerIdentityFromSql_IsTheXxHash3IdentityOfTheRowsHash()
    {
        var loadout = await CreateLoadout();
        var seeded = await SeedGameFiles(loadout, "game/a.bin", "game/b.bin", "c.bin");

        var rows = ((ALoadoutSynchronizer)Synchronizer).WinningFilesForTest(loadout).Where(r => r.ItemType == "Game").ToDictionary(r => r.Path.Path.ToString());

        foreach (var (path, hashes) in seeded)
        {
            rows.Should().ContainKey(path);
            // ParseHex is case-insensitive; to_hex's casing is not something to pin.
            FileIdentity.ParseHex(rows[path].Identity).Should().Be(FileIdentity.XxHash3(hashes.XxHash3), $"the SQL and the C# identity of {path} are the same bytes");
        }
    }

    [Fact]
    public async Task ALoadoutFileWinsWithItsOwnXxHash3Identity()
    {
        var loadout = await CreateLoadout();
        Hash mineHash;
        using (var tx = Connection.BeginTransaction())
        {
            var g = AddEmptyGroup(tx, loadout.LoadoutId, "Mod");
            AddFile(tx, loadout.LoadoutId, g, P("mine"), "mine", out mineHash, out _);
            await tx.Commit();
        }

        var row = ((ALoadoutSynchronizer)Synchronizer).WinningFilesForTest(Refresh(loadout)).Single(r => r.Path == P("mine"));

        row.ItemType.Should().Be("Loadout");
        FileIdentity.ParseHex(row.Identity).Should().Be(FileIdentity.XxHash3(mineHash));
    }
}

/// <summary>
/// A generated file wins its path with no identity, whatever the game folder holds.
/// </summary>
public class IntrinsicWinnerIdentityTests(ITestOutputHelper helper) : AIsolatedGameTest<IntrinsicWinnerIdentityTests, IntrinsicStubbedGame>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddGame<IntrinsicStubbedGame>()
        .AddUniversalGameLocator<IntrinsicStubbedGame>(Version.Parse("0.0.0"));

    [Fact]
    public async Task IntrinsicLayer_WinsWithAnEmptyIdentity()
    {
        var loadout = await CreateLoadout();

        var row = ((ALoadoutSynchronizer)Synchronizer).WinningFilesForTest(loadout).Single(r => r.Path == IntrinsicStubbedGame.GeneratedPath);
        var tree = await Synchronizer.BuildSyncTree(loadout);

        row.ItemType.Should().Be("Intrinsic");
        tree[IntrinsicStubbedGame.GeneratedPath].Loadout.Identity.IsNone.Should().BeTrue("an intrinsic file has no loadout side to compare");
    }
}
