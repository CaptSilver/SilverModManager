using System.Buffers.Binary;
using NexusMods.Paths;
using static NexusMods.Games.Bethesda.Archives.Tests.TestArchives;

namespace NexusMods.Games.Bethesda.Archives.Tests;

public class BethesdaArchiveBuilderTests : IDisposable
{
    private const byte Bc1 = 71;
    private const uint Align = 0xBAADF00D;

    private readonly TemporaryFileManager _temp = new(
        FileSystem.Shared,
        FileSystem.Shared.GetKnownPath(KnownPath.EntryDirectory).Combine($"Bethesda.Archives.Tests-{Guid.NewGuid()}"));

    public void Dispose() => _temp.Dispose();

    private static RelativePath P(string path) => RelativePath.FromUnsanitizedInput(path);

    private static byte[] Repeat(int length, byte value) => Enumerable.Repeat(value, length).ToArray();

    private async Task<byte[]> Build(ArchiveState state, params (FileState State, byte[] Data)[] files)
    {
        await using var builder = BethesdaArchiveBuilder.Create(state, _temp);
        foreach (var (fileState, data) in files)
            await builder.AddFile(fileState, new MemoryStream(data), CancellationToken.None);

        var output = new MemoryStream();
        await builder.Build(output, CancellationToken.None);
        return output.ToArray();
    }

    private static Task<IBethesdaArchiveReader> Open(byte[] archive) =>
        BethesdaArchive.Open(new MemoryStream(archive), CancellationToken.None);

    private static async Task<byte[]> Extract(IBethesdaArchiveEntry entry)
    {
        var ms = new MemoryStream();
        await entry.CopyToAsync(ms, CancellationToken.None);
        return ms.ToArray();
    }

    // Tes3

    [Fact]
    public async Task Tes3RebuiltFromItsStatesShouldMatchTheOriginalBytes()
    {
        var original = Tes3(
            new Tes3Entry("meshes\\a.nif", Pattern(100, 1)),
            new Tes3Entry("textures\\b.dds", Pattern(33, 2)),
            new Tes3Entry("c.txt", Pattern(7, 3)));

        await using var reader = await Open(original);
        var files = new List<(FileState, byte[])>();
        foreach (var entry in reader.Entries) files.Add((entry.State, await Extract(entry)));

        var rebuilt = await Build(reader.State, files.ToArray());

        rebuilt.Should().Equal(original);
    }

    [Fact]
    public async Task Tes3ShouldReadBackWithTheSameEntriesBytesAndStates()
    {
        var original = Tes3(
            new Tes3Entry("meshes\\a.nif", Pattern(100, 1)),
            new Tes3Entry("b.txt", Pattern(33, 2)));
        await using var source = await Open(original);
        var data = new[] { Pattern(100, 1), Pattern(33, 2) };

        var rebuilt = await Build(source.State, source.Entries.Select((e, i) => (e.State, data[i])).Reverse().ToArray());

        await using var reader = await Open(rebuilt);
        reader.State.Should().Be(source.State);
        reader.Entries.Select(e => e.Path).Should().Equal(P("meshes/a.nif"), P("b.txt"));
        reader.Entries.Select(e => e.State).Should().Equal(source.Entries.Select(e => e.State));
        (await Extract(reader.Entries[1])).Should().Equal(data[1]);
    }

    [Fact]
    public async Task Tes3DataOfTheWrongSizeShouldThrow()
    {
        await using var source = await Open(Tes3(new Tes3Entry("a.txt", Pattern(10, 1))));
        var act = () => Build(source.State, (source.Entries[0].State, Pattern(11, 1)));
        await act.Should().ThrowAsync<BethesdaArchiveException>();
    }

    [Fact]
    public async Task Tes3MissingAFileShouldThrowOnBuild()
    {
        await using var source = await Open(Tes3(new Tes3Entry("a.txt", Pattern(10, 1)), new Tes3Entry("b.txt", Pattern(10, 2))));
        var act = () => Build(source.State, (source.Entries[0].State, Pattern(10, 1)));
        await act.Should().ThrowAsync<BethesdaArchiveException>();
    }

    // Tes5

    [Fact]
    public void Tes5HashesShouldMatchTheDocumentedAlgorithm()
    {
        // Worked from the algorithm on https://en.uesp.net/wiki/Skyrim_Mod:Archive_File_Format#Hash_Calculation
        // with an independent implementation (python), not read back from this code.
        Tes5Hashes.Folder("textures\\actors\\character").Should().Be(0x6D1EA1EE74196572UL);
        Tes5Hashes.File("foo.dds").Should().Be(0x8DDBA9C56603EFEFUL);
        Tes5Hashes.Folder("Textures\\Actors\\Character").Should().Be(0x6D1EA1EE74196572UL);
        Tes5Hashes.Folder("meshes").Should().Be(0x322F3A9A6D066573UL);
        Tes5Hashes.Folder("sound").Should().Be(0x6F1BC673056E64UL);
        Tes5Hashes.File("a.nif").Should().Be(0x92CD45FD61018061UL);
        Tes5Hashes.File("c.dds").Should().Be(0x8DDBA9C5630180E3UL);
        Tes5Hashes.Folder("").Should().Be(0UL);
    }

    [Fact]
    public async Task Tes5ShouldWriteTheFolderAndFileHashesIntoTheirRecords()
    {
        var bytes = await Build(
            new Tes5ArchiveState("BSA\0", 104, FolderAndFileNames, 0),
            (new Tes5FileState(0, P("textures\\actors\\character\\foo.dds"), false), Pattern(20, 1)));

        // Header (36), one folder record (16), then the folder name (length byte, 25 characters, zero) and the file record.
        BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(36)).Should().Be(0x6D1EA1EE74196572UL);
        bytes[52].Should().Be(26);
        BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(52 + 27)).Should().Be(0x8DDBA9C56603EFEFUL);
    }

    [Fact]
    public async Task Tes5ShouldOrderFoldersAndFilesByHash()
    {
        var state = new Tes5ArchiveState("BSA\0", 104, FolderAndFileNames, 0);
        var bytes = await Build(state,
            (new Tes5FileState(0, P("textures\\x.txt"), false), Pattern(5, 1)),
            (new Tes5FileState(0, P("meshes\\a.nif"), false), Pattern(5, 2)),
            (new Tes5FileState(0, P("meshes\\c.dds"), false), Pattern(5, 3)),
            (new Tes5FileState(0, P("sound\\y.wav"), false), Pattern(5, 4)));

        await using var reader = await Open(bytes);
        // sound 0x006F.., meshes 0x322F.., textures 0xD507..; in a folder c.dds 0x8DDB.. sorts before a.nif 0x92CD..
        reader.Entries.Select(e => e.Path).Should().Equal(
            P("sound/y.wav"), P("meshes/c.dds"), P("meshes/a.nif"), P("textures/x.txt"));
        reader.Entries.Select(e => e.State.Index).Should().Equal(0, 0, 1, 0);
    }

    public static TheoryData<uint, uint> Tes5Variants() => new()
    {
        { 103, FolderAndFileNames },
        { 104, FolderAndFileNames },
        { 105, FolderAndFileNames },
        { 103, FolderAndFileNames | DefaultCompressed },
        { 104, FolderAndFileNames | DefaultCompressed },
        { 105, FolderAndFileNames | DefaultCompressed },
        { 104, FolderAndFileNames | EmbeddedNames },
        { 105, FolderAndFileNames | DefaultCompressed | EmbeddedNames },
    };

    // Per file: whether the bytes on disk are compressed is the archive default, flipped by the entry's flag.
    [Theory]
    [MemberData(nameof(Tes5Variants))]
    public async Task Tes5ShouldReadBackWithTheSameEntriesBytesAndStates(uint version, uint flags)
    {
        var big = Repeat(6000, 0x41);
        var state = new Tes5ArchiveState("BSA\0", version, flags, 0x3);

        // One entry per default, one flipped, one in a folder of its own.
        var bytes = await Build(state,
            (new Tes5FileState(0, P("meshes\\a.nif"), false), big),
            (new Tes5FileState(1, P("meshes\\b.txt"), true), big),
            (new Tes5FileState(0, P("textures\\actors\\character\\foo.dds"), false), Pattern(300, 5)),
            (new Tes5FileState(0, P("root.txt"), false), Pattern(9, 6)));

        await using var reader = await Open(bytes);
        reader.State.Should().Be(state);
        reader.Entries.Should().HaveCount(4);

        var byPath = reader.Entries.ToDictionary(e => e.Path);
        byPath[P("meshes/a.nif")].State.Should().BeOfType<Tes5FileState>().Which.FlipCompression.Should().BeFalse();
        byPath[P("meshes/b.txt")].State.Should().BeOfType<Tes5FileState>().Which.FlipCompression.Should().BeTrue();
        (await Extract(byPath[P("meshes/a.nif")])).Should().Equal(big);
        (await Extract(byPath[P("meshes/b.txt")])).Should().Equal(big);
        (await Extract(byPath[P("textures/actors/character/foo.dds")])).Should().Equal(Pattern(300, 5));
        (await Extract(byPath[P("root.txt")])).Should().Equal(Pattern(9, 6));
        byPath[P("meshes/a.nif")].Size.Should().Be(6000);

        // Exactly one of the two 6000 byte files is compressed, so the archive holds far less than both.
        bytes.Length.Should().BeInRange(6000, 7500);
    }

    [Theory]
    [InlineData(103u)]
    [InlineData(105u)]
    public async Task Tes5ShouldCompressWithTheCodecOfItsVersion(uint version)
    {
        var data = Repeat(2000, 0x42);
        var bytes = await Build(
            new Tes5ArchiveState("BSA\0", version, FolderAndFileNames | DefaultCompressed, 0),
            (new Tes5FileState(0, P("a\\b.txt"), false), data));

        // Header, one folder record, "a" as a length prefixed zero terminated name, one file record, the
        // file name block; then the entry's data: its original size and the codec's stream.
        var dataStart = 36 + (version == 105 ? 24 : 16) + 3 + 16 + "b.txt\0".Length;
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(dataStart)).Should().Be(2000);
        var opening = version == 105 ? new byte[] { 0x04, 0x22, 0x4D, 0x18 } : [0x78];
        bytes.AsSpan(dataStart + 4, opening.Length).ToArray().Should().Equal(opening);
        bytes.Length.Should().BeLessThan(dataStart + 4 + 200);
    }

    // Ba2

    [Fact]
    public void Ba2HashesShouldBeCrc32OfTheLowerCasedNames()
    {
        // zlib.crc32 of the lower-cased text, computed with python.
        Ba2Hashes.Name("foo").Should().Be(0x8C736521u);
        Ba2Hashes.Name("FOO").Should().Be(0x8C736521u);
        Ba2Hashes.Directory("textures\\actors\\character").Should().Be(0xAEE90253u);
        Ba2Hashes.Directory("Textures/Actors/Character").Should().Be(0xAEE90253u);

        var (dir, name, extension) = Ba2Hashes.Of(P("textures\\actors\\character\\foo.dds"));
        (dir, name, extension).Should().Be((0xAEE90253u, 0x8C736521u, "dds\0"));
    }

    private static Ba2FileState General(int index, string path, bool compressed)
    {
        var (dir, name, extension) = Ba2Hashes.Of(P(path));
        return new Ba2FileState(index, P(path), extension, compressed, Align, 0x00100100, dir, name);
    }

    [Fact]
    public async Task Ba2GeneralShouldReadBackWithTheSameEntriesBytesStatesAndHashes()
    {
        var state = new Ba2ArchiveState(true, Ba2EntryType.Gnrl, "BTDX", 1, 0, 0, 0);
        var files = new[]
        {
            (General(0, "meshes\\a.nif", true), Repeat(5000, 7)),
            (General(1, "textures\\actors\\character\\foo.dds", false), Pattern(64, 2)),
            (General(2, "sound\\fx\\hit.wav", true), Pattern(900, 3)),
        };

        var bytes = await Build(state, files.Reverse().Select(f => ((FileState)f.Item1, f.Item2)).ToArray());

        await using var reader = await Open(bytes);
        reader.State.Should().Be(state);
        reader.Entries.Select(e => e.State).Should().Equal(files.Select(f => (FileState)f.Item1));
        for (var i = 0; i < files.Length; i++)
            (await Extract(reader.Entries[i])).Should().Equal(files[i].Item2);

        var foo = reader.Entries[1].State.Should().BeOfType<Ba2FileState>().Subject;
        (foo.DirHash, foo.NameHash).Should().Be((0xAEE90253u, 0x8C736521u));
        bytes.Length.Should().BeLessThan(5000, "the 5000 byte file is zlib compressed");
    }

    [Fact]
    public async Task Ba2WithoutANameTableShouldLeaveItOutAndZeroItsOffset()
    {
        var state = new Ba2ArchiveState(false, Ba2EntryType.Gnrl, "BTDX", 1, 0, 0, 0);
        var bytes = await Build(state, (General(0, "a.txt", false), Pattern(10, 1)));

        BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16)).Should().Be(0);
        bytes.Length.Should().Be(24 + 36 + 10);
        await using var reader = await Open(bytes);
        reader.State.Should().Be(state);
        reader.Entries[0].Path.Should().Be(P(Ba2Hashes.Of(P("a.txt")).Name.ToString("X")));
    }

    [Fact]
    public async Task Ba2StarfieldGeneralShouldCompressWithLz4Blocks()
    {
        var state = new Ba2ArchiveState(true, Ba2EntryType.Gnrl, "BTDX", 3, 1, 2, 3);
        var data = Repeat(4000, 9);
        var bytes = await Build(state, (General(0, "x.bin", true), data), (General(1, "y.bin", false), Pattern(30, 1)));

        await using var reader = await Open(bytes);
        reader.State.Should().Be(state);
        (await Extract(reader.Entries[0])).Should().Equal(data);
        reader.Entries[0].State.Should().BeOfType<Ba2FileState>().Which.Compressed.Should().BeTrue();
        reader.Entries[1].State.Should().BeOfType<Ba2FileState>().Which.Compressed.Should().BeFalse();
    }

    /// <summary>A 64x32 BC1 texture with two mips, as a DDS file built here from the documented header layout.</summary>
    private static (byte[] Dds, byte[] Mip0, byte[] Mip1) Texture()
    {
        var mip0 = Repeat(1024, 0x11);
        var mip1 = Pattern(256, 4);
        return (DdsHeader.Build(64, 32, 2, Bc1, isCubeMap: false).Concat(mip0).Concat(mip1).ToArray(), mip0, mip1);
    }

    private static Ba2Dx10FileState TextureState(int index, string path, bool compressed0 = true)
    {
        var (dir, name, _) = Ba2Hashes.Of(P(path));
        return new Ba2Dx10FileState(
            index, P(path),
            [new Ba2Chunk(compressed0, Align, EndMip: 0, StartMip: 0, 1024), new Ba2Chunk(false, Align, EndMip: 1, StartMip: 1, 256)],
            Bc1, 2, 64, 32, 24, 0, dir, "dds\0", name, 0, 8);
    }

    [Theory]
    [InlineData(1u, 0u)]
    [InlineData(3u, 3u)]
    public async Task Ba2Dx10ShouldStripTheDdsHeaderAndRestoreItOnRead(uint version, uint compression)
    {
        var (dds, mip0, mip1) = Texture();
        var state = new Ba2ArchiveState(true, Ba2EntryType.Dx10, "BTDX", version, version == 3 ? 1u : 0u, version == 3 ? 2u : 0u, compression);
        var fileState = TextureState(0, "textures\\t.dds");

        var bytes = await Build(state, (fileState, dds));

        bytes.AsSpan().IndexOf("DDS "u8).Should().Be(-1, "the archive keeps no header");
        await using var reader = await Open(bytes);
        reader.State.Should().Be(state);
        reader.Entries.Should().ContainSingle().Which.State.Should().Be(fileState);
        (await Extract(reader.Entries[0])).Should().Equal(dds);
        bytes.Length.Should().BeLessThan(dds.Length, "chunk 0 is compressed and every byte of it repeats");
    }

    [Fact]
    public async Task Ba2Dx10ShouldWriteChunksWithTheirMipRangesAndSizes()
    {
        var (dds, _, _) = Texture();
        var state = new Ba2ArchiveState(true, Ba2EntryType.Dx10, "BTDX", 1, 0, 0, 0);
        var bytes = await Build(state, (TextureState(0, "t.dds", compressed0: false), dds));

        // Header (24), record (24), then two chunk records of 24: offset, packed size, full size, start mip, end mip, align.
        var chunk1 = bytes.AsSpan(24 + 24 + 24);
        BinaryPrimitives.ReadUInt32LittleEndian(chunk1[8..]).Should().Be(0, "stored chunks have no packed size");
        BinaryPrimitives.ReadUInt32LittleEndian(chunk1[12..]).Should().Be(256);
        BinaryPrimitives.ReadUInt16LittleEndian(chunk1[16..]).Should().Be(1);
        BinaryPrimitives.ReadUInt16LittleEndian(chunk1[18..]).Should().Be(1);
        BinaryPrimitives.ReadUInt32LittleEndian(chunk1[20..]).Should().Be(Align);
    }

    [Fact]
    public async Task Ba2Dx10WithAShortDdsShouldThrow()
    {
        var (dds, _, _) = Texture();
        var state = new Ba2ArchiveState(true, Ba2EntryType.Dx10, "BTDX", 1, 0, 0, 0);
        var act = () => Build(state, (TextureState(0, "t.dds"), dds[..^10]));
        await act.Should().ThrowAsync<BethesdaArchiveException>();
    }

    [Fact]
    public async Task Ba2Dx10WithoutADdsHeaderShouldThrow()
    {
        var (dds, _, _) = Texture();
        dds[0] = (byte)'X';
        var state = new Ba2ArchiveState(true, Ba2EntryType.Dx10, "BTDX", 1, 0, 0, 0);
        var act = () => Build(state, (TextureState(0, "t.dds"), dds));
        await act.Should().ThrowAsync<BethesdaArchiveException>();
    }

    [Fact]
    public async Task Ba2ExtensionOfTheWrongLengthShouldThrow()
    {
        var state = new Ba2ArchiveState(true, Ba2EntryType.Gnrl, "BTDX", 1, 0, 0, 0);
        var file = General(0, "a.txt", false) with { Extension = "txt" };
        var act = () => Build(state, (file, Pattern(4, 1)));
        await act.Should().ThrowAsync<BethesdaArchiveException>();
    }

    // Mismatched states

    public static TheoryData<string> Mismatches() => new()
    {
        "tes3-given-tes5",
        "tes5-given-ba2",
        "gnrl-given-tes5",
        "gnrl-given-dx10",
        "dx10-given-gnrl",
    };

    [Theory]
    [MemberData(nameof(Mismatches))]
    public async Task AFileStateOfAnotherKindShouldThrow(string which)
    {
        var tes5File = new Tes5FileState(0, P("a.txt"), false);
        var (archive, file) = which switch
        {
            "tes3-given-tes5" => ((ArchiveState)new Tes3ArchiveState(1, 0, 0, 0x100), (FileState)tes5File),
            "tes5-given-ba2" => (new Tes5ArchiveState("BSA\0", 104, FolderAndFileNames, 0), General(0, "a.txt", false)),
            "gnrl-given-tes5" => (new Ba2ArchiveState(true, Ba2EntryType.Gnrl, "BTDX", 1, 0, 0, 0), tes5File),
            "gnrl-given-dx10" => (new Ba2ArchiveState(true, Ba2EntryType.Gnrl, "BTDX", 1, 0, 0, 0), TextureState(0, "t.dds")),
            "dx10-given-gnrl" => (new Ba2ArchiveState(true, Ba2EntryType.Dx10, "BTDX", 1, 0, 0, 0), General(0, "a.txt", false)),
            _ => throw new ArgumentOutOfRangeException(nameof(which)),
        };

        var act = () => Build(archive, (file, Pattern(4, 1)));
        await act.Should().ThrowAsync<BethesdaArchiveException>();
    }

    [Fact]
    public void AnArchiveStateOfAnUnreadableKindShouldThrow()
    {
        var gnmf = () => BethesdaArchiveBuilder.Create(new Ba2ArchiveState(true, Ba2EntryType.Gnmf, "BTDX", 1, 0, 0, 0), _temp);
        gnmf.Should().Throw<BethesdaArchiveException>();

        var version = () => BethesdaArchiveBuilder.Create(new Tes5ArchiveState("BSA\0", 99, FolderAndFileNames, 0), _temp);
        version.Should().Throw<BethesdaArchiveException>();
    }

    // The helper other test projects use for fixtures.

    [Fact]
    public async Task TestArchivesShouldWriteAReadableTwoMemberTes5Archive()
    {
        var bytes = await Archives.TestArchives.Tes5Archive(_temp);

        await using var reader = await Open(bytes);
        reader.Kind.Should().Be(ArchiveKind.Tes5);
        reader.Entries.Select(e => e.Path).Should().BeEquivalentTo([Archives.TestArchives.FirstMemberPath, Archives.TestArchives.SecondMemberPath]);
        (await Extract(reader.Entries.Single(e => e.Path == Archives.TestArchives.FirstMemberPath)))
            .Should().Equal(Archives.TestArchives.FirstMemberData);
    }

    [Fact]
    public void TestArchivesShouldWriteATinyBc1Dds()
    {
        var dds = Archives.TestArchives.Bc1Dds();
        dds[..4].Should().Equal("DDS "u8.ToArray());
        BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(12)).Should().Be(4);
        BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(16)).Should().Be(4);
        System.Text.Encoding.ASCII.GetString(dds, 84, 4).Should().Be("DXT1");
        dds.Length.Should().Be(128 + 8);
    }
}
