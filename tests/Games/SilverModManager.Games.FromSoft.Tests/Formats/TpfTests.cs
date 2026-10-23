using System.Buffers.Binary;
using FluentAssertions;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;

namespace SilverModManager.Games.FromSoft.Tests.Formats;

public class TpfTests
{
    private const int HeaderSize = TpfFixture.HeaderSize;

    private static byte[] Raw(params (string Name, byte[] Data, byte Format)[] textures) => TpfFixture.Build(textures);

    private static byte[] Bytes(int length, byte fill) => Enumerable.Repeat(fill, length).ToArray();

    [Fact]
    public void ReadReturnsEveryTextureWithItsNameAndBytes()
    {
        var archive = Tpf.Read(Raw(("SB_Icon_02_A", "AAAA"u8.ToArray(), 102), ("SB_Status_GRO_00", "BB"u8.ToArray(), 102)));
        archive.Textures.Select(t => t.Name).Should().Equal("SB_Icon_02_A", "SB_Status_GRO_00");
        archive.Textures.Select(t => t.Data).Should().SatisfyRespectively(
            a => a.Should().Equal("AAAA"u8.ToArray()),
            b => b.Should().Equal("BB"u8.ToArray()));
        archive.Encoding.Should().Be(1);
    }

    [Fact]
    public void WriteRoundTripsByteIdentically()
    {
        // Held to the same bar as the binder rebuild before it is allowed near a multi-hundred-megabyte shipped asset.
        var raw = Raw(("A", [1, 2, 3, 4], 0), ("Bee", Bytes(32, 0xFF), 102), ("Cee_long_name", [], 1));
        Tpf.Write(Tpf.Read(raw)).Should().Equal(raw);
    }

    [Fact]
    public void ShiftJisNamesRoundTripByteIdentically()
    {
        var raw = TpfFixture.Build([("アイコン", [9, 9], 0), ("plain", [8], 0)], encoding: 0);
        var archive = Tpf.Read(raw);
        archive.Textures.Select(t => t.Name).Should().Equal("アイコン", "plain");
        Tpf.Write(archive).Should().Equal(raw);
    }

    [Fact]
    public void WriteRecomputesOffsetsAfterATextureIsAppended()
    {
        // Appending grows the table and the name block, so every data offset behind it moves.
        var archive = Tpf.Read(Raw(("First", Bytes(16, (byte)'1'), 0)));
        var grown = archive.WithTextures([.. archive.Textures, new Texture("Added", Bytes(8, (byte)'2'), 102, 0, 1, 0)]);

        var written = Tpf.Write(grown);
        var back = Tpf.Read(written);
        back.Textures.Select(t => t.Name).Should().Equal("First", "Added");
        back.Textures.Select(t => t.Data).Should().SatisfyRespectively(
            a => a.Should().Equal(Bytes(16, (byte)'1')),
            b => b.Should().Equal(Bytes(8, (byte)'2')));
        BinaryPrimitives.ReadInt32LittleEndian(written.AsSpan(4)).Should().Be(24);
        BinaryPrimitives.ReadInt32LittleEndian(written.AsSpan(8)).Should().Be(2);
    }

    [Fact]
    public void WritePadsTheNameBlockAndCountsThePadInDataSize()
    {
        // Three characters: 3*2 + 2 terminator = 8 bytes, already aligned.
        var aligned = Tpf.Write(Tpf.Read(Raw(("Abc", "XY"u8.ToArray(), 0))));
        BinaryPrimitives.ReadInt32LittleEndian(aligned.AsSpan(4)).Should().Be(2);

        // Two characters: 6 bytes, so 2 bytes of padding follow, and dataSize spans them.
        var padded = Tpf.Write(Tpf.Read(Raw(("Ab", "XY"u8.ToArray(), 0))));
        BinaryPrimitives.ReadInt32LittleEndian(padded.AsSpan(4)).Should().Be(4);
        var dataOffset = BinaryPrimitives.ReadInt32LittleEndian(padded.AsSpan(HeaderSize));
        (dataOffset % 4).Should().Be(0);
        padded.AsSpan(dataOffset, 2).ToArray().Should().Equal("XY"u8.ToArray());
    }

    [Fact]
    public void ReadRejectsAFileThatIsNotATpf()
    {
        var act = () => Tpf.Read([.. "BND4"u8.ToArray(), .. new byte[64]]);
        act.Should().Throw<TpfException>().WithMessage("*not a TPF*");
    }

    [Fact]
    public void ReadRejectsATruncatedHeader()
    {
        var act = () => Tpf.Read([.. "TPF\0"u8.ToArray(), .. new byte[4]]);
        act.Should().Throw<TpfException>();
    }

    [Fact]
    public void ReadRejectsATextureWhoseDataRunsPastTheFile()
    {
        var raw = Raw(("A", Bytes(16, (byte)'1'), 0));
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(HeaderSize + 4), 1 << 30);
        var act = () => Tpf.Read(raw);
        act.Should().Throw<TpfException>().WithMessage("*runs past*");
    }

    [Fact]
    public void ReadRejectsACountTheFileCannotHold()
    {
        var raw = Raw(("A", Bytes(16, (byte)'1'), 0));
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(8), 1 << 20);
        var act = () => Tpf.Read(raw);
        act.Should().Throw<TpfException>().WithMessage("*doesn't fit*");
    }

    [Fact]
    public void ReadRejectsAPlatformOtherThanPc()
    {
        var raw = TpfFixture.Build([("A", [1], 0)], platform: 1);
        var act = () => Tpf.Read(raw);
        act.Should().Throw<TpfException>().WithMessage("*platform*");
    }

    [Fact]
    public void AFloatStructEntryIsRefusedRatherThanMisparsed()
    {
        // A float struct makes the entry stride variable; reading the next entry at the wrong offset would corrupt
        // every texture after it.
        var raw = Raw(("A", Bytes(16, (byte)'1'), 0));
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(HeaderSize + 16), 1);
        var act = () => Tpf.Read(raw);
        act.Should().Throw<TpfException>().WithMessage("*FloatStruct*");
    }

    [Fact]
    public void ReadRejectsAnUnterminatedUtf16TextureName()
    {
        // Without the terminator the scan would run into the padding and texture bytes and return them as the name,
        // which is the key the union merge is built on.
        var raw = Raw(("A", [1, 2, 3, 4], 0));
        var nameOffset = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(HeaderSize + 12));
        raw[nameOffset + 2] = (byte)'X';
        raw[nameOffset + 3] = (byte)'Y';
        var act = () => Tpf.Read(raw);
        act.Should().Throw<TpfException>().WithMessage("*unterminated*");
    }

    [Fact]
    public void ReadRejectsATextureNameThatIsNotValidUtf16()
    {
        var raw = Raw(("AB", [1, 2, 3, 4], 0));
        var nameOffset = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(HeaderSize + 12));
        raw[nameOffset] = 0x00;
        raw[nameOffset + 1] = 0xD8;   // a high surrogate with no pair
        var act = () => Tpf.Read(raw);
        act.Should().Throw<TpfException>();
    }

    [Fact]
    public void ReadRejectsANameOffsetOutsideTheFile()
    {
        var raw = Raw(("A", [1, 2, 3, 4], 0));
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(HeaderSize + 12), raw.Length + 10);
        var act = () => Tpf.Read(raw);
        act.Should().Throw<TpfException>().WithMessage("*outside the file*");
    }

    [Fact]
    public void WriteReproducesTheGoldenTwoTextureArchive()
    {
        var archive = new TpfArchive(
            [new Texture("SB_Icon_02_A", "AAAA"u8.ToArray(), 102, 0, 1, 0), new Texture("SB_Status_GRO_00", "BB"u8.ToArray(), 102, 0, 1, 0)],
            Platform: 0, Flag2: 3, Encoding: 1);
        Tpf.Write(archive).Should().Equal(GoldenFilesTests.Read("tpf_two_textures.tpf"));
    }

    [Fact]
    public void WriteReproducesTheGoldenNamePadCase()
    {
        Tpf.Write(Tpf.Read(GoldenFilesTests.Read("tpf_name_pad.tpf"))).Should().Equal(GoldenFilesTests.Read("tpf_name_pad.tpf"));
        Raw(("Ab", "XY"u8.ToArray(), 0)).Should().Equal(GoldenFilesTests.Read("tpf_name_pad.tpf"));
    }

    [Fact]
    public void ReadsTheGoldenTwoTextureArchive()
    {
        var archive = Tpf.Read(GoldenFilesTests.Read("tpf_two_textures.tpf"));
        archive.Textures.Should().SatisfyRespectively(
            a =>
            {
                a.Name.Should().Be("SB_Icon_02_A");
                a.Data.Should().Equal("AAAA"u8.ToArray());
                (a.Format, a.Type, a.Mipmaps, a.Flags1).Should().Be((102, 0, 1, 0));
            },
            b =>
            {
                b.Name.Should().Be("SB_Status_GRO_00");
                b.Data.Should().Equal("BB"u8.ToArray());
                (b.Format, b.Type, b.Mipmaps, b.Flags1).Should().Be((102, 0, 1, 0));
            });
        (archive.Platform, archive.Flag2, archive.Encoding).Should().Be((0, 3, 1));
    }

    [Fact]
    public void WriteRoundTripsTheGoldenTwoTextureArchive()
    {
        var golden = GoldenFilesTests.Read("tpf_two_textures.tpf");
        Tpf.Write(Tpf.Read(golden)).Should().Equal(golden);
    }
}
