using System.Numerics;
using System.Text;
using FluentAssertions;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;

namespace SilverModManager.Games.FromSoft.Tests.Formats;

public class Bhd5Tests
{
    // Measured from the real Data0.bhd index, not computed by the code under test: this exact hash is the entry that
    // yields the game's menu text.
    private const string MenuPath = "msg/engus/menu_dlc02.msgbnd.dcx";
    private const ulong MenuHash = 0xF6CA09AEB7E65CC9;

    private static FromSoftFormatTraits EldenRing => EldenRingGame.Definition.Formats;

    private static FromSoftFormatTraits TestSouls => TestSoulsGame.Definition.Formats;

    private static FromSoftFormatTraits Game(string name) => name == "EldenRing" ? EldenRing : TestSouls;

    private static readonly (ulong NameHash, int PaddedSize, int UnpaddedSize, long Offset)[] GoldenEntries =
    [
        (0x1111111111111111, 32, 30, 0),
        (0x2222222222222222, 64, 64, 32),
        (0x3333333333333333, 16, 5, 96),
    ];

    [Fact]
    public void ThePathHashMatchesTheValueMeasuredFromTheRealIndex()
        => Bhd5.PathHash(Bhd5.Normalise(MenuPath), EldenRing).Should().Be(MenuHash);

    [Fact]
    public void NormaliseForcesALeadingSlash()
    {
        Bhd5.Normalise("msg/engus/x.dcx").Should().Be("/msg/engus/x.dcx");
        Bhd5.Normalise("/msg/engus/x.dcx").Should().Be("/msg/engus/x.dcx");
    }

    [Fact]
    public void NormaliseLowercasesAndSquaresTheSeparators()
        => Bhd5.Normalise("MSG\\ENGUS\\X.DCX").Should().Be("/msg/engus/x.dcx");

    [Fact]
    public void APathWithoutTheLeadingSlashHashesDifferently()
        => Bhd5.PathHash("msg/engus/menu_dlc02.msgbnd.dcx", EldenRing).Should().NotBe(MenuHash);

    [Theory]
    [InlineData("EldenRing")]
    [InlineData("TestSouls")]
    public void ThePathHashIsTheDocumentedRecurrence(string game)
    {
        var traits = Game(game);
        var mask = (BigInteger.One << traits.PathHashBits) - 1;
        var expect = BigInteger.Zero;
        foreach (var ch in "/abc")
            expect = (ch + traits.PathHashPrime * expect) & mask;
        Bhd5.PathHash("/abc", traits).Should().Be((ulong)expect);
    }

    [Theory]
    [InlineData("EldenRing", 64)]
    [InlineData("TestSouls", 32)]
    public void ThePathHashWrapsAtTheGamesWidth(string game, int bits)
    {
        var traits = Game(game);
        var path = "/" + new string('a', 200);
        var expect = BigInteger.Zero;
        foreach (var ch in path)
            expect = ch + traits.PathHashPrime * expect;
        var hash = Bhd5.PathHash(path, traits);
        hash.Should().Be((ulong)(expect & ((BigInteger.One << bits) - 1)));
        ((BigInteger)hash).Should().BeLessThan(BigInteger.One << bits);
    }

    [Fact]
    public void AHashWidthOutsideOneToSixtyFourIsRefused()
    {
        var act = () => Bhd5.PathHash("/a", EldenRing with { PathHashBits = 65 });
        act.Should().Throw<Bhd5Exception>().WithMessage("*65*");
    }

    [Theory]
    [InlineData("EldenRing")]
    [InlineData("TestSouls")]
    public void ParseReadsEveryEntryOutOfItsBucket(string game)
    {
        var index = Bhd5.Parse(Bhd5Fixture.Build(GoldenEntries), Game(game));
        index.Entries.Keys.Should().BeEquivalentTo(GoldenEntries.Select(e => e.NameHash));
        index.Entries[0x2222222222222222].PaddedSize.Should().Be(64);
        index.Entries[0x3333333333333333].Offset.Should().Be(96);
    }

    [Theory]
    [InlineData("EldenRing")]
    [InlineData("TestSouls")]
    public void ParseKeepsPaddedAndUnpaddedSizesApart(string game)
    {
        var entry = Bhd5.Parse(Bhd5Fixture.Build([(0x55UL, 48, 33, 0L)]), Game(game)).Entries[0x55];
        (entry.PaddedSize, entry.UnpaddedSize).Should().Be((48, 33));
    }

    [Fact]
    public void ParseRefusesSomethingThatIsNotABhd5()
    {
        var act = () => Bhd5.Parse([..Encoding.ASCII.GetBytes("NOPE"), ..new byte[64]], EldenRing);
        act.Should().Throw<Bhd5Exception>().WithMessage("*BHD5*");
    }

    [Fact]
    public void ParseRefusesAnIndexCutShortInsideItsEntries()
    {
        var plain = Bhd5Fixture.Build(GoldenEntries);
        var act = () => Bhd5.Parse(plain[..^20], EldenRing);
        act.Should().Throw<Bhd5Exception>();
    }

    [Fact]
    public void ParseReportsTheSalt()
        => Bhd5.Parse(Bhd5Fixture.Build(Array.Empty<Bhd5Fixture.Entry>(), salt: "GR_other"), EldenRing).Salt.Should().Be("GR_other");

    [Fact]
    public void ParseReportsTheBucketCount()
        => Bhd5.Parse(Bhd5Fixture.Build(GoldenEntries, bucketCount: 7), EldenRing).BucketCount.Should().Be(7);

    [Fact]
    public void ParseRefusesALayoutItDoesNotImplementAndNamesIt()
    {
        var act = () => Bhd5.Parse(Bhd5Fixture.Build(GoldenEntries), EldenRing with { ArchiveLayout = (Bhd5Layout)99 });
        act.Should().Throw<Bhd5Exception>().WithMessage("*99*");
    }

    [Fact]
    public void ParseReadsTheKeyAndRangesOfAnEncryptedEntryAndSkipsTheSentinel()
    {
        var key = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var plain = Bhd5Fixture.Build([new Bhd5Fixture.Entry(7, 48, 40, 0, key, [(0, 32), (-1, -1), (32, 48)])]);
        var entry = Bhd5.Parse(plain, TestSouls).Entries[7];
        entry.AesKey.Should().Equal(key);
        entry.Ranges.Should().Equal(new AesRange(0, 32), new AesRange(32, 48));
    }

    [Fact]
    public void AnEntryWithNoKeyOffsetHasNoKey()
    {
        var entry = Bhd5.Parse(Bhd5Fixture.Build(GoldenEntries), EldenRing).Entries[0x1111111111111111];
        entry.AesKey.Should().BeNull();
        entry.Ranges.Should().BeEmpty();
    }

    [Fact]
    public void TheFixtureReproducesErmsGoldenIndexByteForByte()
    {
        var built = Bhd5Fixture.Build(GoldenEntries, bucketCount: 2, salt: "GR_test");
        built.Should().Equal(GoldenFilesTests.Read("bhd5_two_buckets.bhd5"));
    }

    [Theory]
    [InlineData("EldenRing")]
    [InlineData("TestSouls")]
    public void ParseReadsTheGoldenIndexErmBuilt(string game)
    {
        var index = Bhd5.Parse(GoldenFilesTests.Read("bhd5_two_buckets.bhd5"), Game(game));
        index.Salt.Should().Be("GR_test");
        index.BucketCount.Should().Be(2);
        index.Entries.Values.Select(e => (e.NameHash, e.PaddedSize, e.UnpaddedSize, e.Offset))
            .Should().BeEquivalentTo(GoldenEntries);
    }

    [Fact]
    public void EveryShippedArchiveHasAKey()
    {
        EldenRing.Archives.Should().NotBeEmpty();
        foreach (var archive in EldenRing.Archives)
        {
            archive.Name.Should().NotBeNullOrEmpty();
            archive.Modulus.Should().BeGreaterThan(BigInteger.One << 2000, $"{archive.Name} has a 2048-bit modulus");
            archive.Exponent.Should().BeGreaterThan(BigInteger.One);
        }
    }

    [Fact]
    public void DecryptHeaderReadsWhatTheFixtureSigned()
    {
        var plain = Bhd5Fixture.Build(GoldenEntries);
        var signed = Bhd5Fixture.Sign(plain, Bhd5Fixture.TestKey);
        var back = Bhd5.DecryptHeader(signed, TestSouls.Archives[0]);
        back.AsSpan(0, plain.Length).SequenceEqual(plain).Should().BeTrue();
        (back.Length % 255).Should().Be(0, "each 256-byte block gives 255 bytes");
    }

    [Fact]
    public void DecryptHeaderRejectsAWrongKey()
    {
        var act = () => Bhd5.DecryptHeader(Enumerable.Repeat((byte)0xff, 256).ToArray(), EldenRing.Archives[0]);
        act.Should().Throw<Bhd5Exception>().WithMessage("*Data0*");
    }

    [Fact]
    public void DecryptHeaderRejectsATruncatedArchive()
    {
        var act = () => Bhd5.DecryptHeader(new byte[100], EldenRing.Archives[0]);
        act.Should().Throw<Bhd5Exception>().WithMessage("*truncated*");
    }

    [Fact]
    public void DecryptHeaderRejectsAnEmptyArchive()
    {
        var act = () => Bhd5.DecryptHeader([], EldenRing.Archives[0]);
        act.Should().Throw<Bhd5Exception>().WithMessage("*truncated*");
    }
}
