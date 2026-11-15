using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Wabbajack.Install;

namespace SilverModManager.Wabbajack.Tests.Install;

public class FileContentsTests(ITestOutputHelper helper) : InstallTestBase<FileContentsTests>(helper)
{
    // Longer than the 81920-byte read buffer, so the compare has to loop.
    private static byte[] Bytes(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 31 + i / 251)).ToArray();

    private async Task<(AbsolutePath First, AbsolutePath Second)> Pair(byte[] first, byte[]? second)
    {
        var folder = TemporaryFileManager.CreateFolder();
        var a = folder.Path.Combine("a.bin");
        var b = folder.Path.Combine("b.bin");
        await a.WriteAllBytesAsync(first);
        if (second is not null) await b.WriteAllBytesAsync(second);
        return (a, b);
    }

    [Fact]
    public async Task SameBytes_IdenticalFilesLongerThanOneBuffer_AreTheSame()
    {
        var (a, b) = await Pair(Bytes(200 * 1024), Bytes(200 * 1024));

        (await FileContents.SameBytes(a, b, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task SameBytes_FilesDifferingInTheLastByte_AreNot()
    {
        var changed = Bytes(200 * 1024);
        changed[^1]++;
        var (a, b) = await Pair(Bytes(200 * 1024), changed);

        (await FileContents.SameBytes(a, b, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task SameBytes_DifferentSizes_AreNot()
    {
        var (a, b) = await Pair(Bytes(1000), Bytes(1001));

        (await FileContents.SameBytes(a, b, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task SameBytes_AMissingFile_IsNot()
    {
        var (a, b) = await Pair(Bytes(1000), null);

        (await FileContents.SameBytes(a, b, CancellationToken.None)).Should().BeFalse();
        (await FileContents.SameBytes(b, a, CancellationToken.None)).Should().BeFalse();
    }
}
