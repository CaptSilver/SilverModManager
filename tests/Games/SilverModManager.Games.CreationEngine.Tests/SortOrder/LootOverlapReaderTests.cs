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

    private static byte[] StarfieldPlugin(PluginScale scale, string[] masters, params uint[] formIds) => PluginBytes.Write(PluginFormat.Tes4, new PluginSpec
    {
        Masters = masters,
        Blocks = [new GroupBlock("WEAP", [.. formIds.Select(id => new RecordBlock("WEAP", id))])],
    });

    private PluginLoadoutData StarfieldLoaded(string name, PluginScale? scale, string[] masters, params uint[] formIds) =>
        new(name, _streams.Add(StarfieldPlugin(scale ?? PluginScale.Full, masters, formIds)), true, "Mod", null, false, masters) { Scale = scale };

    [Fact]
    public async Task ResolvesStarfieldFormIdsByTheScaleOfEachMaster()
    {
        var starfield = StarfieldLoaded("Starfield.esm", PluginScale.Full, []);
        var medium = StarfieldLoaded("Medium.esp", PluginScale.Medium, ["Starfield.esm"]);
        // The medium master is second in the header but the first medium one, so 0xFD000123 names its record.
        var a = StarfieldLoaded("A.esp", PluginScale.Full, ["Starfield.esm", "Medium.esp"], 0xFD00_0123, 0x0000_0800);
        var b = StarfieldLoaded("B.esp", PluginScale.Full, ["Medium.esp"], 0xFD00_0123);

        var data = await Reader().Read(LootGame.Starfield, [starfield, medium, a, b], [], CancellationToken.None);

        data.Complete.Should().BeTrue();
        data.Plugins["A.esp"].Records!.OverrideCount.Should().Be(2);
        data.Plugins["A.esp"].Records!.Overlaps(data.Plugins["B.esp"].Records!).Should().BeTrue();
    }

    [Fact]
    public async Task StarfieldRecordsOfAPluginAreKeyedByItsOwnScale()
    {
        var medium = StarfieldLoaded("Medium.esp", PluginScale.Medium, [], 0x0500_3456);
        var overrider = StarfieldLoaded("B.esp", PluginScale.Full, ["Medium.esp"], 0xFD00_3456);

        var data = await Reader().Read(LootGame.Starfield, [medium, overrider], [], CancellationToken.None);

        data.Plugins["Medium.esp"].Records!.Overlaps(data.Plugins["B.esp"].Records!).Should().BeTrue();
    }

    [Fact]
    public async Task AStarfieldPluginWhoseMasterIsNotInTheLoadOrderHasNoRecordsAndMakesTheDataIncomplete()
    {
        var a = StarfieldLoaded("A.esp", PluginScale.Full, ["Gone.esm"], 0x0000_0800);
        var fine = StarfieldLoaded("Fine.esp", PluginScale.Full, [], 0x0000_0801);

        var data = await Reader().Read(LootGame.Starfield, [a, fine], [], CancellationToken.None);

        data.Complete.Should().BeFalse();
        data.Plugins["A.esp"].Records.Should().BeNull();
        data.Plugins["Fine.esp"].Records.Should().NotBeNull();
    }

    [Fact]
    public async Task AStarfieldPluginWhoseMasterHeaderCouldNotBeReadHasNoRecords()
    {
        // Without the header there is no scale, and a guess would number the masters wrongly.
        var master = StarfieldLoaded("Master.esm", scale: null, []);
        var a = StarfieldLoaded("A.esp", PluginScale.Full, ["Master.esm"], 0x0000_0800);

        var data = await Reader().Read(LootGame.Starfield, [master, a], [], CancellationToken.None);

        data.Complete.Should().BeFalse();
        data.Plugins["A.esp"].Records.Should().BeNull();
    }

    private static byte[] MorrowindPlugin(string[] masters, params string[] names) => PluginBytes.Write(PluginFormat.Tes3, new PluginSpec
    {
        Masters = masters,
        Blocks = [.. names.Select(n => new Tes3RecordBlock("GMST", ("NAME", [.. System.Text.Encoding.ASCII.GetBytes(n), (byte)0])))],
    });

    private PluginLoadoutData MorrowindLoaded(string name, string[] masters, params string[] names) =>
        new(name, _streams.Add(MorrowindPlugin(masters, names)), true, "Mod", null, false, masters);

    [Fact]
    public async Task ResolvesMorrowindRecordsAgainstTheRecordsOfTheMasters()
    {
        var master = MorrowindLoaded("Morrowind.esm", [], "sGold", "sSilver", "sCopper");
        var a = MorrowindLoaded("A.esp", ["Morrowind.esm"], "sGold", "sNew");
        var b = MorrowindLoaded("B.esp", ["Morrowind.esm"], "sGold");
        var c = MorrowindLoaded("C.esp", ["Morrowind.esm"], "sElse");

        var data = await Reader(PluginFormat.Tes3).Read(LootGame.Morrowind, [master, a, b, c], [], CancellationToken.None);

        data.Complete.Should().BeTrue();
        data.Plugins["Morrowind.esm"].Records!.OverrideCount.Should().Be(0);
        data.Plugins["A.esp"].Records!.OverrideCount.Should().Be(1);
        data.Plugins["A.esp"].Records!.Count.Should().Be(2);
        data.Plugins["A.esp"].Records!.Overlaps(data.Plugins["B.esp"].Records!).Should().BeTrue();
        data.Plugins["A.esp"].Records!.Overlaps(data.Plugins["C.esp"].Records!).Should().BeFalse();
    }

    [Fact]
    public async Task AMorrowindPluginWhoseMasterIsNotInTheLoadOrderHasNoRecords()
    {
        var a = MorrowindLoaded("A.esp", ["Gone.esm"], "sGold");

        var data = await Reader(PluginFormat.Tes3).Read(LootGame.Morrowind, [a], [], CancellationToken.None);

        data.Complete.Should().BeFalse();
        data.Plugins["A.esp"].Records.Should().BeNull();
    }

    [Fact]
    public async Task AnOpenMwScriptListHasNoRecordsAndIsNotOpened()
    {
        var scripts = new PluginLoadoutData("Mod.omwscripts", _streams.Add("lua: scripts/x.lua\n"u8.ToArray()), true, "Mod", null, false, []);
        var addon = MorrowindLoaded("Mod.omwaddon", [], "sGold");

        var data = await Reader(PluginFormat.Tes3).Read(LootGame.OpenMW, [scripts, addon], [], CancellationToken.None);

        data.Complete.Should().BeTrue();
        data.Plugins["Mod.omwscripts"].Records!.Count.Should().Be(0);
        _streams.Opens(scripts.Identity).Should().Be(0);
        data.Plugins["Mod.omwaddon"].Records!.Count.Should().Be(1);
    }

    [Fact]
    public async Task ASecondReadOfMorrowindPluginsOpensNothingAgain()
    {
        var master = MorrowindLoaded("Morrowind.esm", [], "sGold");
        var a = MorrowindLoaded("A.esp", ["Morrowind.esm"], "sGold");
        var reader = Reader(PluginFormat.Tes3);

        await reader.Read(LootGame.Morrowind, [master, a], [], CancellationToken.None);
        await reader.Read(LootGame.Morrowind, [master, a], [], CancellationToken.None);

        _streams.Opens(a.Identity).Should().Be(1);
        _streams.Opens(master.Identity).Should().Be(1);
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
