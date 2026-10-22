using FluentAssertions;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Formats;
using NexusMods.Games.FromSoft.Tests.Fixtures;

namespace NexusMods.Games.FromSoft.Tests;

public class FromSoftFormatTraitsTests
{
    private static FromSoftFormatTraits EldenRing => EldenRingGame.Definition.Formats;

    private static FromSoftFormatTraits TestSouls => TestSoulsGame.Definition.Formats;

    [Fact]
    public void EldenRingPacksBnd4WithAnEncryptedZstdRegulation()
    {
        EldenRing.Binder.Should().Be(BinderFormat.Bnd4);
        EldenRing.ReadableCompressions.Should().Equal(DcxCompression.Kraken, DcxCompression.Dflt, DcxCompression.Zstd);
        EldenRing.PackedWrite.Should().Be(new DcxWriteFormat(DcxCompression.Dflt, 0x11000, 9, 0));
        EldenRing.RegulationFileName.Should().Be("regulation.bin");
        EldenRing.RegulationWrite.Should().Be(new DcxWriteFormat(DcxCompression.Zstd, 0x11000, 15, 16));
        EldenRing.ParamdefSet.Should().Be("eldenring");
    }

    [Fact]
    public void EldenRingsRegulationKeyIsThe256BitKeyTheGameShipsWith()
    {
        EldenRing.RegulationKey.Should().HaveCount(32);
        Convert.ToHexString(EldenRing.RegulationKey.AsSpan())
            .Should().Be("99BFFC366A6BC8C6F5827D093602D676C42892A01C207FB024D3AF4E493FEF99");
    }

    [Fact]
    public void TheTestGameDiffersFromEldenRingInEveryField()
    {
        TestSouls.Binder.Should().NotBe(EldenRing.Binder);
        TestSouls.ReadableCompressions.Should().NotEqual(EldenRing.ReadableCompressions);
        TestSouls.PackedWrite.Should().Be(new DcxWriteFormat(DcxCompression.Dflt, 0x10000, 6, 0));
        TestSouls.PackedWrite.Compression.Should().Be(EldenRing.PackedWrite.Compression, "the wrapper is the one field the two games share");
        TestSouls.PackedWrite.Unk04.Should().NotBe(EldenRing.PackedWrite.Unk04);
        TestSouls.PackedWrite.Level.Should().NotBe(EldenRing.PackedWrite.Level);
        TestSouls.RegulationFileName.Should().BeNull().And.NotBe(EldenRing.RegulationFileName);
        TestSouls.RegulationWrite.Should().BeNull().And.NotBe(EldenRing.RegulationWrite);
        TestSouls.RegulationKey.Should().BeEmpty();
        TestSouls.ParamdefSet.Should().Be("testsouls").And.NotBe(EldenRing.ParamdefSet);
    }
}
