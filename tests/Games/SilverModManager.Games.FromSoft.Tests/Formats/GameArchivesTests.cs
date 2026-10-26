using System.Collections.Immutable;
using System.Text;
using FluentAssertions;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;

namespace SilverModManager.Games.FromSoft.Tests.Formats;

public class GameArchivesTests : IDisposable
{
    private const string MenuPath = "msg/engus/menu_dlc02.msgbnd.dcx";

    private readonly string _game = Path.Combine(Directory.CreateTempSubdirectory("game-archives").FullName, "Game");

    public GameArchivesTests() => Directory.CreateDirectory(_game);

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_game)!, recursive: true);

    private static FromSoftFormatTraits TestSouls => TestSoulsGame.Definition.Formats;

    private static byte[] Payload => Encoding.ASCII.GetBytes("menu text, long enough that DEFLATE has something to do. ").Concat(Enumerable.Range(0, 200).Select(i => (byte)(i % 7))).ToArray();

    private static byte[] WrappedPayload => Dcx.Write(Payload, TestSouls.PackedWrite);

    /// <summary>Writes <c>Name.bhd</c> and <c>Name.bdt</c>; the payload sits after <paramref name="lead"/> bytes of unrelated data.</summary>
    private void WriteArchive(
        string name, Bhd5Fixture.SigningKey key, string path, FromSoftFormatTraits traits,
        byte[] stored, int unpadded, bool encrypted, int lead = 48)
        => GameArchiveFixture.WriteSized(_game, name, key, traits, encrypted, lead, [(path, stored, unpadded)]);

    [Fact]
    public void ReadFileReturnsTheDcxAndTheDcxReaderRecoversThePayload()
    {
        var dcx = WrappedPayload;
        WriteArchive("TestData0", Bhd5Fixture.TestKey, MenuPath, TestSouls, dcx, dcx.Length, encrypted: true);

        var read = GameArchives.ReadFile(_game, MenuPath, TestSouls);

        read.Should().Equal(dcx);
        Dcx.Read(read, TestSouls).Should().Equal(Payload);
    }

    [Fact]
    public void AFileWithNoKeyIsReturnedAsStored()
    {
        var raw = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();
        WriteArchive("TestData0", Bhd5Fixture.TestKey, "a/b.bin", TestSouls, raw, raw.Length, encrypted: false);
        GameArchives.ReadFile(_game, "a/b.bin", TestSouls).Should().Equal(raw);
    }

    [Fact]
    public void AnUnpaddedSizeOfZeroKeepsThePaddedBuffer()
    {
        var raw = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();
        WriteArchive("TestData0", Bhd5Fixture.TestKey, "a/b.bin", TestSouls, raw, unpadded: 0, encrypted: true);
        GameArchives.ReadFile(_game, "a/b.bin", TestSouls).Should().HaveCount(48).And.StartWith(raw);
    }

    [Fact]
    public void ALookupIsCaseInsensitiveLikeTheGame()
    {
        var dcx = WrappedPayload;
        WriteArchive("TestData0", Bhd5Fixture.TestKey, MenuPath, TestSouls, dcx, dcx.Length, encrypted: true);
        GameArchives.ReadFile(_game, MenuPath.ToUpperInvariant().Replace('/', '\\'), TestSouls).Should().Equal(dcx);
    }

    [Fact]
    public void AMissingPathIsAnErrorNotEmptyBytes()
    {
        var dcx = WrappedPayload;
        WriteArchive("TestData0", Bhd5Fixture.TestKey, MenuPath, TestSouls, dcx, dcx.Length, encrypted: true);
        var act = () => GameArchives.ReadFile(_game, "Msg\\NoSuchFile.dcx", TestSouls);
        act.Should().Throw<Bhd5Exception>().WithMessage("*/msg/nosuchfile.dcx*TestData0*");
    }

    [Fact]
    public void FindReportsTheArchiveThatHoldsThePath()
    {
        var dcx = WrappedPayload;
        WriteArchive("TestData0", Bhd5Fixture.TestKey, MenuPath, TestSouls, dcx, dcx.Length, encrypted: true);
        GameArchives.Find(_game, MenuPath, TestSouls)!.Value.Archive.Name.Should().Be("TestData0");
        GameArchives.Find(_game, "not/there", TestSouls).Should().BeNull();
    }

    [Fact]
    public void ArchivesAreWalkedInOrderAndAnAbsentOneIsSkipped()
    {
        var second = TestSouls with { Archives = [Bhd5Fixture.TestKey.Archive("Absent"), Bhd5Fixture.TestKey.Archive("TestData1")] };
        var dcx = WrappedPayload;
        WriteArchive("TestData1", Bhd5Fixture.TestKey, MenuPath, second, dcx, dcx.Length, encrypted: true);
        GameArchives.Find(_game, MenuPath, second)!.Value.Archive.Name.Should().Be("TestData1");
        GameArchives.ReadFile(_game, MenuPath, second).Should().Equal(dcx);
    }

    [Fact]
    public void TheFirstArchiveThatHoldsThePathWins()
    {
        var both = TestSouls with { Archives = [Bhd5Fixture.TestKey.Archive("TestData0"), Bhd5Fixture.TestKey.Archive("TestData1")] };
        var first = new byte[32];
        var second = Enumerable.Repeat((byte)9, 32).ToArray();
        WriteArchive("TestData0", Bhd5Fixture.TestKey, "x/y", both, first, 32, encrypted: false);
        WriteArchive("TestData1", Bhd5Fixture.TestKey, "x/y", both, second, 32, encrypted: false);
        GameArchives.ReadFile(_game, "x/y", both).Should().Equal(first);
    }

    [Fact]
    public void AnArchiveWhoseKeyIsWrongFailsLoudlyInsteadOfBeingSkipped()
    {
        // 0xff throughout cannot be a block RSA-encrypted under a genuine key, so the outcome does not depend on luck.
        var real = TestSouls with { Archives = [EldenRingGame.Definition.Formats.Archives[0]] };
        File.WriteAllBytes(Path.Combine(_game, "Data0.bhd"), Enumerable.Repeat((byte)0xff, 256).ToArray());
        var act = () => GameArchives.Find(_game, MenuPath, real);
        act.Should().Throw<Bhd5Exception>().WithMessage("*Data0*");
    }

    [Fact]
    public void UnreadableNamesTheFirstArchiveWhoseIndexCannotBeLoaded()
    {
        var three = TestSouls with { Archives = [Bhd5Fixture.TestKey.Archive("TestData0"), Bhd5Fixture.TestKey.Archive("TestData1"), Bhd5Fixture.TestKey.Archive("TestData2")] };
        WriteArchive("TestData0", Bhd5Fixture.TestKey, MenuPath, three, WrappedPayload, WrappedPayload.Length, encrypted: true);
        File.WriteAllBytes(Path.Combine(_game, "TestData1.bhd"), new byte[100]);
        File.WriteAllBytes(Path.Combine(_game, "TestData2.bhd"), new byte[100]);

        var unreadable = GameArchives.Unreadable(_game, three);

        unreadable!.Value.Archive.Name.Should().Be("TestData1");
        unreadable.Value.Problem.Should().Contain("TestData1.bhd").And.Contain("truncated");
    }

    [Fact]
    public void UnreadableIsNullWhenEveryArchiveTheInstallShipsOpens()
    {
        var withAbsent = TestSouls with { Archives = [Bhd5Fixture.TestKey.Archive("TestData0"), Bhd5Fixture.TestKey.Archive("Absent")] };
        WriteArchive("TestData0", Bhd5Fixture.TestKey, MenuPath, withAbsent, WrappedPayload, WrappedPayload.Length, encrypted: true);

        GameArchives.Unreadable(_game, withAbsent).Should().BeNull();
    }

    [Fact]
    public void ABdtThatEndsBeforeTheEntryIsAnError()
    {
        var dcx = WrappedPayload;
        WriteArchive("TestData0", Bhd5Fixture.TestKey, MenuPath, TestSouls, dcx, dcx.Length, encrypted: true);
        var bdt = Path.Combine(_game, "TestData0.bdt");
        File.WriteAllBytes(bdt, File.ReadAllBytes(bdt)[..60]);
        var act = () => GameArchives.ReadFile(_game, MenuPath, TestSouls);
        act.Should().Throw<Bhd5Exception>().WithMessage("*TestData0.bdt*");
    }

    [Fact]
    public void AGameWithNoArchivesCannotReadFromThem()
    {
        var none = TestSouls with { Archives = ImmutableArray<ArchiveIndex>.Empty };
        GameArchives.Find(_game, MenuPath, none).Should().BeNull();
        var act = () => GameArchives.ReadFile(_game, MenuPath, none);
        act.Should().Throw<Bhd5Exception>().WithMessage("*not read from archives*");
    }

    [Fact]
    public void ARewrittenIndexIsNotServedFromTheCache()
    {
        var dcx = WrappedPayload;
        WriteArchive("TestData0", Bhd5Fixture.TestKey, "old/path", TestSouls, dcx, dcx.Length, encrypted: true);
        GameArchives.Find(_game, "old/path", TestSouls).Should().NotBeNull();

        WriteArchive("TestData0", Bhd5Fixture.TestKey, "new/path", TestSouls, dcx, dcx.Length, encrypted: true, lead: 64);
        // The rewrite has the same length, and two writes this close can share a coarse mtime; a game update cannot.
        var bhd = Path.Combine(_game, "TestData0.bhd");
        File.SetLastWriteTimeUtc(bhd, File.GetLastWriteTimeUtc(bhd).AddSeconds(1));

        GameArchives.Find(_game, "old/path", TestSouls).Should().BeNull();
        GameArchives.Find(_game, "new/path", TestSouls).Should().NotBeNull();
    }

    [Fact]
    public void ACachedIndexIsNotHandedToAnotherKey()
    {
        var dcx = WrappedPayload;
        WriteArchive("TestData0", Bhd5Fixture.TestKey, MenuPath, TestSouls, dcx, dcx.Length, encrypted: true);
        GameArchives.Find(_game, MenuPath, TestSouls).Should().NotBeNull();

        var otherKey = TestSouls with { Archives = [EldenRingGame.Definition.Formats.Archives[0] with { Name = "TestData0" }] };
        var act = () => GameArchives.Find(_game, MenuPath, otherKey);
        act.Should().Throw<Bhd5Exception>();
    }

    [Fact]
    public void ANegativePaddedSizeIsRefusedNamingThePath()
    {
        var entry = new Bhd5Fixture.Entry(Bhd5.PathHash(Bhd5.Normalise("a/b.bin"), TestSouls), -16, 0, 0);
        File.WriteAllBytes(Path.Combine(_game, "TestData0.bhd"), Bhd5Fixture.Sign(Bhd5Fixture.Build([entry]), Bhd5Fixture.TestKey));
        File.WriteAllBytes(Path.Combine(_game, "TestData0.bdt"), new byte[64]);
        var act = () => GameArchives.ReadFile(_game, "a/b.bin", TestSouls);
        act.Should().Throw<Bhd5Exception>().WithMessage("*/a/b.bin*-16*");
    }
}
