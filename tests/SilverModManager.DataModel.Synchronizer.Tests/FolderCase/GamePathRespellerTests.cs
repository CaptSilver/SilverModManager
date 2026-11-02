using System.Text;
using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.FolderCase;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests.FolderCase;

public class GamePathRespellerTests(ITestOutputHelper helper) : AStubbedGameTest<GamePathRespellerTests>(helper)
{
    private static GamePath G(string path) => new(LocationId.Game, RelativePath.FromUnsanitizedInput(path));

    private static FolderSpellings InterfaceIsCapitalised() => FolderSpellings.Build(new FolderSpellingSources(
        [], AllTiers: true, [], [(G("Data/Interface/vanilla.swf"), true)], []));

    private async Task RecordDiskState(Loadout.ReadOnly loadout, string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        using var tx = Connection.BeginTransaction();
        _ = new DiskStateEntry.New(tx, tx.TempId(DiskStateEntry.EntryPartition))
        {
            Path = G(path).ToGamePathParentTuple(loadout.Installation),
            Identity = FileIdentity.XxHash3(bytes.xxHash3()),
            XxHash3 = bytes.xxHash3(),
            Size = Size.FromLong(bytes.Length),
            LastModified = new DateTime(2021, 5, 6, 7, 8, 9, DateTimeKind.Utc),
            GameId = loadout.Installation,
        };
        await tx.Commit();
    }

    private string[] TargetPaths(Loadout.ReadOnly loadout)
        => GamePathRows.TargetPaths(Connection.Db, loadout.Id).Select(r => r.Path).Order(StringComparer.Ordinal).ToArray();

    private async Task<LoadoutItemGroupId> AddGroupWithFiles(Loadout.ReadOnly loadout, string name, params string[] paths)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, name);
        foreach (var path in paths) AddFile(tx, loadout.LoadoutId, group, G(path), path);
        await tx.Commit();
        return group;
    }

    [Fact]
    public async Task Respell_RewritesTargetPathPinAndGameInstalledRows()
    {
        var loadout = await CreateLoadout();
        var group = await AddGroupWithFiles(loadout, "mod", "Data/interface/a.swf", "Data/Interface/b.swf");
        using (var tx = Connection.BeginTransaction())
        {
            _ = new LoadoutFileWinner.New(tx) { LoadoutId = loadout.LoadoutId, TargetPath = G("Data/interface/a.swf").ToGamePathParentTuple(loadout.Id), WinnerGroupId = group };
            _ = new GameInstalledFile.New(tx, out var fileId)
            {
                Path = G("Data/interface/g.swf").ToGamePathParentTuple(loadout.Id),
                Hash = Hash.From(1),
                Size = Size.From(1),
                LoadoutItem = new LoadoutItem.New(tx, fileId) { Name = "g.swf", LoadoutId = loadout.LoadoutId, ParentId = group },
            };
            _ = new GameInstalledClaim.New(tx)
            {
                LoadoutId = loadout.LoadoutId,
                Path = G("Data/interface/c.swf").ToGamePathParentTuple(loadout.Id),
                Source = "s",
                SourceId = "1",
                ClaimedAt = DateTimeOffset.UtcNow,
            };
            await tx.Commit();
        }

        var count = await GamePathRespeller.Respell(Connection, loadout.LoadoutId, loadout.Installation.Id, InterfaceIsCapitalised(), GamePathFilters.Empty);

        count.Should().Be(4);
        TargetPaths(loadout).Should().Equal("Data/Interface/a.swf", "Data/Interface/b.swf");
        GamePathRows.PinPaths(Connection.Db, loadout.Id).Select(r => r.Path).Should().Equal("Data/Interface/a.swf");
        GamePathRows.GameInstalledFilePaths(Connection.Db, loadout.Id).Select(r => r.Path).Should().Equal("Data/Interface/g.swf");
        GamePathRows.GameInstalledClaimPaths(Connection.Db, loadout.Id).Select(r => r.Path).Should().Equal("Data/Interface/c.swf");
        Connection.Db.Datoms(SilverModManager.Sdk.Games.GameInstallMetadata.RespellPending).Should().BeEmpty("the pending flag is cleared with the last chunk");
    }

    [Fact]
    public async Task Respell_RecreatesDiskStateRowsWithTheirIdentity()
    {
        var loadout = await CreateLoadout();
        await RecordDiskState(loadout, "Data/interface/x.swf", "payload");
        var before = DiskStateEntry.FindByGame(Connection.Db, loadout.Installation.Id).Single(e => ((GamePath)e.Path).Path.ToString().EndsWith("x.swf"));

        await GamePathRespeller.Respell(Connection, loadout.LoadoutId, loadout.Installation.Id, InterfaceIsCapitalised(), GamePathFilters.Empty);

        var after = DiskStateEntry.FindByGame(Connection.Db, loadout.Installation.Id).Where(e => ((GamePath)e.Path).Path.ToString().EndsWith("x.swf")).Should().ContainSingle().Subject;
        ((GamePath)after.Path).Path.ToString().Should().Be("Data/Interface/x.swf");
        after.IdentityOf().Should().Be(before.IdentityOf());
        after.Size.Should().Be(before.Size);
        after.LastModified.Should().Be(before.LastModified);
    }

    [Fact]
    public async Task FinishPending_CompletesMarkedRows()
    {
        var loadout = await CreateLoadout();
        await AddGroupWithFiles(loadout, "mod", "Data/Interface/a.swf");
        var row = GamePathRows.TargetPaths(Connection.Db, loadout.Id).Single();
        using (var tx = Connection.BeginTransaction())
        {
            tx.Retract(row.Row, LoadoutItemWithTargetPath.TargetPath, row.Tuple);
            tx.Add(row.Row, LoadoutItemWithTargetPath.TargetPath, (row.Parent, row.Location, RelativePath.FromUnsanitizedInput("Data/Interface/a.swf" + GamePathRespeller.Marker)));
            tx.Add(loadout.Installation.Id, SilverModManager.Sdk.Games.GameInstallMetadata.RespellPending, true);
            await tx.Commit();
        }
        TargetPaths(loadout).Should().Equal("Data/Interface/a.swf" + GamePathRespeller.Marker);

        var finished = await GamePathRespeller.FinishPending(Connection, loadout.Installation.Id, [loadout.LoadoutId], Logger);

        finished.Should().Be(1);
        TargetPaths(loadout).Should().Equal("Data/Interface/a.swf");
        Connection.Db.Datoms(SilverModManager.Sdk.Games.GameInstallMetadata.RespellPending).Should().BeEmpty();
    }

    [Fact]
    public async Task FinishPending_WithNoFlag_ReadsNothingAndChangesNothing()
    {
        var loadout = await CreateLoadout();
        await AddGroupWithFiles(loadout, "mod", "Data/Interface/a" + GamePathRespeller.Marker);

        (await GamePathRespeller.FinishPending(Connection, loadout.Installation.Id, [loadout.LoadoutId], Logger)).Should().Be(0);

        TargetPaths(loadout).Should().Equal("Data/Interface/a" + GamePathRespeller.Marker);
    }

    [Fact]
    public async Task Respell_ReadsRowsRecordedAfterTheCallersSnapshot()
    {
        var loadout = await CreateLoadout();
        var snapshot = Connection.Db;

        // What the reindex at the start of an apply does: records a file after the caller took its snapshot.
        await RecordDiskState(loadout, "Data/interface/late.swf", "late");
        await GamePathRespeller.Respell(Connection, loadout.LoadoutId, loadout.Installation.Id, InterfaceIsCapitalised(), GamePathFilters.Empty);

        snapshot.Should().NotBeSameAs(Connection.Db);
        DiskStateEntry.FindByGame(Connection.Db, loadout.Installation.Id)
            .Select(e => ((GamePath)e.Path).Path.ToString()).Where(p => p.EndsWith("late.swf"))
            .Should().Equal("Data/Interface/late.swf");
    }

    [Fact]
    public async Task Respell_ChunksRunBothPhases_NeverMoreThanOneChunkMarked()
    {
        var loadout = await CreateLoadout();
        await AddGroupWithFiles(loadout, "mod", Enumerable.Range(0, 7).Select(i => $"Data/interface/f{i}.swf").ToArray());
        var mostMarked = 0;
        using var subscription = Connection.Revisions.Subscribe(db =>
        {
            var marked = GamePathRows.TargetPaths(db, loadout.Id).Count(r => r.Path.EndsWith(GamePathRespeller.Marker, StringComparison.Ordinal));
            mostMarked = Math.Max(mostMarked, marked);
        });

        await GamePathRespeller.Respell(Connection, loadout.LoadoutId, loadout.Installation.Id, InterfaceIsCapitalised(), GamePathFilters.Empty, chunk: 3);

        mostMarked.Should().BeInRange(1, 3);
        TargetPaths(loadout).Should().OnlyContain(p => p.StartsWith("Data/Interface/"));
    }

    [Fact]
    public async Task Respell_LeavesFilteredPathsAlone()
    {
        var loadout = await CreateLoadout();
        await AddGroupWithFiles(loadout, "mod", "Data/interface/a.swf");

        await GamePathRespeller.Respell(Connection, loadout.LoadoutId, loadout.Installation.Id, InterfaceIsCapitalised(), GamePathFilters.Create(_ => true));

        TargetPaths(loadout).Should().Equal("Data/interface/a.swf");
    }
}
