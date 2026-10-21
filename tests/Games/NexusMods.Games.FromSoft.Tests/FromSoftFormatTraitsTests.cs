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
        EldenRing.PackedWriteCompression.Should().Be(DcxCompression.Dflt);
        EldenRing.RegulationFileName.Should().Be("regulation.bin");
        EldenRing.RegulationCompression.Should().Be(DcxCompression.Zstd);
        EldenRing.RegulationZstdLevel.Should().Be(15);
        EldenRing.RegulationZstdWindowLog.Should().Be(16);
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
        TestSouls.PackedWriteCompression.Should().Be(DcxCompression.Dflt, "the one field Elden Ring and the test game could share is spelled out below");
        TestSouls.RegulationFileName.Should().BeNull().And.NotBe(EldenRing.RegulationFileName);
        TestSouls.RegulationCompression.Should().Be(DcxCompression.None).And.NotBe(EldenRing.RegulationCompression);
        TestSouls.RegulationZstdLevel.Should().Be(0).And.NotBe(EldenRing.RegulationZstdLevel);
        TestSouls.RegulationZstdWindowLog.Should().Be(0).And.NotBe(EldenRing.RegulationZstdWindowLog);
        TestSouls.RegulationKey.Should().BeEmpty();
        TestSouls.ParamdefSet.Should().Be("testsouls").And.NotBe(EldenRing.ParamdefSet);
    }
}
