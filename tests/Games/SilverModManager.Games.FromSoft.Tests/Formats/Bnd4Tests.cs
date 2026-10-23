using System.Buffers.Binary;
using FluentAssertions;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;

namespace SilverModManager.Games.FromSoft.Tests.Formats;

public class Bnd4Tests
{
    private const int HeaderSize = Bnd4Fixture.HeaderSize;
    private const int EntryHeaderSize = Bnd4Fixture.EntryHeaderSize;

    private static readonly IBinder Binder = Binders.For(EldenRingGame.Definition.Formats);

    private static byte[] Archive(params (int Id, string Name, byte[] Data)[] entries) => Bnd4Fixture.Build(entries);

    private static (int, string, byte[]) E(int id, string name, string data) => (id, name, System.Text.Encoding.ASCII.GetBytes(data));

    private static Dictionary<int, byte[]> ById(IEnumerable<BinderEntry> entries) => entries.ToDictionary(e => e.Id, e => e.Data);

    private static void SetI32(byte[] b, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(offset), value);

    private static void SetU32(byte[] b, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(offset), value);

    private static void SetI64(byte[] b, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(offset), value);

    private static long GetI64(byte[] b, int offset) => BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(offset));

    private static uint GetU32(byte[] b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset));

    [Fact]
    public void ReadReturnsEntriesInOrder()
    {
        var entries = Binder.Read(Archive(E(10, "a.fmg", "AAA"), E(20, "b.fmg", "BBBB")));
        entries.Select(e => (e.Id, e.Name)).Should().Equal([(10, "a.fmg"), (20, "b.fmg")]);
        entries[0].Data.Should().Equal("AAA"u8.ToArray());
        entries[1].Data.Should().Equal("BBBB"u8.ToArray());
    }

    [Fact]
    public void RebuildWithNoChangesIsByteIdentical()
    {
        var raw = Archive(E(10, "a.fmg", "AAA"), E(20, "b.fmg", "BBBB"));
        Binder.Rebuild(raw, new Dictionary<int, byte[]>()).Should().Equal(raw);
    }

    [Fact]
    public void RebuildWithIdenticalReplacementsIsByteIdentical()
    {
        var raw = Archive(E(10, "a.fmg", "AAA"), E(20, "b.fmg", "BBBB"), E(30, "c.fmg", "C"));
        Binder.Rebuild(raw, ById(Binder.Read(raw))).Should().Equal(raw);
    }

    [Fact]
    public void RebuildPreservesTheHashTable()
    {
        byte[] marker = [0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xBA, 0xBE];
        var raw = Bnd4Fixture.Build([E(10, "a.fmg", "AAA")], marker);
        var rebuilt = Binder.Rebuild(raw, new Dictionary<int, byte[]> { [10] = "much longer replacement data"u8.ToArray() });
        rebuilt.AsSpan().IndexOf(marker).Should().Be(raw.AsSpan().IndexOf(marker));
        rebuilt.AsSpan().IndexOf(marker).Should().BeGreaterThan(0);
    }

    [Fact]
    public void RebuildReplacesEntryData()
    {
        var raw = Archive(E(10, "a.fmg", "AAA"), E(20, "b.fmg", "BBBB"));
        var rebuilt = Binder.Rebuild(raw, new Dictionary<int, byte[]> { [20] = "replaced with something longer"u8.ToArray() });
        var byId = ById(Binder.Read(rebuilt));
        byId[10].Should().Equal("AAA"u8.ToArray());
        byId[20].Should().Equal("replaced with something longer"u8.ToArray());
    }

    [Fact]
    public void RebuildHasNoTrailingPaddingAfterTheLastEntry()
    {
        var raw = Archive(E(10, "a.fmg", "AAA"), E(20, "b.fmg", "BBBBB"));
        var rebuilt = Binder.Rebuild(raw, new Dictionary<int, byte[]> { [20] = "X"u8.ToArray() });
        var lastOffset = (int)GetU32(rebuilt, HeaderSize + EntryHeaderSize + 0x18);
        rebuilt.Length.Should().Be(lastOffset + 1);
    }

    [Fact]
    public void RebuildKeepsTheHeaderRegionIdentical()
    {
        var raw = Archive(E(10, "a.fmg", "AAA"));
        var rebuilt = Binder.Rebuild(raw, new Dictionary<int, byte[]> { [10] = "different length entirely"u8.ToArray() });
        // Names and the hash table sit between the entry headers and the data; sizes and offsets live in the headers.
        var dataOffset = (int)GetI64(raw, 0x28);
        rebuilt[..HeaderSize].Should().Equal(raw[..HeaderSize]);
        rebuilt[(HeaderSize + EntryHeaderSize)..dataOffset].Should().Equal(raw[(HeaderSize + EntryHeaderSize)..dataOffset]);
    }

    [Fact]
    public void RebuildRejectsAnUnknownEntryId()
    {
        var raw = Archive(E(10, "a.fmg", "AAA"));
        var act = () => Binder.Rebuild(raw, new Dictionary<int, byte[]> { [999] = "nope"u8.ToArray() });
        act.Should().Throw<Bnd4Exception>().WithMessage("*999*");
    }

    [Fact]
    public void ReadRejectsANonBnd4()
    {
        var act = () => Binder.Read([.. "NOTBND4"u8, .. new byte[128]]);
        act.Should().Throw<Bnd4Exception>();
    }

    [Fact]
    public void ReadRejectsABnd4WithOnlyTheMagicCorrupted()
    {
        // An all-zero buffer trips the data offset check, not the magic check, so corrupt only the magic.
        var raw = Archive(E(10, "a.fmg", "AAA"));
        "DCX\0"u8.CopyTo(raw);
        var act = () => Binder.Read(raw);
        act.Should().Throw<Bnd4Exception>().WithMessage("*magic*");
    }

    [Fact]
    public void ReadRejectsANegativeEntryCount()
    {
        var raw = Archive(E(10, "a.fmg", "AAA"));
        SetI32(raw, 0x0C, -1);
        var act = () => Binder.Read(raw);
        act.Should().Throw<Bnd4Exception>().WithMessage("*negative*");
    }

    [Fact]
    public void ReadRejectsAZeroEntryHeaderSizeWhenEntriesExist()
    {
        // With a size of 0 an absurd count would pass the table-fits-the-file check and run the read loop for ever.
        var raw = Archive(E(10, "a.fmg", "AAA"));
        SetI32(raw, 0x0C, 1_000_000_000);
        SetI64(raw, 0x20, 0);
        var act = () => Binder.Read(raw);
        act.Should().Throw<Bnd4Exception>();
    }

    [Fact]
    public void ReadRejectsAnEntryHeaderTableThatOverrunsTheFile()
    {
        var raw = Archive(E(10, "a.fmg", "AAA"));
        SetI32(raw, 0x0C, 1_000_000);
        var act = () => Binder.Read(raw);
        act.Should().Throw<Bnd4Exception>().WithMessage("*doesn't fit*");
    }

    [Fact]
    public void ReadRejectsAnEntryHeaderSizeBeyondTheFile()
    {
        var raw = Archive(E(10, "a.fmg", "AAA"));
        SetI64(raw, 0x20, long.MaxValue);
        var act = () => Binder.Read(raw);
        act.Should().Throw<Bnd4Exception>().WithMessage("*doesn't fit*");
    }

    [Fact]
    public void ReadRejectsADataOffsetThatOverlapsTheEntryHeaders()
    {
        var raw = Archive(E(10, "a.fmg", "AAA"));
        SetI64(raw, 0x28, 0);
        var act = () => Binder.Read(raw);
        act.Should().Throw<Bnd4Exception>().WithMessage("*overlaps*");
    }

    [Fact]
    public void ReadRejectsADataOffsetThatExceedsTheFile()
    {
        var raw = Archive(E(10, "a.fmg", "AAA"));
        SetI64(raw, 0x28, raw.Length + 1000);
        var act = () => Binder.Read(raw);
        act.Should().Throw<Bnd4Exception>().WithMessage("*exceeds*");
    }

    [Fact]
    public void ReadRejectsAnEntryHeaderSizeSmallerThanItsFields()
    {
        var data = new byte[0x50];
        "BND4"u8.CopyTo(data);
        SetI32(data, 0x0C, 1);
        SetI64(data, 0x20, 4);
        SetI64(data, 0x28, 0x48);
        var act = () => Binder.Read(data);
        act.Should().Throw<Bnd4Exception>().WithMessage("*smaller than*");
    }

    [Fact]
    public void ReadRejectsANegativeEntrySize()
    {
        var raw = Archive(E(10, "a.fmg", "AAA"));
        SetI64(raw, HeaderSize + 0x10, -1);
        var act = () => Binder.Read(raw);
        act.Should().Throw<Bnd4Exception>().WithMessage("*negative*");
    }

    [Fact]
    public void ReadRejectsEntryDataThatRunsPastTheBuffer()
    {
        var raw = Archive(E(10, "a.fmg", "AAA"));
        SetI64(raw, HeaderSize + 0x10, raw.Length + 1000);
        var act = () => Binder.Read(raw);
        act.Should().Throw<Bnd4Exception>().WithMessage("*runs past*");
    }

    [Fact]
    public void ReadRejectsAnEntrySizeThatWouldOverflowTheBoundCheck()
    {
        var raw = Archive(E(10, "a.fmg", "AAA"));
        SetI64(raw, HeaderSize + 0x10, long.MaxValue);
        var act = () => Binder.Read(raw);
        act.Should().Throw<Bnd4Exception>().WithMessage("*runs past*");
    }

    [Fact]
    public void ReadRejectsANegativeNameOffset()
    {
        var raw = Archive(E(10, "a.fmg", "AAA"));
        SetI32(raw, HeaderSize + 0x20, -10);
        var act = () => Binder.Read(raw);
        act.Should().Throw<Bnd4Exception>().WithMessage("*name offset*");
    }

    [Fact]
    public void ReadRejectsAnUnterminatedEntryName()
    {
        var data = new byte[0x66];
        "BND4"u8.CopyTo(data);
        SetI32(data, 0x0C, 1);
        SetI64(data, 0x20, EntryHeaderSize);
        SetI64(data, 0x28, 0x64);
        SetI64(data, HeaderSize + 0x10, 0);
        SetU32(data, HeaderSize + 0x18, 0x64);
        SetI32(data, HeaderSize + 0x1C, 1);
        SetI32(data, HeaderSize + 0x20, 0x64);
        System.Text.Encoding.Unicode.GetBytes("A").CopyTo(data, 0x64);
        var act = () => Binder.Read(data);
        act.Should().Throw<Bnd4Exception>().WithMessage("*unterminated*");
    }

    [Fact]
    public void ReadRejectsANameThatIsNotValidUtf16()
    {
        var raw = Archive(E(10, "a.fmg", "AAA"));
        var nameOffset = (int)GetU32(raw, HeaderSize + 0x20);
        raw[nameOffset] = 0x00;
        raw[nameOffset + 1] = 0xD8; // a high surrogate with nothing after it
        var act = () => Binder.Read(raw);
        act.Should().Throw<Bnd4Exception>().WithMessage("*UTF-16*");
    }

    [Fact]
    public void ReadRejectsANameOffsetPastTheEndOfTheBuffer()
    {
        var raw = Archive(E(10, "a.fmg", "AAA"));
        SetI32(raw, HeaderSize + 0x20, raw.Length + 1000);
        var act = () => Binder.Read(raw);
        act.Should().Throw<Bnd4Exception>().WithMessage("*name offset*");
    }

    [Fact]
    public void ReadRejectsAnEntryDataOffsetBeforeTheDataSection()
    {
        // An offset of 0 would hand back the file's own magic bytes as the entry's data.
        var raw = Archive(E(10, "a.fmg", "AAA"), E(20, "b.fmg", "BBBB"));
        SetU32(raw, HeaderSize + EntryHeaderSize + 0x18, 0);
        var act = () => Binder.Read(raw);
        act.Should().Throw<Bnd4Exception>().WithMessage("*precedes*");
    }

    [Fact]
    public void ReadRejectsOverlappingEntryDataRegions()
    {
        var raw = Archive(E(10, "a.fmg", "AAAA"), E(20, "b.fmg", "BBBB"));
        var dataOffset = (uint)GetI64(raw, 0x28);
        SetU32(raw, HeaderSize + EntryHeaderSize + 0x18, dataOffset + 2);
        var act = () => Binder.Read(raw);
        act.Should().Throw<Bnd4Exception>().WithMessage("*overlap*");
    }

    [Fact]
    public void RebuildPreservesAnUnalignedLeadingGapBeforeTheFirstEntry()
    {
        // data_offset marks the end of the hash table, which is not always where the first entry starts.
        var original = Archive(E(10, "a.fmg", "AAA"), E(20, "b.fmg", "BBBB"));
        var dataOffset = (int)GetI64(original, 0x28);
        byte[] gap = [0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11];
        var raw = new List<byte>(original);
        raw.InsertRange(dataOffset, gap);
        var shifted = raw.ToArray();
        for (var i = 0; i < 2; i++)
        {
            var at = HeaderSize + i * EntryHeaderSize + 0x18;
            SetU32(shifted, at, GetU32(shifted, at) + (uint)gap.Length);
        }

        var rebuilt = Binder.Rebuild(shifted, new Dictionary<int, byte[]> { [20] = "replaced with something longer"u8.ToArray() });

        var byId = ById(Binder.Read(rebuilt));
        byId[10].Should().Equal("AAA"u8.ToArray());
        byId[20].Should().Equal("replaced with something longer"u8.ToArray());
        var firstOffset = (int)GetU32(rebuilt, HeaderSize + 0x18);
        rebuilt[dataOffset..firstOffset].Should().Equal(gap);
    }

    [Fact]
    public void RebuildPropagatesTheReadGuardForAFirstEntryOffsetBeforeTheDataSection()
    {
        var raw = Archive(E(10, "a.fmg", "AAA"));
        var dataOffset = (uint)GetI64(raw, 0x28);
        SetU32(raw, HeaderSize + 0x18, dataOffset - 4);
        var act = () => Binder.Rebuild(raw, new Dictionary<int, byte[]> { [10] = "different length entirely"u8.ToArray() });
        act.Should().Throw<Bnd4Exception>().WithMessage("*precedes*");
    }

    [Fact]
    public void ABinderWithNoEntriesReadsEmptyAndRebuildsToItself()
    {
        var raw = Bnd4Fixture.Build([]);
        Binder.Read(raw).Should().BeEmpty();
        Binder.Rebuild(raw, new Dictionary<int, byte[]>()).Should().Equal(raw);
    }

    [Fact]
    public void RebuildLeavesTheSourceBufferUntouched()
    {
        var raw = Archive(E(10, "a.fmg", "AAA"), E(20, "b.fmg", "BBBB"));
        var before = raw.ToArray();
        Binder.Rebuild(raw, new Dictionary<int, byte[]> { [10] = "longer than before"u8.ToArray() });
        raw.Should().Equal(before);
    }

    // The golden blobs are erm's own output; matching them is what ties this fixture and rebuild to that tool.

    [Fact]
    public void TheFixtureReproducesTheGoldenArchiveByteForByte()
    {
        Bnd4Fixture.ThreeEntries().Should().Equal(GoldenFilesTests.Read("bnd4_three_entries.bnd"));
    }

    [Fact]
    public void RebuildReproducesTheGoldenRebuildByteForByte()
    {
        var golden = GoldenFilesTests.Read("bnd4_three_entries.bnd");
        var rebuilt = Binder.Rebuild(golden, new Dictionary<int, byte[]> { [20] = "replaced with something longer"u8.ToArray() });
        rebuilt.Should().Equal(GoldenFilesTests.Read("bnd4_three_entries_rebuilt.bnd"));
    }

    [Fact]
    public void TheGoldenArchiveReadsAsItsThreeEntries()
    {
        var entries = Binder.Read(GoldenFilesTests.Read("bnd4_three_entries.bnd"));
        entries.Select(e => (e.Id, e.Name)).Should().Equal([(10, "a.fmg"), (20, "b.fmg"), (30, "c.fmg")]);
        entries.Select(e => e.Data).Should().BeEquivalentTo(Bnd4Fixture.ThreeEntryList.Select(e => e.Data), o => o.WithStrictOrdering());
    }

    [Fact]
    public void AGameWhoseBindersAreBnd4GetsTheBnd4Binder()
    {
        Binders.For(EldenRingGame.Definition.Formats).Should().BeOfType<Bnd4>();
    }
}
