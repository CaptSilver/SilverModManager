using FluentAssertions;
using SilverModManager.Sdk.Native;

namespace SilverModManager.Sdk.Tests.Native;

public class PeFileVersionTests
{
    private const uint Signature = 0xFEEF04BD;
    private const uint StrucVersion = 0x00010000;

    /// <summary>A VS_FIXEDFILEINFO as the first fields of the struct lay them out: signature, struct version, file version.</summary>
    private static byte[] FixedFileInfo(uint structVersion, uint fileVersionMs, uint fileVersionLs)
    {
        var bytes = new byte[52];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), Signature);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), structVersion);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), fileVersionMs);
        BitConverter.TryWriteBytes(bytes.AsSpan(12), fileVersionLs);
        return bytes;
    }

    private static MemoryStream Around(byte[] struc, int before = 100, int after = 100)
    {
        var bytes = new byte[before + struc.Length + after];
        Array.Fill(bytes, (byte)0xAB);
        struc.CopyTo(bytes, before);
        return new MemoryStream(bytes);
    }

    [Fact]
    public void Read_FindsTheFileVersionOfTheFixedFileInfo()
    {
        using var stream = Around(FixedFileInfo(StrucVersion, 0x0002_0007, 0x0001_0000));

        Assert.Equal("2.7.1.0", PeFileVersion.Read(stream));
    }

    [Fact]
    public void ReadVersion_ReturnsTheSameVersionAsAVersionValue()
    {
        using var stream = Around(FixedFileInfo(StrucVersion, 0x0002_0007, 0x0001_0000));

        Assert.Equal(new Version(2, 7, 1, 0), PeFileVersion.ReadVersion(stream));
    }

    [Fact]
    public void ReadVersion_WithNoSignature_ReturnsNull()
    {
        using var stream = new MemoryStream(new byte[4096]);

        Assert.Null(PeFileVersion.ReadVersion(stream));
    }

    [Fact]
    public void Read_WithNoSignature_ReturnsNull()
    {
        using var stream = new MemoryStream(new byte[4096]);

        Assert.Null(PeFileVersion.Read(stream));
    }

    [Fact]
    public void Read_WithAWrongStructVersion_ReturnsNull()
    {
        using var stream = Around(FixedFileInfo(0x00020000, 0x0002_0007, 0x0001_0000));

        Assert.Null(PeFileVersion.Read(stream));
    }

    [Fact]
    public void Read_SkipsACoincidentalSignatureAndTakesTheRealStruct()
    {
        var coincidence = FixedFileInfo(0xDEADBEEF, 0x0009_0009, 0x0009_0009);
        var real = FixedFileInfo(StrucVersion, 0x0002_0007, 0x0001_0000);
        using var stream = new MemoryStream([..coincidence, ..new byte[40], ..real]);

        Assert.Equal("2.7.1.0", PeFileVersion.Read(stream));
    }

    [Fact]
    public void Read_FindsAStructThatStraddlesTheReadBuffer()
    {
        var struc = FixedFileInfo(StrucVersion, 0x0002_0007, 0x0001_0000);
        // The scan reads in blocks; put the struct across every offset near a few plausible block sizes.
        foreach (var size in new[] { 4096, 8192, 65536, 81920 })
        {
            for (var offset = size - 6; offset <= size + 2; offset++)
            {
                using var stream = Around(struc, before: offset, after: 10);
                Assert.Equal("2.7.1.0", PeFileVersion.Read(stream));
            }
        }
    }

    [Fact]
    public void Read_OfATruncatedStruct_ReturnsNull()
    {
        var struc = FixedFileInfo(StrucVersion, 0x0002_0007, 0x0001_0000);
        using var stream = new MemoryStream(struc[..12]);

        Assert.Null(PeFileVersion.Read(stream));
    }

    // --- VS_VERSIONINFO ---

    private static int Align4(int value) => (value + 3) & ~3;

    /// <summary>One node of a version resource: header, UTF-16 key, padding, value, then children. Lengths exclude trailing padding, as the format has it.</summary>
    private static byte[] Node(string key, byte[] value, int valueLength, short type, params byte[][] children)
    {
        var bytes = new List<byte>([0, 0]);
        bytes.AddRange(BitConverter.GetBytes((ushort)valueLength));
        bytes.AddRange(BitConverter.GetBytes(type));
        bytes.AddRange(System.Text.Encoding.Unicode.GetBytes(key + "\0"));
        while (bytes.Count % 4 != 0) bytes.Add(0);
        bytes.AddRange(value);
        foreach (var child in children)
        {
            while (bytes.Count % 4 != 0) bytes.Add(0);
            bytes.AddRange(child);
        }

        var all = bytes.ToArray();
        BitConverter.TryWriteBytes(all.AsSpan(0), (ushort)all.Length);
        return all;
    }

    private static byte[] StringValue(string key, string value) =>
        Node(key, System.Text.Encoding.Unicode.GetBytes(value + "\0"), value.Length + 1, 1);

    private static byte[] StringFileInfo(params byte[][] strings) =>
        Node("StringFileInfo", [], 0, 1, Node("040904B0", [], 0, 1, strings));

    /// <summary>A VS_VERSIONINFO as a resource holds it: fixed info (0x34 bytes) then the given children.</summary>
    private static byte[] FakeVersionInfo(uint fileVersionMs, uint fileVersionLs, params byte[][] children) =>
        Node("VS_VERSION_INFO", FixedFileInfo(StrucVersion, fileVersionMs, fileVersionLs)[..0x34], 0x34, 0, children);

    [Fact]
    public void ReadVersion_OrdersWordsMajorMinorBuildRevision()
    {
        // Four different words, so a swapped pair shows: major 1, minor 2, then the low word's halves 3 and 4.
        using var stream = Around(FixedFileInfo(StrucVersion, 0x0001_0002, 0x0003_0004));

        PeFileVersion.ReadVersion(stream).Should().Be(new Version(1, 2, 3, 4));
    }

    [Fact]
    public void ReadProductVersion_FindsTheStringTableValue()
    {
        var info = FakeVersionInfo(0x0001_0002, 0x0003_0004,
            StringFileInfo(StringValue("FileVersion", "9.9.9.9"), StringValue("ProductVersion", "1, 6, 1170, 0")));
        using var stream = Around(info);

        PeFileVersion.ReadProductVersion(stream).Should().Be("1, 6, 1170, 0");
    }

    [Fact]
    public void ReadProductVersion_FindsTheValueAfterAnotherChild()
    {
        var info = FakeVersionInfo(0x0001_0002, 0x0003_0004,
            Node("VarFileInfo", [], 0, 1, Node("Translation", [9, 4, 0xB0, 4], 4, 0)),
            StringFileInfo(StringValue("ProductVersion", "2.0")));
        using var stream = Around(info);

        PeFileVersion.ReadProductVersion(stream).Should().Be("2.0");
    }

    [Fact]
    public void ReadProductVersion_AtTheStartOfTheStream_Works()
    {
        var info = FakeVersionInfo(1, 1, StringFileInfo(StringValue("ProductVersion", "3.1")));
        using var stream = new MemoryStream(info);

        // The struct starts 40 bytes in, so a stream of just the resource has it at offset 40.
        PeFileVersion.ReadProductVersion(stream).Should().Be("3.1");
    }

    [Fact]
    public void ReadProductVersion_NullWithoutStringFileInfo()
    {
        using var stream = Around(FakeVersionInfo(0x0001_0002, 0x0003_0004));

        PeFileVersion.ReadProductVersion(stream).Should().BeNull();
    }

    [Fact]
    public void ReadProductVersion_NullWithoutAProductVersionString()
    {
        using var stream = Around(FakeVersionInfo(1, 1, StringFileInfo(StringValue("FileVersion", "1.0"))));

        PeFileVersion.ReadProductVersion(stream).Should().BeNull();
    }

    [Fact]
    public void ReadProductVersion_NullWithNoFixedFileInfo()
    {
        using var stream = new MemoryStream(new byte[4096]);

        PeFileVersion.ReadProductVersion(stream).Should().BeNull();
    }

    [Fact]
    public void ReadProductVersion_NullWhenTheStructIsNotInsideAVersionResource()
    {
        using var stream = Around(FixedFileInfo(StrucVersion, 1, 1));

        PeFileVersion.ReadProductVersion(stream).Should().BeNull();
    }

    [Fact]
    public void ReadProductVersion_NullWhenTheResourceIsTruncated()
    {
        var info = FakeVersionInfo(1, 1, StringFileInfo(StringValue("ProductVersion", "3.1")));
        using var stream = new MemoryStream(info[..(info.Length - 10)]);

        PeFileVersion.ReadProductVersion(stream).Should().BeNull();
    }

    [Fact]
    public void ReadVersions_ReturnsTheFixedWordsAndTheProductVersionStringTogether()
    {
        var info = FakeVersionInfo(0x0001_0000, 0, StringFileInfo(StringValue("ProductVersion", "1.4.15.0")));
        using var stream = Around(info);

        var versions = PeFileVersion.ReadVersions(stream);

        versions.Fixed.Should().Be(new Version(1, 0, 0, 0));
        versions.ProductVersion.Should().Be("1.4.15.0");
    }

    [Fact]
    public void ReadVersions_HasNoStringWhenTheStructIsBare()
    {
        using var stream = Around(FixedFileInfo(StrucVersion, 0x0001_0002, 0x0003_0004));

        var versions = PeFileVersion.ReadVersions(stream);

        versions.Fixed.Should().Be(new Version(1, 2, 3, 4));
        versions.ProductVersion.Should().BeNull();
    }

    [Fact]
    public void ReadVersions_HasNothingWhenThereIsNoStruct()
    {
        using var stream = new MemoryStream(new byte[4096]);

        var versions = PeFileVersion.ReadVersions(stream);

        versions.Fixed.Should().BeNull();
        versions.ProductVersion.Should().BeNull();
    }

    [Fact]
    public void ReadVersions_StartsFromTheBeginningWhateverThePosition()
    {
        using var stream = Around(FakeVersionInfo(1, 1, StringFileInfo(StringValue("ProductVersion", "3.1"))));
        stream.Position = stream.Length;

        PeFileVersion.ReadVersions(stream).ProductVersion.Should().Be("3.1");
    }

    [Fact]
    public void ReadVersions_ScansAHugeFileOnce()
    {
        // The struct sits at the very end, as it does in SkyrimVR.exe, so a second scan would read the file twice.
        var info = FakeVersionInfo(0x0001_0000, 0, StringFileInfo(StringValue("ProductVersion", "1.4.15.0")));
        var bytes = new byte[1_000_000 + info.Length];
        Array.Fill(bytes, (byte)0xAB);
        info.CopyTo(bytes, 1_000_000);
        using var stream = new CountingStream(new MemoryStream(bytes));

        PeFileVersion.ReadVersions(stream).ProductVersion.Should().Be("1.4.15.0");

        stream.BytesRead.Should().BeLessThan(bytes.Length * 3 / 2);
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        private int Count(int read) { BytesRead += read; return read; }
    }

    /// <summary>MZ header, e_lfanew, PE signature, COFF header and an optional header that starts with <paramref name="magic"/>.</summary>
    private static byte[] FakePe(ushort magic, bool peSignature = true)
    {
        var bytes = new byte[0x200];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.TryWriteBytes(bytes.AsSpan(0x3C), 0x80);
        if (peSignature) "PE\0\0"u8.CopyTo(bytes.AsSpan(0x80));
        BitConverter.TryWriteBytes(bytes.AsSpan(0x80 + 4 + 16), (ushort)0xE0);
        BitConverter.TryWriteBytes(bytes.AsSpan(0x80 + 4 + 20), magic);
        return bytes;
    }

    [Theory]
    [InlineData((ushort)0x10b)]
    [InlineData((ushort)0x20b)]
    public void IsPortableExecutable_TrueForBothOptionalHeaderMagics(ushort magic)
    {
        using var stream = new MemoryStream(FakePe(magic));

        PeFileVersion.IsPortableExecutable(stream).Should().BeTrue();
    }

    [Fact]
    public void IsPortableExecutable_FalseForAnUnknownOptionalHeaderMagic()
    {
        using var stream = new MemoryStream(FakePe(0x107));

        PeFileVersion.IsPortableExecutable(stream).Should().BeFalse();
    }

    [Fact]
    public void IsPortableExecutable_FalseWithoutThePeSignature()
    {
        using var stream = new MemoryStream(FakePe(0x10b, peSignature: false));

        PeFileVersion.IsPortableExecutable(stream).Should().BeFalse();
    }

    [Fact]
    public void IsPortableExecutable_FalseWithoutTheDosMagic()
    {
        var bytes = FakePe(0x10b);
        bytes[0] = 0;
        using var stream = new MemoryStream(bytes);

        PeFileVersion.IsPortableExecutable(stream).Should().BeFalse();
    }

    [Fact]
    public void IsPortableExecutable_FalseForAShortStream()
    {
        using var stream = new MemoryStream("MZ"u8.ToArray());

        PeFileVersion.IsPortableExecutable(stream).Should().BeFalse();
    }
}
