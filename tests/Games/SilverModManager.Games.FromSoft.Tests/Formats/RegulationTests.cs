using System.Buffers.Binary;
using System.Security.Cryptography;
using FluentAssertions;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Formats;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Games.FromSoft.Tests.Formats.Fixtures;

namespace NexusMods.Games.FromSoft.Tests.Formats;

public class RegulationTests
{
    private static FromSoftFormatTraits EldenRing => EldenRingGame.Definition.Formats;

    private static FromSoftFormatTraits TestSouls => TestSoulsGame.Definition.Formats;

    private static byte[] Payload() => RegulationFixture.Payload(RegulationFixture.GoldenEntries, RegulationFixture.GoldenVersion);

    private static byte[] Decrypt(byte[] blob) => AesCbc.DecryptCbc([..EldenRing.RegulationKey], blob[..16], blob[16..]);

    [Fact]
    public void PackThenUnpackReturnsThePayload()
    {
        var iv = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var blob = Regulation.Pack(Payload(), iv, EldenRing);
        Regulation.Unpack(blob, EldenRing).Should().Equal(Payload());
    }

    [Fact]
    public void ThePackedFileStartsWithTheIvItWasGiven()
    {
        var iv = Enumerable.Range(1, 16).Select(i => (byte)(i * 3)).ToArray();
        Regulation.Pack(Payload(), iv, EldenRing)[..16].Should().Equal(iv);
    }

    [Fact]
    public void TheCiphertextIsAWholeNumberOfBlocks()
        => ((Regulation.Pack(Payload(), new byte[16], EldenRing).Length - 16) % 16).Should().Be(0);

    [Fact]
    public void ThePaddingIsZeroFillThenAFullBlockOf0x10()
    {
        var plain = Decrypt(Regulation.Pack(Payload(), new byte[16], EldenRing));
        plain[^16..].Should().Equal(Enumerable.Repeat((byte)0x10, 16));
        var dcxLength = Dcx.HeaderSize + (int)BinaryPrimitives.ReadUInt32BigEndian(plain.AsSpan(0x20));
        plain[dcxLength..^16].Should().OnlyContain(b => b == 0);
        (plain.Length - 16).Should().Be((dcxLength + 15) & ~15);
    }

    [Fact]
    public void ThePaddingBlockIsAddedEvenWhenTheDcxIsAlreadyAligned()
    {
        // Find a payload whose DCX happens to end on a block boundary; the full block must still follow.
        for (var extra = 0; extra < 256; extra++)
        {
            var payload = Payload().Concat(Enumerable.Range(0, extra).Select(i => (byte)(i * 131 + 7))).ToArray();
            var plain = Decrypt(Regulation.Pack(payload, new byte[16], EldenRing));
            var dcxLength = Dcx.HeaderSize + (int)BinaryPrimitives.ReadUInt32BigEndian(plain.AsSpan(0x20));
            if (dcxLength % 16 != 0) continue;
            plain.Length.Should().Be(dcxLength + 16);
            return;
        }
        Assert.Fail("no aligned DCX found in the sweep");
    }

    [Fact]
    public void TheBodyIsAZstdDcxWithTheTraitsWindow()
    {
        var plain = Decrypt(Regulation.Pack(Payload(), new byte[16], EldenRing));
        plain[0x28..0x2C].Should().Equal("ZSTD"u8.ToArray());
        plain[Dcx.HeaderSize + 5].Should().Be(0x30, "the frame header's window descriptor follows its flags byte");
    }

    [Fact]
    public void ThePackedBytesMatchWhatErmProduces()
    {
        var golden = GoldenFilesTests.Read("regulation_synthetic.bin");
        RegulationFixture.BuildFromEntries(RegulationFixture.GoldenEntries, RegulationFixture.GoldenVersion, EldenRing)
            .Should().Equal(golden);
    }

    [Fact]
    public void UnpackReadsErmsGolden()
        => Regulation.Unpack(GoldenFilesTests.Read("regulation_synthetic.bin"), EldenRing).Should().Equal(Payload());

    [Fact]
    public void EntriesReadsTheMembersOfTheGolden()
    {
        var entries = Regulation.Entries(GoldenFilesTests.Read("regulation_synthetic.bin"), EldenRing);
        entries.Select(e => (e.Id, e.Name)).Should().Equal([(1, "a.param"), (2, "b.param"), (3, "c.param")]);
        entries[1].Data.Should().Equal(RegulationFixture.GoldenEntries[1].Data);
    }

    [Fact]
    public void TheVersionIsTheEightBytesAtOffset18()
        => Regulation.Version(Payload()).Should().Be("11701000");

    [Fact]
    public void TheVersionOfAPayloadThatIsNotABnd4IsRefused()
        => FluentActions.Invoking(() => Regulation.Version(new byte[0x40]))
            .Should().Throw<RegulationException>().WithMessage("*BND4*");

    [Fact]
    public void TheVersionOfAShortPayloadIsRefused()
        => FluentActions.Invoking(() => Regulation.Version("BND4\0\0\0\0"u8.ToArray()))
            .Should().Throw<RegulationException>().WithMessage("*too short*");

    [Fact]
    public void AVersionThatIsNotAsciiIsRefused()
    {
        var payload = Payload();
        payload[0x18] = 0xE9;
        FluentActions.Invoking(() => Regulation.Version(payload))
            .Should().Throw<RegulationException>().WithMessage("*not ASCII*");
    }

    [Fact]
    public void AGameWithoutARegulationRefusesEveryOperationNamingTheGame()
    {
        var blob = new byte[48];
        FluentActions.Invoking(() => Regulation.Unpack(blob, TestSouls)).Should().Throw<RegulationException>().WithMessage("*no regulation*");
        FluentActions.Invoking(() => Regulation.Entries(blob, TestSouls)).Should().Throw<RegulationException>().WithMessage("*no regulation*");
        FluentActions.Invoking(() => Regulation.Pack(Payload(), new byte[16], TestSouls)).Should().Throw<RegulationException>().WithMessage("*no regulation*");
        FluentActions.Invoking(() => Regulation.Unpack(blob, TestSouls)).Should().Throw<RegulationException>().WithMessage($"*{TestSouls.ParamdefSet}*");
    }

    [Fact]
    public void AnIvThatIsNot16BytesIsRefused()
        => FluentActions.Invoking(() => Regulation.Pack(Payload(), new byte[8], EldenRing)).Should().Throw<AesException>();

    [Fact]
    public void ABlobShorterThanAnIvIsRefused()
        => FluentActions.Invoking(() => Regulation.Unpack(new byte[8], EldenRing)).Should().Throw<RegulationException>();

    [Fact]
    public void Sha256HexIsTheLowercaseDigestOfTheWholeFile()
    {
        var blob = GoldenFilesTests.Read("regulation_synthetic.bin");
        Regulation.Sha256Hex(blob).Should().Be(Convert.ToHexString(SHA256.HashData(blob)).ToLowerInvariant());
    }
}
