using System.Buffers.Binary;
using System.Security.Cryptography;
using FluentAssertions;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;

namespace SilverModManager.Games.FromSoft.Tests.Formats;

public class DcxTests
{
    private static FromSoftFormatTraits EldenRing => EldenRingGame.Definition.Formats;

    private static FromSoftFormatTraits TestSouls => TestSoulsGame.Definition.Formats;

    private static DcxWriteFormat Dflt => EldenRing.PackedWrite;

    private static DcxWriteFormat Zstd => EldenRing.RegulationWrite!;

    private static uint U32(byte[] blob, int offset) => BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(offset));

    private static void SetU32(byte[] blob, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(offset), value);

    private static byte[] ModelPayload() => [.."BND4"u8, ..Enumerable.Range(0, 256 * 40).Select(i => (byte)i)];

    /// <summary>A payload long enough that the encoder has to record a real window instead of eliding it.</summary>
    private static byte[] WindowPayload() => Enumerable.Range(0, 4000).SelectMany(i => Enumerable.Repeat((byte)(i % 251), 97)).ToArray();

    // Deterministic and bigger than the 64 KiB window, so the window size shows up in the frame.
    private static readonly byte[] PinnedPayload = Enumerable.Range(0, 300000).Select(i => (byte)(i * 7919 + (i >> 3))).ToArray();

    // Taken with zstd 1.5.7. The libzstd that produced it and the one ZstdSharp.Port 0.8.6 ports (its assembly carries
    // the version string "1.5.7") agree. A regulation is built on each machine and co-op compares the file, so a
    // different frame here means two players building different files from the same inputs.
    private const string PinnedFrameSha256 = "2d587b5079b7baaf1353de00d78a38c0b86f4799a0104db1be1bce234b54f5c4";

    /// <summary>A container with the given method and body, laid out the way <see cref="Dcx.Write"/> lays out a zlib one.</summary>
    private static byte[] Container(string method, int uncompressedSize, byte[] body)
    {
        var header = Dcx.Write([], Dflt)[..Dcx.HeaderSize];
        var blob = new byte[Dcx.HeaderSize + body.Length];
        header.CopyTo(blob, 0);
        System.Text.Encoding.ASCII.GetBytes(method).CopyTo(blob, 0x28);
        SetU32(blob, 0x1C, (uint)uncompressedSize);
        SetU32(blob, 0x20, (uint)body.Length);
        body.CopyTo(blob, Dcx.HeaderSize);
        return blob;
    }

    [Fact]
    public void ADfltContainerRoundTrips()
    {
        var payload = ModelPayload();
        var blob = Dcx.Write(payload, Dflt);
        blob[..4].Should().Equal("DCX\0"u8.ToArray());
        blob[0x28..0x2C].Should().Equal("DFLT"u8.ToArray());
        Dcx.Read(blob, EldenRing).Should().Equal(payload);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(-1)]
    public void ADfltLevelOutsideOneToNineIsRefused(int level)
    {
        var act = () => Dcx.Write("payload"u8, Dflt with { Level = level });
        act.Should().Throw<DcxException>().WithMessage("*1-9*", "the game rejects the container before decompressing it");
    }

    [Fact]
    public void TheHeaderFieldsMatchAKnownGoodLayout()
    {
        var payload = new byte[1000];
        Array.Fill(payload, (byte)'x');
        var blob = Dcx.Write(payload, Dflt);
        blob[0x00..0x04].Should().Equal("DCX\0"u8.ToArray());
        blob[0x18..0x1C].Should().Equal("DCS\0"u8.ToArray());
        blob[0x24..0x28].Should().Equal("DCP\0"u8.ToArray());
        blob[0x40..0x44].Should().Equal(0x00, 0x01, 0x01, 0x00);
        blob[0x44..0x48].Should().Equal("DCA\0"u8.ToArray());
        U32(blob, 0x04).Should().Be(0x11000);
        U32(blob, 0x08).Should().Be(0x18);
        U32(blob, 0x0C).Should().Be(0x24);
        U32(blob, 0x10).Should().Be(0x44);
        U32(blob, 0x14).Should().Be(0x4C);
        U32(blob, 0x2C).Should().Be(0x20);
        U32(blob, 0x48).Should().Be(8);
        blob[0x30].Should().Be(9);
        U32(blob, 0x1C).Should().Be((uint)payload.Length);
        U32(blob, 0x20).Should().Be((uint)(blob.Length - Dcx.HeaderSize));
    }

    [Fact]
    public void TheHeaderVariantComesFromTheGamesWriteFormat()
    {
        var payload = new byte[1000];
        Array.Fill(payload, (byte)'x');
        var elden = Dcx.Write(payload, EldenRing.PackedWrite);
        var test = Dcx.Write(payload, TestSouls.PackedWrite);

        U32(elden, 0x04).Should().Be(0x11000);
        U32(test, 0x04).Should().Be(0x10000);
        elden[0x30].Should().Be(9);
        test[0x30].Should().Be(6);

        // The compressed size follows the level, so it is masked; every other byte is shared.
        var differing = Enumerable.Range(0, Dcx.HeaderSize).Where(i => elden[i] != test[i] && i is < 0x20 or > 0x23).ToArray();
        differing.Should().Equal(0x06, 0x30);
    }

    [Fact]
    public void ANonDcxIsRefused()
    {
        var act = () => Dcx.Read([..System.Text.Encoding.ASCII.GetBytes("NOTDCX"), ..new byte[200]], EldenRing);
        act.Should().Throw<DcxException>().WithMessage("*not a DCX*");
    }

    [Fact]
    public void ATruncatedHeaderIsRefused()
    {
        var act = () => Dcx.Read([..("DCX\0"u8.ToArray()), ..new byte[10]], EldenRing);
        act.Should().Throw<DcxException>().WithMessage("*header is truncated*");
    }

    [Fact]
    public void AnUnknownCompressionIsRefused()
    {
        var blob = Dcx.Write("payload"u8, Dflt);
        "ZZZZ"u8.CopyTo(blob.AsSpan(0x28));
        var act = () => Dcx.Read(blob, EldenRing);
        act.Should().Throw<DcxException>().WithMessage("*unsupported*");
    }

    [Fact]
    public void ATruncatedBodyIsRefused()
    {
        var blob = Dcx.Write(ModelPayload(), Dflt);
        var act = () => Dcx.Read(blob[..^10], EldenRing);
        act.Should().Throw<DcxException>().WithMessage("*truncated*");
    }

    [Fact]
    public void ADfltSizeMismatchIsRefused()
    {
        var payload = ModelPayload();
        var blob = Dcx.Write(payload, Dflt);
        SetU32(blob, 0x1C, (uint)payload.Length + 1);
        var act = () => Dcx.Read(blob, EldenRing);
        act.Should().Throw<DcxException>().WithMessage("*DFLT payload*");
    }

    [Fact]
    public void ADfltBodyBiggerThanTheHeaderClaimsIsRefused()
    {
        var payload = ModelPayload();
        var blob = Dcx.Write(payload, Dflt);
        SetU32(blob, 0x1C, (uint)payload.Length - 1);
        var act = () => Dcx.Read(blob, EldenRing);
        act.Should().Throw<DcxException>().WithMessage("*DFLT payload*");
    }

    [Fact]
    public void ACorruptDfltBodyIsRefusedAsAMalformedContainer()
    {
        var blob = Dcx.Write(ModelPayload(), Dflt);
        Array.Clear(blob, Dcx.HeaderSize, (int)U32(blob, 0x20));
        var act = () => Dcx.Read(blob, EldenRing);
        act.Should().Throw<DcxException>().WithMessage("*DFLT body could not be decompressed*");
    }

    [Fact]
    public void ACompressedSizeThatOverrunsTheBufferIsRefused()
    {
        var blob = Dcx.Write("x"u8, Dflt);
        SetU32(blob, 0x20, 10_000_000);
        var act = () => Dcx.Read(blob[..Dcx.HeaderSize], EldenRing);
        act.Should().Throw<DcxException>().WithMessage("*truncated*");
    }

    [Fact]
    public void AZstdContainerRoundTrips()
    {
        var payload = ModelPayload();
        var blob = Dcx.Write(payload, Zstd);
        blob[0x28..0x2C].Should().Equal("ZSTD"u8.ToArray());
        Dcx.Read(blob, EldenRing).Should().Equal(payload);
    }

    [Theory]
    [InlineData("ZSTD")]
    [InlineData("DFLT")]
    [InlineData("KRAK")]
    public void AnImpossibleUncompressedSizeIsRefusedBeforeAnyDecoderRuns(string method)
    {
        // A bare 32-bit size straight out of the file: without the cap up front a decoder would size its buffer from it.
        var blob = Container(method, 0, "garbage"u8.ToArray());
        SetU32(blob, 0x1C, uint.MaxValue);
        var act = () => Dcx.Read(blob, EldenRing);
        act.Should().Throw<DcxException>().WithMessage("*refusing to allocate*");
    }

    [Fact]
    public void TheCapIsTheOneTheKrakenDecoderUses()
    {
        var blob = Dcx.Write("payload"u8, Zstd);
        SetU32(blob, 0x1C, (uint)Kraken.MaxUncompressedSize + 1);
        var act = () => Dcx.Read(blob, EldenRing);
        act.Should().Throw<DcxException>().WithMessage($"*{Kraken.MaxUncompressedSize}*");
    }

    [Fact]
    public void AZstdFrameOmitsTheContentSizeFlag()
    {
        // Real regulation frames carry a frame header descriptor of 0x00; the one-shot compressor sets bit 7.
        var frame = Dcx.Write(new byte[4096], Zstd)[Dcx.HeaderSize..];
        frame[..4].Should().Equal(0x28, 0xB5, 0x2F, 0xFD);
        (frame[4] & 0x80).Should().Be(0, $"descriptor {frame[4]:X2} must not announce a content size");
    }

    [Fact]
    public void ZstdAcceptsLevelsTheDfltPathRejects()
    {
        var payload = "payload"u8.ToArray();
        var blob = Dcx.Write(payload, Zstd with { Level = 15 });
        blob[0x30].Should().Be(15);
        Dcx.Read(blob, EldenRing).Should().Equal(payload);
    }

    [Fact]
    public void AZstdHeaderRecordsBothSizes()
    {
        var payload = Enumerable.Range(0, 25600).Select(i => (byte)i).ToArray();
        var blob = Dcx.Write(payload, Zstd);
        U32(blob, 0x1C).Should().Be((uint)payload.Length);
        U32(blob, 0x20).Should().Be((uint)(blob.Length - Dcx.HeaderSize));
    }

    [Fact]
    public void ATruncatedZstdBodyIsRefused()
    {
        var blob = Dcx.Write(Enumerable.Range(0, 25600).Select(i => (byte)i).ToArray(), Zstd);
        var act = () => Dcx.Read(blob[..^32], EldenRing);
        act.Should().Throw<DcxException>().WithMessage("*truncated*");
    }

    [Fact]
    public void AZstdSizeMismatchIsRefused()
    {
        var payload = Enumerable.Range(0, 25600).Select(i => (byte)i).ToArray();
        var blob = Dcx.Write(payload, Zstd);
        SetU32(blob, 0x1C, (uint)payload.Length + 1);
        var act = () => Dcx.Read(blob, EldenRing);
        act.Should().Throw<DcxException>().WithMessage("*ZSTD payload*");
    }

    [Fact]
    public void ACorruptZstdBodyIsRefusedAsAMalformedContainer()
    {
        var blob = Dcx.Write(ModelPayload(), Zstd);
        Array.Fill(blob, (byte)0xFF, Dcx.HeaderSize, 32);
        var act = () => Dcx.Read(blob, EldenRing);
        act.Should().Throw<DcxException>().WithMessage("*ZSTD body could not be decompressed*");
    }

    [Fact]
    public void AZstdFramesWindowStaysWithinWhatTheGameLoads()
    {
        // A regulation whose frame demands a large window crashes the game on load, with byte-identical content to one that works.
        var frame = Dcx.Write(WindowPayload(), Zstd)[Dcx.HeaderSize..];
        frame[..4].Should().Equal(0x28, 0xB5, 0x2F, 0xFD);
        (frame[4] & 0x20).Should().Be(0, "single-segment frames encode the window differently");
        var windowLog = 10 + (frame[5] >> 3);
        windowLog.Should().BeLessThanOrEqualTo(Zstd.WindowLog);
    }

    [Fact]
    public void AZstdFramesWindowDescriptorMatchesTheBytesShippedRegulationsCarry()
    {
        Dcx.Write(WindowPayload(), Zstd)[Dcx.HeaderSize + 5].Should().Be(0x30);
    }

    [Fact]
    public void TheWindowLogComesFromTheWriteFormat()
    {
        var frame = Dcx.Write(WindowPayload(), Zstd with { WindowLog = 18 })[Dcx.HeaderSize..];
        (10 + (frame[5] >> 3)).Should().Be(18);
    }

    [Fact]
    public void TheCompressedFrameMatchesThePinnedDigest()
    {
        var frame = Dcx.Write(PinnedPayload, Zstd)[Dcx.HeaderSize..];
        Convert.ToHexString(SHA256.HashData(frame)).ToLowerInvariant().Should().Be(PinnedFrameSha256);
    }

    [Fact]
    public void AKrakContainerReadsBackThroughAGameThatShipsKraken()
    {
        var payload = KrakenFixture.Payload(300 * 1024);
        var blob = Container("KRAK", payload.Length, KrakenFixture.Stored(payload));
        Dcx.Read(blob, EldenRing).Should().Equal(payload);
    }

    [Theory]
    [InlineData("KRAK")]
    [InlineData("ZSTD")]
    public void AGameThatDoesNotShipAMethodRefusesItAndNamesTheOnesItReads(string method)
    {
        var blob = Container(method, 7, "payload"u8.ToArray());
        var act = () => Dcx.Read(blob, TestSouls);
        act.Should().Throw<DcxException>().WithMessage($"*{method}*Dflt*");
    }

    [Fact]
    public void AGameThatShipsDfltReadsIt()
    {
        var payload = ModelPayload();
        Dcx.Read(Dcx.Write(payload, TestSouls.PackedWrite), TestSouls).Should().Equal(payload);
    }

    [Theory]
    [InlineData(DcxCompression.Kraken)]
    [InlineData(DcxCompression.None)]
    public void NothingWritesKrakOrAnUnwrappedFile(DcxCompression compression)
    {
        var act = () => Dcx.Write("payload"u8, Dflt with { Compression = compression });
        act.Should().Throw<DcxException>();
    }

    [Fact]
    public void AZstdLevelOutsideWhatLibzstdTakesIsRefused()
    {
        var act = () => Dcx.Write("payload"u8, Zstd with { Level = 0 });
        act.Should().Throw<DcxException>();
    }

    [Fact]
    public void TheDfltGoldenHasTheHeaderTheWriterBuildsAndDecodesToItsPayload()
    {
        var golden = GoldenFilesTests.Read("dflt_payload.dcx");
        var written = Dcx.Write("payload"u8, Dflt);

        // The compressed body is zlib's own packing, which .NET need not reproduce, so the compressed size is left out.
        golden[..0x20].Should().Equal(written[..0x20]);
        golden[0x24..Dcx.HeaderSize].Should().Equal(written[0x24..Dcx.HeaderSize]);
        Dcx.Read(golden, EldenRing).Should().Equal("payload"u8.ToArray());
        Dcx.Read(written, EldenRing).Should().Equal("payload"u8.ToArray());
    }

    [Fact]
    public void TheZstdGoldenIsReproducedByteForByte()
    {
        var golden = GoldenFilesTests.Read("zstd_pinned.dcx");
        Dcx.Write(PinnedPayload, Zstd).Should().Equal(golden);
        Dcx.Read(golden, EldenRing).Should().Equal(PinnedPayload);
    }
}
