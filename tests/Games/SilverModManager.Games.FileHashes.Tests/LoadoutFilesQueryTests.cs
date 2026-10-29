using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FileHashes.Tests;

/// <summary>
/// <c>file_hashes.loadout_files</c> as the synchronizer calls it, with only the main store's handle,
/// against a real query engine holding a downloaded store ("hashes") and a local one ("local_hashes").
/// </summary>
public sealed class LoadoutFilesQueryTests : IDisposable
{
    private readonly RealQueryEngineHost _host = new();
    private readonly IConnection _main;
    private readonly IConnection _central;
    private readonly IConnection _local;

    public LoadoutFilesQueryTests()
    {
        _main = _host.OpenInMemory("main");
        _central = _host.OpenInMemory("hashes");
        _local = _host.OpenInMemory("local_hashes");
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task LoadoutFiles_ReturnsVanillaRowsFromBothHashStores()
    {
        await HashRows.AddSteamManifest(_central, 1001, ("a.esm", "central file"));
        await HashRows.AddSteamManifest(_local, 1002, ("b.esm", "local file"));
        var loadout = await HashRows.AddLoadout(_main, GameStore.Steam, "/games/test", "1001", "1002");

        var rows = LoadoutFiles(loadout);

        rows.Select(r => r.Path.ToString()).Should().BeEquivalentTo(["a.esm", "b.esm"]);
    }

    [Fact]
    public async Task LoadoutFiles_SamePathInBothStores_YieldsOneRow()
    {
        // Passes before the local store is read too: the union has to keep deduplicating identical
        // value rows, because the same manifest content can be in both stores.
        await HashRows.AddSteamManifest(_central, 1001, ("a.esm", "same content"));
        await HashRows.AddSteamManifest(_local, 1002, ("a.esm", "same content"));
        var loadout = await HashRows.AddLoadout(_main, GameStore.Steam, "/games/test", "1001", "1002");

        var rows = LoadoutFiles(loadout);

        rows.Should().ContainSingle().Which.Path.ToString().Should().Be("a.esm");
    }

    [Fact]
    public async Task LoadoutFiles_PathsAreResolvedWithinTheStoreThatHoldsThem()
    {
        // Entity ids are per store, so a path row in one store and a hash row in the other can share an id.
        // Two files in each store give both stores overlapping ids; every row must keep its own hash.
        await HashRows.AddSteamManifest(_central, 1001, ("a.esm", "central one"), ("a2.esm", "central two"));
        await HashRows.AddSteamManifest(_local, 1002, ("b.esm", "local one"), ("b2.esm", "local two"));
        var loadout = await HashRows.AddLoadout(_main, GameStore.Steam, "/games/test", "1001", "1002");

        var rows = LoadoutFiles(loadout).ToDictionary(r => r.Path.ToString(), r => r.Hash);

        rows.Should().HaveCount(4);
        rows["a.esm"].Should().Be(await HashOf("central one"));
        rows["a2.esm"].Should().Be(await HashOf("central two"));
        rows["b.esm"].Should().Be(await HashOf("local one"));
        rows["b2.esm"].Should().Be(await HashOf("local two"));
    }

    [Fact]
    public async Task GogLoadoutFiles_ReadsBothStores()
    {
        await HashRows.AddGogBuild(_central, 100, ("a.esm", "central file"));
        await HashRows.AddGogBuild(_local, 200, ("b.esm", "local file"));
        var loadout = await HashRows.AddLoadout(_main, GameStore.GOG, "/games/test", "100", "200");

        var rows = LoadoutFiles(loadout);

        rows.Select(r => r.Path.ToString()).Should().BeEquivalentTo(["a.esm", "b.esm"]);
    }

    [Fact]
    public async Task LoadoutFiles_IncludesASnapshotBaselineMatchedByStoreAndPath()
    {
        await LocalBaselineRows.AddLocalBaseline(_local, GameStore.Steam, "/games/test", LocalBaselineKind.Snapshot,
            [("a.esm", "one"), ("b.esm", "two")]);
        var loadout = await HashRows.AddLoadout(_main, GameStore.Steam, "/games/test");

        var rows = LoadoutFiles(loadout);

        rows.Select(r => r.Path.ToString()).Should().BeEquivalentTo(["a.esm", "b.esm"]);
    }

    [Fact]
    public async Task LoadoutFiles_BaselineForAnotherPath_IsNotReturned()
    {
        await LocalBaselineRows.AddLocalBaseline(_local, GameStore.Steam, "/games/elsewhere", LocalBaselineKind.Snapshot,
            [("a.esm", "one"), ("b.esm", "two")]);
        var loadout = await HashRows.AddLoadout(_main, GameStore.Steam, "/games/test");

        LoadoutFiles(loadout).Should().BeEmpty();
    }

    [Fact]
    public async Task LoadoutFiles_BaselineForAnotherStore_IsNotReturned()
    {
        await LocalBaselineRows.AddLocalBaseline(_local, GameStore.GOG, "/games/test", LocalBaselineKind.Snapshot, [("a.esm", "one")]);
        var loadout = await HashRows.AddLoadout(_main, GameStore.Steam, "/games/test");

        LoadoutFiles(loadout).Should().BeEmpty();
    }

    [Fact]
    public async Task LoadoutFiles_PathReportedNonVanilla_IsRemovedEvenWhenTheCentralStoreKnowsIt()
    {
        await HashRows.AddSteamManifest(_central, 1001, ("x.esm", "vanilla x"), ("y.esm", "vanilla y"));
        await LocalBaselineRows.AddLocalBaseline(_local, GameStore.Steam, "/games/test", LocalBaselineKind.DepotManifests,
            [], "x.esm");
        await HashRows.BuiltForLocatorIds(_local, "/games/test", "1001");
        var loadout = await HashRows.AddLoadout(_main, GameStore.Steam, "/games/test", "1001");

        var rows = LoadoutFiles(loadout);

        rows.Select(r => r.Path.ToString()).Should().BeEquivalentTo(["y.esm"], "the baseline found x.esm modified, so it has no Layer 0 row");
    }

    [Fact]
    public async Task LoadoutFiles_PathInASnapshotBaseline_TakesTheLocalHashOverTheCentralOne()
    {
        await HashRows.AddSteamManifest(_central, 1001, ("y.esm", "central y"));
        await LocalBaselineRows.AddLocalBaseline(_local, GameStore.Steam, "/games/test", LocalBaselineKind.Snapshot,
            [("y.esm", "local y")]);
        await HashRows.BuiltForLocatorIds(_local, "/games/test", "1001");
        var loadout = await HashRows.AddLoadout(_main, GameStore.Steam, "/games/test", "1001");

        var rows = LoadoutFiles(loadout);

        rows.Should().ContainSingle().Which.Hash.Should().Be(await HashOf("local y"));
    }

    [Fact]
    public async Task LoadoutFiles_PathReportedNonVanilla_IsRemovedFromTheLocalManifestRowsToo()
    {
        // A depot-manifest baseline's manifest lists the modified file as well, with the vanilla hash.
        await HashRows.AddSteamManifest(_local, 1001, ("x.esm", "vanilla x"), ("y.esm", "vanilla y"));
        await LocalBaselineRows.AddLocalBaseline(_local, GameStore.Steam, "/games/test", LocalBaselineKind.DepotManifests,
            [("y.esm", "vanilla y")], "x.esm");
        await HashRows.BuiltForLocatorIds(_local, "/games/test", "1001");
        var loadout = await HashRows.AddLoadout(_main, GameStore.Steam, "/games/test", "1001");

        LoadoutFiles(loadout).Select(r => r.Path.ToString()).Should().BeEquivalentTo(["y.esm"]);
    }

    [Fact]
    public async Task LoadoutFiles_BaselineOfAnotherInstallation_LeavesCentralRowsAlone()
    {
        await HashRows.AddSteamManifest(_central, 1001, ("x.esm", "vanilla x"));
        await LocalBaselineRows.AddLocalBaseline(_local, GameStore.Steam, "/games/elsewhere", LocalBaselineKind.DepotManifests,
            [], "x.esm");
        var loadout = await HashRows.AddLoadout(_main, GameStore.Steam, "/games/test", "1001");

        LoadoutFiles(loadout).Select(r => r.Path.ToString()).Should().BeEquivalentTo(["x.esm"]);
    }

    [Fact]
    public async Task LoadoutFiles_GameMoved_ReadsTheBaselineAtTheNewPathOnceTheRowFollows()
    {
        await HashRows.AddSteamManifest(_central, 1001, ("x.esm", "vanilla x"), ("y.esm", "vanilla y"));
        await LocalBaselineRows.AddLocalBaseline(_local, GameStore.Steam, "/mnt/steam/test", LocalBaselineKind.DepotManifests,
            [], "x.esm");
        await HashRows.BuiltForLocatorIds(_local, "/mnt/steam/test", "1001");
        var loadout = await HashRows.AddLoadout(_main, GameStore.Steam, "/home/steam/test", "1001");

        LoadoutFiles(loadout).Select(r => r.Path.ToString()).Should().BeEquivalentTo(["x.esm", "y.esm"], "no baseline is at the old path");

        await MoveInstall(loadout, "/mnt/steam/test");

        LoadoutFiles(loadout).Select(r => r.Path.ToString()).Should().BeEquivalentTo(["y.esm"], "the baseline at the new path found x.esm modified");
    }

    [Fact]
    public async Task LoadoutFiles_GameMoved_ReadsASnapshotBaselineAtTheNewPath()
    {
        await LocalBaselineRows.AddLocalBaseline(_local, GameStore.Steam, "/mnt/steam/test", LocalBaselineKind.Snapshot, [("a.esm", "one")]);
        var loadout = await HashRows.AddLoadout(_main, GameStore.Steam, "/home/steam/test");

        LoadoutFiles(loadout).Should().BeEmpty();

        await MoveInstall(loadout, "/mnt/steam/test");

        LoadoutFiles(loadout).Select(r => r.Path.ToString()).Should().BeEquivalentTo(["a.esm"]);
    }

    private async Task MoveInstall(EntityId loadout, string newPath)
    {
        var install = Loadout.Load(_main.Db, loadout).Installation;
        using var tx = _main.BeginTransaction();
        tx.Add(install.Id, GameInstallMetadata.Path, newPath);
        await tx.Commit();
    }

    [Fact]
    public async Task DbName_AcceptsAMacroParameter()
    {
        // Decides whether each arm is written once and called per store, or duplicated under two literals.
        using var host = new RealQueryEngineHost("CREATE MACRO test_by_name(name) AS TABLE SELECT XxHash3 FROM MDB_HASHRELATION(DbName=>name);");
        var store = host.OpenInMemory("hashes");
        var expected = await RealQueryEngineHost.AddHashRow(store, "row");

        var hashes = store.Query<Hash>($"SELECT XxHash3 FROM test_by_name('hashes')").ToList();

        hashes.Should().ContainSingle().Which.Should().Be(expected);
    }

    [Fact]
    public async Task LoadoutFiles_IdentityIsTheXxHash3IdentityOfTheRowsHash()
    {
        await HashRows.AddSteamManifest(_central, 1001, ("a.esm", "central file"), ("b.esm", "another file"));
        var loadout = await HashRows.AddLoadout(_main, GameStore.Steam, "/games/test", "1001");

        var rows = _main.Query<(RelativePath Path, Hash Hash, string Identity)>(
            $"SELECT Path, Hash, Identity FROM file_hashes.loadout_files({_main.Db}) WHERE Loadout = {loadout.Value}").ToList();

        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => FileIdentity.ParseHex(r.Identity).Equals(FileIdentity.XxHash3(r.Hash)));
    }

    [Fact]
    public async Task LoadoutFiles_ListsAnAttestedRowWithItsSha1IdentityAndNoXxHash3()
    {
        await HashRows.AddBaselineOwningManifest(_local, LocalBaselineKind.DepotManifests, 1002);
        await HashRows.AddAttestedFile(_local, 1002, "a.esm", "attested file");
        var loadout = await HashRows.AddLoadout(_main, GameStore.Steam, "/games/test", "1002");

        var rows = _main.Query<(RelativePath Path, Hash? Hash, string Identity, Size Size)>(
            $"SELECT Path, Hash, Identity, Size FROM file_hashes.loadout_files({_main.Db}) WHERE Loadout = {loadout.Value}").ToList();

        var expected = (await MultiHasher.HashStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("attested file"))));
        var row = rows.Should().ContainSingle().Subject;
        row.Path.ToString().Should().Be("a.esm");
        row.Hash.Should().BeNull("nothing was read, so there is no xxHash3 to list");
        FileIdentity.ParseHex(row.Identity).Should().Be(FileIdentity.Sha1(expected.Sha1), "hex() over the Sha1 column gives the digits the service builds");
        row.Size.Should().Be(expected.Size);
    }

    [Fact]
    public async Task LoadoutFiles_AnAttestedPathReplacesTheDownloadedStoresRowForIt()
    {
        await HashRows.AddSteamManifest(_central, 1001, ("a.esm", "central file"), ("b.esm", "central other"));
        await HashRows.AddBaselineOwningManifest(_local, LocalBaselineKind.DepotManifests, 1002);
        await HashRows.AddAttestedFile(_local, 1002, "a.esm", "attested file");
        await HashRows.BuiltForLocatorIds(_local, "/games/test", "1001", "1002");
        var loadout = await HashRows.AddLoadout(_main, GameStore.Steam, "/games/test", "1001", "1002");

        var rows = _main.Query<(RelativePath Path, string Identity)>(
            $"SELECT Path, Identity FROM file_hashes.loadout_files({_main.Db}) WHERE Loadout = {loadout.Value}").ToList();

        rows.Should().HaveCount(2);
        rows.Single(r => r.Path.ToString() == "a.esm").Identity.Should().StartWith("02");
        rows.Single(r => r.Path.ToString() == "b.esm").Identity.Should().StartWith("01");
    }

    [Fact]
    public async Task LoadoutFiles_ABaselineBuiltForOtherLocatorIds_ContributesNoRows()
    {
        // The game updated from build 1001 to 1002 and the downloaded store knows 1002. The old baseline's vanilla,
        // attested and non-vanilla rows describe files the new build replaced, so the new manifest answers alone.
        await HashRows.AddSteamManifest(_central, 1002, ("a.esm", "new a"), ("x.esm", "new x"));
        await HashRows.AddBaselineOwningManifest(_local, LocalBaselineKind.DepotManifests, 1001, ("b.esm", "old b"));
        await HashRows.AddAttestedFile(_local, 1001, "a.esm", "old a");
        using (var tx = _local.BeginTransaction())
        {
            _ = new NonVanillaFile.New(tx)
            {
                BaselineId = LocalBaseline.FindByGamePath(_local.Db, "/games/test").Single().Id,
                Path = RelativePath.FromUnsanitizedInput("x.esm"),
                Reason = NonVanillaReason.HashDiffers,
                Size = Size.From(1),
            };
            await tx.Commit();
        }
        var loadout = await HashRows.AddLoadout(_main, GameStore.Steam, "/games/test", "1002");

        var rows = _main.Query<(RelativePath Path, string Identity)>(
            $"SELECT Path, Identity FROM file_hashes.loadout_files({_main.Db}) WHERE Loadout = {loadout.Value}").ToList();

        rows.Select(r => r.Path.ToString()).Should().BeEquivalentTo(["a.esm", "x.esm"], "the old baseline's b.esm and its verdict on x.esm are gone");
        FileIdentity.ParseHex(rows.Single(r => r.Path.ToString() == "a.esm").Identity).Should().Be(FileIdentity.XxHash3(await HashOf("new a")), "not the old build's SHA-1 row");
    }

    [Fact]
    public async Task LoadoutFiles_ABaselineBuiltForTheLoadoutsLocatorIds_CountsEvenWhenAnIdIsListedTwice()
    {
        await HashRows.AddBaselineOwningManifest(_local, LocalBaselineKind.DepotManifests, 1001, ("b.esm", "b"));
        await HashRows.AddAttestedFile(_local, 1001, "a.esm", "a");
        var loadout = await HashRows.AddLoadout(_main, GameStore.Steam, "/games/test", "1001", "1001");

        LoadoutFiles(loadout).Select(r => r.Path.ToString()).Should().BeEquivalentTo(["a.esm", "b.esm"]);
    }

    [Theory]
    [InlineData(0xABUL, "0100000000000000AB")]
    [InlineData(0xFFFFFFFFFFFFFFFFUL, "01FFFFFFFFFFFFFFFF")]
    [InlineData(0x0123456789ABCDEFUL, "010123456789ABCDEF")]
    public void Xxhash3IdentityMacro_PadsTheDigestToSixteenDigitsAfterTheKindByte(ulong value, string expected)
    {
        var hex = _main.Query<string>($"SELECT file_hashes.xxhash3_identity(CAST({value} AS UBIGINT))").Single();

        hex.Should().BeEquivalentTo(expected);
        FileIdentity.ParseHex(hex).Should().Be(FileIdentity.XxHash3(Hash.From(value)));
    }

    [Theory]
    [InlineData(0xABUL)]
    [InlineData(0xFFFFFFFFFFFFFFFFUL)]
    [InlineData(0x0123456789ABCDEFUL)]
    public void Xxhash3IdentityMacro_SpellsTheIdentityLikeToHex(ulong value)
    {
        var hex = _main.Query<string>($"SELECT file_hashes.xxhash3_identity(CAST({value} AS UBIGINT))").Single();

        hex.Should().Be(FileIdentity.XxHash3(Hash.From(value)).ToHex(), "count(DISTINCT Identity) and string compares need one spelling");
    }

    private List<(RelativePath Path, Hash Hash, Size Size)> LoadoutFiles(EntityId loadout)
    {
        return _main.Query<(RelativePath Path, Hash Hash, Size Size)>(
            $"SELECT Path, Hash, Size FROM file_hashes.loadout_files({_main.Db}) WHERE Loadout = {loadout.Value}"
        ).ToList();
    }

    private static async Task<Hash> HashOf(string content)
        => (await MultiHasher.HashStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)))).XxHash3;
}
