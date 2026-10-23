using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Formats;
using NexusMods.Games.FromSoft.Tests.Formats.Fixtures;
using NexusMods.Games.FromSoft.Tests.Fixtures;

namespace NexusMods.Games.FromSoft.Tests.Formats;

// BND3 serves games that arrive later; every game supported today uses BND4, and erm has no BND3 reader. There is
// no golden file to match, so the proof is a synthetic builder (Bnd3Fixture) plus round-trips through the reader.
public class Bnd3Tests
{
    private static readonly IBinder Binder = Binders.For(TestSoulsGame.Definition.Formats);

    private static byte[] Archive(byte flags = Bnd3Fixture.DefaultFlags, bool bigEndian = false, params (int Id, string Name, byte[] Data)[] entries) =>
        Bnd3Fixture.Build(entries.Length == 0 ? Bnd3Fixture.ThreeEntryList : entries, flags, bigEndian);

    private static void SetI32(byte[] b, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(offset), value);

    private static int GetI32(byte[] b, int offset) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(offset));

    private static int Stride(byte flags = Bnd3Fixture.DefaultFlags) => Bnd3Fixture.EntryHeaderSize(flags);

    private static int EntryAt(int index, byte flags = Bnd3Fixture.DefaultFlags) => Bnd3Fixture.HeaderSize + index * Stride(flags);

    [Fact]
    public void ReadReturnsEntriesInOrderWithIdsAndNames()
    {
        var entries = Binder.Read(Archive());
        entries.Select(e => (e.Id, e.Name)).Should().Equal([(10, "a.fmg"), (20, "b.fmg"), (30, "c.fmg")]);
        entries.Select(e => e.Data).Should().BeEquivalentTo(Bnd3Fixture.ThreeEntryList.Select(e => e.Data), o => o.WithStrictOrdering());
    }

    // Laid out by hand rather than through Bnd3Fixture, so the fixture and the reader cannot agree on a misreading:
    // 0x74 is how a little-endian archive stores ids, names and the uncompressed size, which makes a 0x18 stride.
    [Fact]
    public void AnArchiveLaidOutTheWayTheGamesStoreItReads()
    {
        var source = new byte[0x43];
        "BND3"u8.CopyTo(source);
        "07D7R6\0\0"u8.CopyTo(source.AsSpan(4));
        source[0x0C] = 0x74;
        SetI32(source, 0x10, 1);
        SetI32(source, 0x14, 0x3E);
        source[0x20] = 0x40;
        SetI32(source, 0x24, 3);
        SetI32(source, 0x28, 0x40);
        SetI32(source, 0x2C, 7);
        SetI32(source, 0x30, 0x38);
        SetI32(source, 0x34, 3);
        "a.fmg\0"u8.CopyTo(source.AsSpan(0x38));
        "XYZ"u8.CopyTo(source.AsSpan(0x40));

        var entry = Binder.Read(source).Should().ContainSingle().Subject;
        (entry.Id, entry.Name).Should().Be((7, "a.fmg"));
        entry.Data.Should().Equal("XYZ"u8.ToArray());
    }

    [Fact]
    public void TheFixtureStoresTheFormatByteTheWayTheGamesDo()
    {
        Archive()[0x0C].Should().Be(0x74);
        var bigEndian = Archive(bigEndian: true);
        (bigEndian[0x0C], bigEndian[0x0D], bigEndian[0x0E]).Should().Be(((byte)0x2E, (byte)1, (byte)1));
    }

    [Theory]
    [InlineData(Bnd3Fixture.Ids | Bnd3Fixture.Names)]
    [InlineData(Bnd3Fixture.Ids | Bnd3Fixture.Names | Bnd3Fixture.LongOffsets)]
    [InlineData(Bnd3Fixture.Ids | Bnd3Fixture.Names1)]
    [InlineData(Bnd3Fixture.Ids | Bnd3Fixture.Names2)]
    [InlineData(Bnd3Fixture.DefaultFlags)]
    [InlineData(Bnd3Fixture.DefaultFlags | Bnd3Fixture.LongOffsets)]
    public void EveryEntryStrideReadsBack(byte flags)
    {
        var entries = Binder.Read(Archive(flags));
        entries.Select(e => (e.Id, e.Name)).Should().Equal([(10, "a.fmg"), (20, "b.fmg"), (30, "c.fmg")]);
        entries[2].Data.Should().Equal(Bnd3Fixture.ThreeEntryList[2].Data);
    }

    [Fact]
    public void TheLongOffsetAndUncompressedSizeFlagsWidenTheEntryStride()
    {
        Stride(Bnd3Fixture.DefaultFlags | Bnd3Fixture.LongOffsets).Should().Be(Stride() + 4);
        Stride(Bnd3Fixture.Ids | Bnd3Fixture.Names).Should().Be(Stride() - 4);
        var plain = Archive();
        var wide = Archive(Bnd3Fixture.DefaultFlags | Bnd3Fixture.LongOffsets);
        // The wide archive's second entry header sits one stride further on, and still reads.
        GetI32(wide, EntryAt(1, Bnd3Fixture.DefaultFlags | Bnd3Fixture.LongOffsets) + 4).Should().Be(GetI32(plain, EntryAt(1) + 4));
    }

    [Fact]
    public void ABigEndianArchiveReads()
    {
        var entries = Binder.Read(Archive(bigEndian: true));
        entries.Select(e => (e.Id, e.Name)).Should().Equal([(10, "a.fmg"), (20, "b.fmg"), (30, "c.fmg")]);
    }

    [Fact]
    public void ABigEndianArchiveRebuildsAndReparses()
    {
        var source = Archive(bigEndian: true);
        var rebuilt = Binder.Rebuild(source, new Dictionary<int, byte[]> { [20] = "longer replacement"u8.ToArray() });
        Binder.Read(rebuilt).Single(e => e.Id == 20).Data.Should().Equal("longer replacement"u8.ToArray());
        rebuilt[0x0D].Should().Be(1);
    }

    [Fact]
    public void TheFormatsBigEndianBitAloneMakesTheArchiveBigEndian()
    {
        var source = Archive(bigEndian: true);
        source[0x0D] = 0;
        source[0x0C] |= 0x01;
        Binder.Read(source).Select(e => (e.Id, e.Name)).Should().Equal([(10, "a.fmg"), (20, "b.fmg"), (30, "c.fmg")]);
    }

    [Fact]
    public void FormatWithoutNamesYieldsEmptyNames()
    {
        var entries = Binder.Read(Archive(Bnd3Fixture.Ids));
        entries.Select(e => e.Name).Should().OnlyContain(n => n == "");
        entries.Select(e => e.Id).Should().Equal(10, 20, 30);
    }

    [Fact]
    public void ShiftJisNameWithATwoByteCharacterRoundTrips()
    {
        var name = "ぁ.fmg"; // hiragana, two bytes in Shift_JIS
        var source = Bnd3Fixture.Build([(1, name, "x"u8.ToArray()), (2, "b", "y"u8.ToArray())]);
        Binder.Read(source)[0].Name.Should().Be(name);
        var rebuilt = Binder.Rebuild(source, new Dictionary<int, byte[]> { [1] = "xyz"u8.ToArray() });
        Binder.Read(rebuilt)[0].Name.Should().Be(name);
    }

    [Fact]
    public void ShiftJisIsCodePage932()
    {
        ShiftJis.Encoding.CodePage.Should().Be(932);
        ShiftJis.Encoding.GetBytes("ぁ").Should().Equal(0x82, 0x9F);
    }

    [Fact]
    public void IdentityRebuildIsByteIdentical()
    {
        var source = Archive();
        Binder.Rebuild(source, new Dictionary<int, byte[]>()).Should().Equal(source);
    }

    [Fact]
    public void RebuildingWithTheSameDataIsByteIdentical()
    {
        var source = Archive();
        var same = Binder.Read(source).ToDictionary(e => e.Id, e => e.Data);
        Binder.Rebuild(source, same).Should().Equal(source);
    }

    [Fact]
    public void RebuildReplacesOneEntryAndReparses()
    {
        var source = Archive();
        var replacement = Enumerable.Range(0, 100).Select(i => (byte)(i ^ 0x5A)).ToArray();
        var rebuilt = Binder.Rebuild(source, new Dictionary<int, byte[]> { [20] = replacement });

        var entries = Binder.Read(rebuilt);
        entries.Select(e => (e.Id, e.Name)).Should().Equal([(10, "a.fmg"), (20, "b.fmg"), (30, "c.fmg")]);
        entries[0].Data.Should().Equal(Bnd3Fixture.ThreeEntryList[0].Data);
        entries[1].Data.Should().Equal(replacement);
        entries[2].Data.Should().Equal(Bnd3Fixture.ThreeEntryList[2].Data);
    }

    [Fact]
    public void RebuildKeepsEntriesAlignedAndBothSizesCurrent()
    {
        var source = Archive();
        var rebuilt = Binder.Rebuild(source, new Dictionary<int, byte[]> { [10] = new byte[5] });
        for (var i = 0; i < 3; i++)
            (GetI32(rebuilt, EntryAt(i) + 8) % 0x10).Should().Be(0);
        GetI32(rebuilt, EntryAt(0) + 4).Should().Be(5);
        GetI32(rebuilt, EntryAt(0) + 20).Should().Be(5);
        // 0x14 is where the names end, and names never move.
        GetI32(rebuilt, 0x14).Should().Be(GetI32(source, 0x14));
    }

    [Fact]
    public void RebuildRefusesAnUnknownId()
    {
        var act = () => Binder.Rebuild(Archive(), new Dictionary<int, byte[]> { [99] = [1] });
        act.Should().Throw<Bnd3Exception>().WithMessage("*no entry with id 99*");
    }

    [Fact]
    public void AMemberStoredCompressedIsRefused()
    {
        var source = Archive();
        source[EntryAt(1)] = Bnd3Fixture.Store(Bnd3Fixture.MemberFlags | 0x01, bigEndian: false);
        var read = () => Binder.Read(source);
        read.Should().Throw<Bnd3Exception>().WithMessage("*id=20*compressed*");
        var rebuild = () => Binder.Rebuild(source, new Dictionary<int, byte[]> { [10] = [1] });
        rebuild.Should().Throw<Bnd3Exception>().WithMessage("*id=20*compressed*");
    }

    [Fact]
    public void BadMagicIsRefused()
    {
        var source = Archive();
        source[0] = (byte)'X';
        var act = () => Binder.Read(source);
        act.Should().Throw<Bnd3Exception>().WithMessage("*bad magic*");
    }

    [Fact]
    public void ANegativeEntryCountIsRefused()
    {
        var source = Archive();
        SetI32(source, 0x10, -1);
        var act = () => Binder.Read(source);
        act.Should().Throw<Bnd3Exception>().WithMessage("*negative*");
    }

    [Fact]
    public void AHugeEntryCountWhoseHeaderTableOverrunsTheBufferIsRefused()
    {
        var source = Archive();
        SetI32(source, 0x10, int.MaxValue);
        var act = () => Binder.Read(source);
        act.Should().Throw<Bnd3Exception>().WithMessage("*doesn't fit*");
    }

    [Fact]
    public void ANegativeEntrySizeIsRefused()
    {
        var source = Archive();
        SetI32(source, EntryAt(0) + 4, -1);
        var act = () => Binder.Read(source);
        act.Should().Throw<Bnd3Exception>().WithMessage("*negative size*");
    }

    [Fact]
    public void ADataOffsetInsideTheHeaderTableIsRefused()
    {
        var source = Archive();
        SetI32(source, EntryAt(0) + 8, Bnd3Fixture.HeaderSize);
        var act = () => Binder.Read(source);
        act.Should().Throw<Bnd3Exception>().WithMessage("*precedes*");
    }

    [Fact]
    public void OverlappingEntryDataIsRefused()
    {
        var source = Archive();
        SetI32(source, EntryAt(1) + 8, GetI32(source, EntryAt(0) + 8));
        var act = () => Binder.Read(source);
        act.Should().Throw<Bnd3Exception>().WithMessage("*overlaps*");
    }

    [Fact]
    public void EntryDataPastTheBufferIsRefused()
    {
        var source = Archive();
        SetI32(source, EntryAt(2) + 4, 10_000);
        var act = () => Binder.Read(source);
        act.Should().Throw<Bnd3Exception>().WithMessage("*runs past*");
    }

    [Fact]
    public void ANameOffsetOutsideTheBufferIsRefused()
    {
        var source = Archive();
        SetI32(source, EntryAt(0) + 16, source.Length + 5);
        var act = () => Binder.Read(source);
        act.Should().Throw<Bnd3Exception>().WithMessage("*name offset*");
    }

    [Fact]
    public void AnUnterminatedNameIsRefused()
    {
        // Point the last entry's name at the final byte of a buffer whose tail is non-zero.
        var source = Archive(entries: [(1, "a", "AB"u8.ToArray())]);
        SetI32(source, EntryAt(0) + 16, source.Length - 1);
        var act = () => Binder.Read(source);
        act.Should().Throw<Bnd3Exception>().WithMessage("*unterminated*");
    }

    [Fact]
    public void ATruncatedHeaderIsRefused()
    {
        var act = () => Binder.Read(Archive().AsSpan(0, 0x10));
        act.Should().Throw<Bnd3Exception>();
    }

    [Fact]
    public void BothBindersShareTheInterfaceSoACallerNeverBranchesOnTheGame()
    {
        var games = new[] { EldenRingGame.Definition.Formats, TestSoulsGame.Definition.Formats };
        var binders = games.Select(Binders.For).ToArray();
        binders.Should().AllBeAssignableTo<IBinder>();
        binders[0].Should().BeOfType<Bnd4>();
        binders[1].Should().BeOfType<Bnd3>();
    }
}
