using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Games.Bethesda.Archives;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.LoadOrder.Tests;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Games.CreationEngine.Loot;
using SilverModManager.Games.CreationEngine.SortOrder;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Games.CreationEngine.Tests.SortOrder;

/// <summary>
/// What the sorter is given for a load order: records resolved against masters, the files of the archives each
/// plugin loads, and what is cached between sorts.
/// </summary>
public class LootOverlapReaderTests
{
    private readonly FakeStreams _streams = new();

    private LootOverlapReader Reader(PluginFormat format = PluginFormat.Tes4) =>
        new(new PluginOverlapCache(_streams, format, NullLogger.Instance), format, NullLogger.Instance);

    private static byte[] Plugin(params uint[] formIds) => PluginBytes.Write(PluginFormat.Tes4, new PluginSpec
    {
        Masters = ["Skyrim.esm"],
        Blocks = [new GroupBlock("WEAP", [.. formIds.Select(id => new RecordBlock("WEAP", id))])],
    });

    private static async Task<byte[]> Archive(params string[] files)
    {
        using var temporary = new TemporaryFileManager(FileSystem.Shared);
        await using var builder = BethesdaArchiveBuilder.Create(new Tes5ArchiveState("BSA\0", 104, 0x3, 0), temporary);
        foreach (var file in files)
            await builder.AddFile(new Tes5FileState(0, RelativePath.FromUnsanitizedInput(file), false), new MemoryStream("data"u8.ToArray()), CancellationToken.None);

        var output = new MemoryStream();
        await builder.Build(output, CancellationToken.None);
        return output.ToArray();
    }

    private PluginLoadoutData Loaded(string name, byte[] bytes) =>
        new(name, _streams.Add(bytes), true, "Mod", null, false, ["Skyrim.esm"]);

    [Fact]
    public async Task ResolvesEachPluginsRecordsAgainstItsMasters()
    {
        var a = Loaded("A.esp", Plugin(0x0000_0D62));
        var z = Loaded("Z.esp", Plugin(0x0000_0D62, 0x0000_0D63, 0x0100_0800));

        var data = await Reader().Read(LootGame.SkyrimSE, [a, z], [], CancellationToken.None);

        data.Complete.Should().BeTrue();
        data.Plugins["A.esp"].Records!.OverrideCount.Should().Be(1);
        data.Plugins["Z.esp"].Records!.OverrideCount.Should().Be(2);
        data.Plugins["A.esp"].Records!.Overlaps(data.Plugins["Z.esp"].Records!).Should().BeTrue();
    }

    [Fact]
    public async Task GivesEachPluginTheFilesOfTheArchivesItLoads()
    {
        var a = Loaded("A.esp", Plugin());
        var z = Loaded("Z.esp", Plugin());
        var aArchive = _streams.Add(await Archive("meshes/x.nif"));
        var zArchive = _streams.Add(await Archive("meshes/x.nif", "meshes/y.nif"));

        var data = await Reader().Read(LootGame.SkyrimSE, [a, z], [("A.bsa", aArchive), ("Z.bsa", zArchive)], CancellationToken.None);

        data.Plugins["A.esp"].Assets.Count.Should().Be(1);
        data.Plugins["Z.esp"].Assets.Count.Should().Be(2);
        data.Plugins["A.esp"].Assets.Overlaps(data.Plugins["Z.esp"].Assets).Should().BeTrue();
    }

    [Fact]
    public async Task APluginWithNoArchiveHasNoAssets()
    {
        var data = await Reader().Read(LootGame.SkyrimSE, [Loaded("A.esp", Plugin())], [("Other.bsa", _streams.Add(await Archive("meshes/x.nif")))], CancellationToken.None);

        data.Plugins["A.esp"].Assets.Count.Should().Be(0);
    }

    [Fact]
    public async Task ReadsAnArchiveSeveralPluginsLoadOnce()
    {
        var a = Loaded("Mod.esp", Plugin());
        var b = Loaded("ModB.esp", Plugin());
        var shared = _streams.Add(await Archive("meshes/x.nif"));

        var data = await Reader().Read(LootGame.Fallout4, [a, b], [("ModB - Main.ba2", shared)], CancellationToken.None);

        data.Plugins["Mod.esp"].Assets.Count.Should().Be(1);
        data.Plugins["ModB.esp"].Assets.Count.Should().Be(1);
        _streams.Opens(shared).Should().Be(1);
    }

    [Fact]
    public async Task ASecondReadOfTheSamePluginsOpensNothingAgain()
    {
        var a = Loaded("A.esp", Plugin(0x0000_0D62));
        var archive = _streams.Add(await Archive("meshes/x.nif"));
        var reader = Reader();

        await reader.Read(LootGame.SkyrimSE, [a], [("A.bsa", archive)], CancellationToken.None);
        await reader.Read(LootGame.SkyrimSE, [a], [("A.bsa", archive)], CancellationToken.None);

        _streams.Opens(a.Identity).Should().Be(1);
        _streams.Opens(archive).Should().Be(1);
    }

    [Fact]
    public async Task APluginThatCannotBeReadHasNoRecordsAndMakesTheDataIncomplete()
    {
        var broken = Loaded("Broken.esp", "not a plugin"u8.ToArray());
        var fine = Loaded("Fine.esp", Plugin(0x0000_0D62));

        var data = await Reader().Read(LootGame.SkyrimSE, [broken, fine], [], CancellationToken.None);

        data.Complete.Should().BeFalse();
        data.Plugins["Broken.esp"].Records.Should().BeNull();
        data.Plugins["Fine.esp"].Records!.Count.Should().Be(1);
    }

    [Fact]
    public async Task AnArchiveThatCannotBeReadIsLeftOutAndDoesNotMakeTheDataIncomplete()
    {
        var a = Loaded("A.esp", Plugin());
        var bad = _streams.Add("not an archive"u8.ToArray());

        var data = await Reader().Read(LootGame.SkyrimSE, [a], [("A.bsa", bad)], CancellationToken.None);

        data.Complete.Should().BeTrue();
        data.Plugins["A.esp"].Assets.Count.Should().Be(0);
    }

    [Fact]
    public async Task APluginTheStoreHasNoBytesForIsReadAgainOnTheNextSort()
    {
        var bytes = Plugin(0x0000_0D62);
        var missing = new PluginLoadoutData("A.esp", FileIdentity.XxHash3(bytes.xxHash3()), true, "Mod", null, false, ["Skyrim.esm"]);
        var reader = Reader();

        var first = await reader.Read(LootGame.SkyrimSE, [missing], [], CancellationToken.None);
        _streams.Add(bytes);
        var second = await reader.Read(LootGame.SkyrimSE, [missing], [], CancellationToken.None);

        first.Plugins["A.esp"].Records.Should().BeNull();
        first.Complete.Should().BeFalse();
        second.Plugins["A.esp"].Records!.Count.Should().Be(1);
    }

    [Theory]
    [InlineData(PluginFormat.Tes4, LootGame.SkyrimSE, true)]
    [InlineData(PluginFormat.Tes4, LootGame.Fallout4, true)]
    [InlineData(PluginFormat.Tes4Oblivion, LootGame.Oblivion, true)]
    [InlineData(PluginFormat.Tes4, LootGame.Starfield, false)]
    [InlineData(PluginFormat.Tes3, LootGame.Morrowind, false)]
    [InlineData(PluginFormat.Tes3, LootGame.OpenMW, false)]
    public void CoversTheGamesWhoseFormIdsAreComparedAsTheyStand(PluginFormat format, LootGame game, bool covered)
    {
        Reader(format).Covers(game).Should().Be(covered);
    }

    private sealed class FakeStreams : IStreamSourceDispatcher
    {
        private readonly Dictionary<FileIdentity, byte[]> _files = new();
        private readonly Dictionary<FileIdentity, int> _opens = new();

        public FileIdentity Add(byte[] bytes)
        {
            var identity = FileIdentity.XxHash3(bytes.xxHash3());
            _files[identity] = bytes;
            return identity;
        }

        public int Opens(FileIdentity identity) => _opens.GetValueOrDefault(identity);

        public ValueTask<Stream?> OpenAsync(FileIdentity identity, CancellationToken cancellationToken = default)
        {
            lock (_opens) _opens[identity] = _opens.GetValueOrDefault(identity) + 1;
            return ValueTask.FromResult<Stream?>(_files.TryGetValue(identity, out var bytes) ? new MemoryStream(bytes) : null);
        }

        public bool Exists(FileIdentity identity) => _files.ContainsKey(identity);
    }
}
