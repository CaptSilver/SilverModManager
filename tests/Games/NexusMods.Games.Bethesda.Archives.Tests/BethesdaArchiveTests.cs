using System.Text;
using NexusMods.Paths;
using static NexusMods.Games.Bethesda.Archives.Tests.TestArchives;

namespace NexusMods.Games.Bethesda.Archives.Tests;

public class BethesdaArchiveTests
{
    private static Task<IBethesdaArchiveReader> Open(byte[] archive) =>
        BethesdaArchive.Open(new MemoryStream(archive), CancellationToken.None);

    private static async Task<byte[]> Extract(IBethesdaArchiveEntry entry)
    {
        var ms = new MemoryStream();
        await entry.CopyToAsync(ms, CancellationToken.None);
        return ms.ToArray();
    }

    // Open takes a stream, so a file name never takes part: the magic alone picks the reader.
    [Fact]
    public async Task OpenShouldDetectTes3ByMagic()
    {
        await using var reader = await Open(Tes3(new Tes3Entry("meshes\\a.nif", Pattern(10, 1))));
        reader.Kind.Should().Be(ArchiveKind.Tes3);
    }

    [Fact]
    public async Task OpenShouldDetectTes5ByMagic()
    {
        await using var reader = await Open(Tes5(104, FolderAndFileNames,
            new Tes5Entry("meshes", "a.nif", Pattern(10, 1), false)));
        reader.Kind.Should().Be(ArchiveKind.Tes5);
    }

    [Fact]
    public async Task Tes3ShouldListPathsSizesAndBytes()
    {
        var a = Pattern(100, 1);
        var b = Pattern(33, 2);
        await using var reader = await Open(Tes3(new Tes3Entry("meshes\\a.nif", a), new Tes3Entry("textures\\b.dds", b)));

        reader.Entries.Select(e => e.Path).Should().Equal(
            RelativePath.FromUnsanitizedInput("meshes/a.nif"),
            RelativePath.FromUnsanitizedInput("textures/b.dds"));
        reader.Entries.Select(e => e.Size).Should().Equal(100UL, 33UL);
        (await Extract(reader.Entries[0])).Should().Equal(a);
        (await Extract(reader.Entries[1])).Should().Equal(b);
    }

    [Fact]
    public async Task Tes3ShouldRecordArchiveAndFileStates()
    {
        var bytes = Tes3(new Tes3Entry("a.txt", Pattern(5, 1)), new Tes3Entry("bb.txt", Pattern(6, 2)));
        await using var reader = await Open(bytes);

        // header (12) + 2 * (size, offset) + 2 name offsets + names + hash table
        var hashOffset = 2u * 8 + 2 * 4 + 6 + 7;
        reader.State.Should().Be(new Tes3ArchiveState(
            FileCount: 2, DataOffset: 12 + hashOffset + 2 * 8, HashOffset: hashOffset, VersionNumber: 0x100));
        reader.Entries[1].State.Should().Be(new Tes3FileState(
            Index: 1, Path: RelativePath.FromUnsanitizedInput("bb.txt"), Offset: 5, NameOffset: 6,
            Hash1: 0xAA000001, Hash2: 0xBB000001, Size: 6));
    }

    [Theory]
    [InlineData(103u)]
    [InlineData(104u)]
    public async Task Tes5ZlibArchivesShouldExtractCompressedAndStoredEntries(uint version)
    {
        var packed = Pattern(5000, 3);
        var stored = Pattern(77, 4);
        await using var reader = await Open(Tes5(version, FolderAndFileNames | DefaultCompressed,
            new Tes5Entry("meshes\\armor", "cuirass.nif", packed, true),
            new Tes5Entry("textures", "skin.dds", stored, true)));

        reader.State.Should().Be(new Tes5ArchiveState("BSA\0", version, FolderAndFileNames | DefaultCompressed, 3));
        reader.Entries.Select(e => e.Path).Should().Equal(
            RelativePath.FromUnsanitizedInput("meshes/armor/cuirass.nif"),
            RelativePath.FromUnsanitizedInput("textures/skin.dds"));
        reader.Entries.Select(e => e.Size).Should().Equal(5000UL, 77UL);
        (await Extract(reader.Entries[0])).Should().Equal(packed);
        (await Extract(reader.Entries[1])).Should().Equal(stored);
    }

    [Fact]
    public async Task Tes5Version105ShouldDecodeLz4Frames()
    {
        var packed = Pattern(20_000, 5);
        await using var reader = await Open(Tes5(105, FolderAndFileNames | DefaultCompressed,
            new Tes5Entry("sound", "voice.fuz", packed, true),
            new Tes5Entry("sound", "other.fuz", Pattern(40, 6), true)));

        reader.Entries.Select(e => e.Size).Should().Equal(20_000UL, 40UL);
        (await Extract(reader.Entries[0])).Should().Equal(packed);
        (await Extract(reader.Entries[1])).Should().Equal(Pattern(40, 6));
    }

    [Theory]
    [InlineData(104u)]
    [InlineData(105u)]
    public async Task Tes5EmbeddedNamesShouldBeSkipped(uint version)
    {
        var data = Pattern(300, 7);
        var flags = FolderAndFileNames | EmbeddedNames;
        await using var reader = await Open(Tes5(version, flags,
            new Tes5Entry("meshes", "a.nif", data, false),
            new Tes5Entry("meshes", "b.nif", data, false)));

        reader.Entries.Select(e => e.Size).Should().Equal(300UL, 300UL);
        (await Extract(reader.Entries[1])).Should().Equal(data);
    }

    [Fact]
    public async Task Tes5ShouldKeepEmbeddedNamesAndCompressionTogether()
    {
        var data = Pattern(4000, 8);
        await using var reader = await Open(Tes5(105, FolderAndFileNames | EmbeddedNames | DefaultCompressed,
            new Tes5Entry("meshes", "a.nif", data, true)));

        reader.Entries[0].Size.Should().Be(4000UL);
        (await Extract(reader.Entries[0])).Should().Equal(data);
    }

    [Fact]
    public async Task FlipCompressionShouldInvertTheArchiveDefault()
    {
        var a = Pattern(3000, 9);
        var b = Pattern(3000, 10);
        var c = Pattern(3000, 11);
        var d = Pattern(3000, 12);

        // The archive compresses by default: entries written uncompressed carry the flip flag.
        await using var compressedDefault = await Open(Tes5(105, FolderAndFileNames | DefaultCompressed,
            new Tes5Entry("f", "plain.bin", a, false),
            new Tes5Entry("f", "packed.bin", b, true)));
        compressedDefault.Entries.Select(e => e.State).Should().Equal(
            new Tes5FileState(0, RelativePath.FromUnsanitizedInput("f/plain.bin"), true),
            new Tes5FileState(1, RelativePath.FromUnsanitizedInput("f/packed.bin"), false));
        (await Extract(compressedDefault.Entries[0])).Should().Equal(a);
        (await Extract(compressedDefault.Entries[1])).Should().Equal(b);

        // And the other way round: stored by default, flagged entries are compressed.
        await using var storedDefault = await Open(Tes5(104, FolderAndFileNames,
            new Tes5Entry("f", "plain.bin", c, false),
            new Tes5Entry("f", "packed.bin", d, true)));
        storedDefault.Entries.Select(e => ((Tes5FileState)e.State).FlipCompression).Should().Equal(false, true);
        storedDefault.Entries.Select(e => e.Size).Should().Equal(3000UL, 3000UL);
        (await Extract(storedDefault.Entries[0])).Should().Equal(c);
        (await Extract(storedDefault.Entries[1])).Should().Equal(d);
    }

    [Fact]
    public async Task Tes5ShouldNumberFilesWithinTheirFolder()
    {
        await using var reader = await Open(Tes5(104, FolderAndFileNames,
            new Tes5Entry("a", "1.bin", Pattern(4, 1), false),
            new Tes5Entry("a", "2.bin", Pattern(4, 2), false),
            new Tes5Entry("b", "3.bin", Pattern(4, 3), false)));

        reader.Entries.Select(e => e.State.Index).Should().Equal(0, 1, 0);
    }

    // Wabbajack records member paths with its own per-version decoding, and those strings have to match
    // what this reader produces: ASCII for Morrowind (other bytes become '?'), UTF-7 for version 103,
    // UTF-8 for 104 and Windows-1252 for 105.
    [Fact]
    public async Task Tes3NamesShouldDecodeAsAscii()
    {
        await using var reader = await Open(Tes3(new Tes3Entry("caf\u00e9.nif", Pattern(3, 1))));
        reader.Entries[0].Path.Should().Be(RelativePath.FromUnsanitizedInput("caf?.nif"));
    }

    [Fact]
    public async Task Tes5Version103NamesShouldDecodeAsUtf7()
    {
        await using var reader = await Open(Tes5(103, FolderAndFileNames,
            new Tes5Entry("meshes", "a+b.nif", Pattern(3, 1), false)));
        // The '+' opens a base64 shift that swallows the 'b'; Wabbajack records the same mangled name.
        reader.Entries[0].Path.Should().Be(RelativePath.FromUnsanitizedInput("meshes/a.nif"));
    }

    [Fact]
    public async Task Tes5Version104NamesShouldDecodeAsUtf8()
    {
        await using var reader = await Open(Tes5(104, FolderAndFileNames,
            new Tes5Entry("meshes", "caf\u00c3\u00a9.nif", Pattern(3, 1), false)));
        reader.Entries[0].Path.Should().Be(RelativePath.FromUnsanitizedInput("meshes/caf\u00e9.nif"));
    }

    [Fact]
    public async Task Tes5Version105NamesShouldDecodeAsWindows1252()
    {
        await using var reader = await Open(Tes5(105, FolderAndFileNames,
            new Tes5Entry("meshes", "caf\u00e9\u0080.nif", Pattern(3, 1), false)));
        reader.Entries[0].Path.Should().Be(RelativePath.FromUnsanitizedInput("meshes/caf\u00e9\u20ac.nif"));
    }

    [Fact]
    public async Task OpenShouldRejectABa2WithAnUnknownVersion()
    {
        var act = () => Open(Encoding.ASCII.GetBytes("BTDX").Concat(new byte[40]).ToArray());
        (await act.Should().ThrowAsync<BethesdaArchiveException>()).WithMessage("*version 0*");
    }

    [Fact]
    public async Task OpenShouldRejectUnknownMagic()
    {
        var act = () => Open(Encoding.ASCII.GetBytes("RIFF").Concat(new byte[40]).ToArray());
        await act.Should().ThrowAsync<BethesdaArchiveException>();
    }

    [Fact]
    public async Task OpenShouldRejectAnEmptyStream()
    {
        var act = () => Open([]);
        await act.Should().ThrowAsync<BethesdaArchiveException>();
    }

    [Fact]
    public async Task OpenShouldRejectUnknownTes5Versions()
    {
        var bytes = Tes5(104, FolderAndFileNames, new Tes5Entry("f", "a.bin", Pattern(4, 1), false));
        bytes[4] = 77;
        var act = () => Open(bytes);
        (await act.Should().ThrowAsync<BethesdaArchiveException>()).WithMessage("*Tes5*version*77*");
    }

    [Fact]
    public async Task TruncatedTes3HeaderShouldNameKindAndOffset()
    {
        var bytes = Tes3(new Tes3Entry("a.txt", Pattern(5, 1)));
        var act = () => Open(bytes[..10]);
        (await act.Should().ThrowAsync<BethesdaArchiveException>()).WithMessage("Tes3 archive is truncated at offset 8*");
    }

    [Fact]
    public async Task TruncatedTes3FileTableShouldNameKindAndOffset()
    {
        var bytes = Tes3(new Tes3Entry("a.txt", Pattern(5, 1)), new Tes3Entry("b.txt", Pattern(5, 2)));
        var act = () => Open(bytes[..24]);
        (await act.Should().ThrowAsync<BethesdaArchiveException>()).WithMessage("Tes3 archive*offset*");
    }

    [Fact]
    public async Task Tes3HashOffsetPastTheEndShouldNameKindAndOffset()
    {
        var bytes = Tes3(new Tes3Entry("a.txt", Pattern(5, 1)));
        BitConverter.GetBytes(0xFFFFFFF0u).CopyTo(bytes, 4);
        var act = () => Open(bytes);
        (await act.Should().ThrowAsync<BethesdaArchiveException>()).WithMessage("Tes3 archive is truncated at offset 24*");
    }

    [Fact]
    public async Task TruncatedTes5HeaderShouldNameKindAndOffset()
    {
        var bytes = Tes5(104, FolderAndFileNames, new Tes5Entry("f", "a.bin", Pattern(4, 1), false));
        var act = () => Open(bytes[..20]);
        (await act.Should().ThrowAsync<BethesdaArchiveException>()).WithMessage("Tes5 archive is truncated at offset 20*");
    }

    [Fact]
    public async Task TruncatedTes5FileRecordsShouldNameKindAndOffset()
    {
        var bytes = Tes5(104, FolderAndFileNames, new Tes5Entry("f", "a.bin", Pattern(4, 1), false));
        // Header and the folder record are intact; the file record block is cut after the folder name.
        var act = () => Open(bytes[..(36 + 16 + 4)]);
        (await act.Should().ThrowAsync<BethesdaArchiveException>()).WithMessage("Tes5 archive*offset*");
    }

    [Fact]
    public async Task Tes5HugeFileCountShouldFailBeforeAllocating()
    {
        var bytes = Tes5(104, FolderAndFileNames, new Tes5Entry("f", "a.bin", Pattern(4, 1), false));
        BitConverter.GetBytes(0xFFFFFFF0u).CopyTo(bytes, 16); // folder count
        var act = () => Open(bytes);
        await act.Should().ThrowAsync<BethesdaArchiveException>();
    }

    [Fact]
    public async Task TruncatedEntryDataShouldThrowBethesdaArchiveException()
    {
        var bytes = Tes3(new Tes3Entry("a.txt", Pattern(50, 1)));
        await using var reader = await Open(bytes[..^20]);

        var act = () => Extract(reader.Entries[0]);
        (await act.Should().ThrowAsync<BethesdaArchiveException>()).WithMessage("Tes3 archive*offset*");
    }

    [Fact]
    public async Task TruncatedCompressedEntryShouldThrowBethesdaArchiveException()
    {
        var bytes = Tes5(104, FolderAndFileNames | DefaultCompressed,
            new Tes5Entry("f", "a.bin", Pattern(5000, 1), true));
        await using var reader = await Open(bytes[..^30]);

        var act = () => Extract(reader.Entries[0]);
        await act.Should().ThrowAsync<BethesdaArchiveException>();
    }

    [Fact]
    public async Task ConcurrentExtractionShouldNotInterleaveReads()
    {
        var entries = Enumerable.Range(0, 8).Select(i => new Tes5Entry("f", $"{i}.bin", Pattern(2000 + i, i), i % 2 == 0)).ToArray();
        await using var reader = await Open(Tes5(105, FolderAndFileNames | DefaultCompressed, entries));

        var results = await Task.WhenAll(reader.Entries.Select(Extract));
        for (var i = 0; i < entries.Length; i++) results[i].Should().Equal(entries[i].Data);
    }
}
