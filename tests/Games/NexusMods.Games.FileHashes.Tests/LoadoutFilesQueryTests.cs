using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Hashes;

namespace NexusMods.Games.FileHashes.Tests;

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
    public async Task DbName_AcceptsAMacroParameter()
    {
        // Decides whether each arm is written once and called per store, or duplicated under two literals.
        using var host = new RealQueryEngineHost("CREATE MACRO test_by_name(name) AS TABLE SELECT XxHash3 FROM MDB_HASHRELATION(DbName=>name);");
        var store = host.OpenInMemory("hashes");
        var expected = await RealQueryEngineHost.AddHashRow(store, "row");

        var hashes = store.Query<Hash>($"SELECT XxHash3 FROM test_by_name('hashes')").ToList();

        hashes.Should().ContainSingle().Which.Should().Be(expected);
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
