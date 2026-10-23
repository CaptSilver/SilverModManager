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
}
