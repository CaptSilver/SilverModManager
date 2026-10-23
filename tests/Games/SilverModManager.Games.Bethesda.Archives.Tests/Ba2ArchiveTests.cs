using System.Buffers.Binary;
using NexusMods.Paths;
using static SilverModManager.Games.Bethesda.Archives.Tests.TestArchives;

namespace SilverModManager.Games.Bethesda.Archives.Tests;

public class Ba2ArchiveTests
{
    private const byte Bc1 = 71;
    private const byte Bc7 = 98;

    private static Task<IBethesdaArchiveReader> Open(byte[] archive) =>
        BethesdaArchive.Open(new MemoryStream(archive), CancellationToken.None);

    private static async Task<byte[]> Extract(IBethesdaArchiveEntry entry)
    {
        var ms = new MemoryStream();
        await entry.CopyToAsync(ms, CancellationToken.None);
        return ms.ToArray();
    }

    private static RelativePath P(string path) => RelativePath.FromUnsanitizedInput(path);

    // Open takes a stream, so a file name (.bsa or .ba2) never takes part: the magic alone picks the reader.
    [Fact]
    public async Task GeneralArchiveShouldOpenByMagicAndListPathsAndBytes()
    {
        var zipped = Pattern(300, 1);
        var stored = Pattern(40, 2);
        await using var reader = await Open(Ba2General(1, 0,
            new Ba2Entry("meshes\\a.nif", zipped, true),
            new Ba2Entry("sound\\b.txt", stored, false)));

        reader.Kind.Should().Be(ArchiveKind.Ba2);
        reader.Entries.Select(e => e.Path).Should().Equal(P("meshes/a.nif"), P("sound/b.txt"));
        reader.Entries.Select(e => e.Size).Should().Equal(300UL, 40UL);
        (await Extract(reader.Entries[0])).Should().Equal(zipped);
        (await Extract(reader.Entries[1])).Should().Equal(stored);
    }

    [Fact]
    public async Task GeneralArchiveShouldRecordArchiveAndFileStates()
    {
        await using var reader = await Open(Ba2General(1, 0,
            new Ba2Entry("a.txt", Pattern(30, 1), true),
            new Ba2Entry("b.txt", Pattern(10, 2), false)));

        reader.State.Should().Be(new Ba2ArchiveState(true, Ba2EntryType.Gnrl, "BTDX", 1, 0, 0, 0));
        reader.Entries[0].State.Should().Be(new Ba2FileState(
            Index: 0, Path: P("a.txt"), Extension: "txt\0", Compressed: true,
            Align: 0xBAADF00D, Flags: 0x00100100, DirHash: 0x20000000, NameHash: 0x10000000));
        ((Ba2FileState)reader.Entries[1].State).Compressed.Should().BeFalse();
        reader.Entries[1].State.Index.Should().Be(1);
    }

    [Fact]
    public async Task ArchiveWithoutANameTableShouldNameEntriesByTheirHash()
    {
        var bytes = Ba2General(1, 0, new Ba2Entry("a.txt", Pattern(10, 1), false));
        // Zero the name table offset and drop the table.
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), 0);
        await using var reader = await Open(bytes[..^(2 + 5)]);

        reader.State.Should().BeOfType<Ba2ArchiveState>().Which.HasNameTable.Should().BeFalse();
        reader.Entries[0].Path.Should().Be(P("10000000"));
    }

    [Fact]
    public async Task StarfieldGeneralArchiveShouldCarryItsExtraHeaderFields()
    {
        var data = Pattern(500, 3);
        await using var reader = await Open(Ba2General(3, 3, new Ba2Entry("x.bin", data, true)));

        reader.State.Should().Be(new Ba2ArchiveState(true, Ba2EntryType.Gnrl, "BTDX", 3, 1, 2, 3));
        (await Extract(reader.Entries[0])).Should().Equal(data);
    }

    [Fact]
    public async Task TextureArchiveShouldRejoinChunksBehindADdsHeader()
    {
        var top = Pattern(256, 4);   // chunk contents are arbitrary: the reader does not check them against the format
        var tail = Pattern(100, 5);
        await using var reader = await Open(Ba2Dx10(1, 0,
            new Ba2Texture("textures\\t.dds", 64, 32, 2, Bc1, (top, true), (tail, false))));

        reader.State.Should().Be(new Ba2ArchiveState(true, Ba2EntryType.Dx10, "BTDX", 1, 0, 0, 0));
        var entry = reader.Entries.Should().ContainSingle().Which;
        entry.Path.Should().Be(P("textures/t.dds"));

        var dds = await Extract(entry);
        dds.Should().HaveCount(128 + 356); // legacy BC1 header, no DX10 extension
        entry.Size.Should().Be((ulong)dds.Length);
        dds[..4].Should().Equal("DDS "u8.ToArray());
        dds[128..].Should().Equal(top.Concat(tail));
        Dds.Height(dds).Should().Be(32);
        Dds.Width(dds).Should().Be(64);
        Dds.MipCount(dds).Should().Be(2);
        Dds.FourCc(dds).Should().Be("DXT1");
    }

    [Fact]
    public async Task TextureOfAFormatWithoutALegacyPixelFormatShouldGetADx10Header()
    {
        var data = Pattern(64, 6);
        await using var reader = await Open(Ba2Dx10(1, 0, new Ba2Texture("t.dds", 8, 8, 1, Bc7, (data, false))));

        var dds = await Extract(reader.Entries[0]);
        dds.Should().HaveCount(148 + 64);
        Dds.FourCc(dds).Should().Be("DX10");
        BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(128)).Should().Be(Bc7);
        dds[148..].Should().Equal(data);
    }

    [Fact]
    public async Task TextureEntryShouldRecordEveryField()
    {
        await using var reader = await Open(Ba2Dx10(1, 0,
            new Ba2Texture("t.dds", 64, 32, 2, Bc1, (Pattern(20, 1), true), (Pattern(10, 2), false))));

        var state = reader.Entries[0].State.Should().BeOfType<Ba2Dx10FileState>().Subject;
        state.Should().Be(new Ba2Dx10FileState(
            Index: 0, Path: P("t.dds"),
            Chunks: [new Ba2Chunk(true, 0xBAADF00D, EndMip: 1, StartMip: 0, 20), new Ba2Chunk(false, 0xBAADF00D, EndMip: 2, StartMip: 1, 10)],
            PixelFormat: Bc1, NumMips: 2, Width: 64, Height: 32, ChunkHdrLen: 24, Unk8: 0,
            DirHash: 0x20000000, Extension: "dds\0", NameHash: 0x10000000, IsCubeMap: 0, TileMode: 8));
    }

    [Fact]
    public async Task StarfieldTextureChunksShouldDecodeAsLz4Blocks()
    {
        var a = Pattern(400, 7);
        var b = Pattern(120, 8);
        await using var reader = await Open(Ba2Dx10(3, 3,
            new Ba2Texture("t.dds", 32, 16, 2, Bc1, (a, true), (b, true))));

        var dds = await Extract(reader.Entries[0]);
        dds[128..].Should().Equal(a.Concat(b));
    }

    [Fact]
    public async Task TruncatedArchiveShouldThrowAnArchiveException()
    {
        var bytes = Ba2General(1, 0, new Ba2Entry("a.txt", Pattern(200, 1), false));
        var act = () => Open(bytes[..30]);
        await act.Should().ThrowAsync<BethesdaArchiveException>();
    }

    [Fact]
    public async Task TextureArchiveClaimingMoreRecordsThanItHoldsShouldThrowAnArchiveException()
    {
        var bytes = Ba2Dx10(1, 0, new Ba2Texture("t.dds", 4, 4, 1, Bc1, (Pattern(8, 1), false)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), uint.MaxValue);
        var act = () => Open(bytes);
        await act.Should().ThrowAsync<BethesdaArchiveException>();
    }

    [Fact]
    public async Task CorruptCompressedEntryShouldThrowAnArchiveException()
    {
        var bytes = Ba2General(1, 0, new Ba2Entry("a.txt", Pattern(200, 1), true));
        var dataStart = 24 + 36;
        for (var i = 0; i < 12; i++) bytes[dataStart + i] = 0xFF;
        await using var reader = await Open(bytes);

        var act = () => Extract(reader.Entries[0]);
        await act.Should().ThrowAsync<BethesdaArchiveException>();
    }

    [Fact]
    public async Task PlayStationTextureArchivesShouldBeRefusedLoudly()
    {
        var bytes = Ba2General(1, 0, new Ba2Entry("a.txt", Pattern(10, 1), false));
        "GNMF"u8.CopyTo(bytes.AsSpan(8));
        var act = () => Open(bytes);
        (await act.Should().ThrowAsync<BethesdaArchiveException>()).WithMessage("*GNMF*");
    }

    /// <summary>Reads the DDS header fields straight from their documented offsets.</summary>
    private static class Dds
    {
        public static uint Height(byte[] dds) => BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(12));
        public static uint Width(byte[] dds) => BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(16));
        public static uint MipCount(byte[] dds) => BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(28));
        public static string FourCc(byte[] dds) => System.Text.Encoding.ASCII.GetString(dds, 84, 4);
    }
}
