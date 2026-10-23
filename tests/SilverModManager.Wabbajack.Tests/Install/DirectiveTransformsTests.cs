using System.Text.Json;
using FluentAssertions;
using NexusMods.Games.Bethesda.Archives;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.Hashes;
using NexusMods.Wabbajack.Format;
using NexusMods.Wabbajack.Install;
using NexusMods.Wabbajack.Transforms;

namespace NexusMods.Wabbajack.Tests.Install;

public class DirectiveTransformsTests : IDisposable
{
    private readonly TemporaryFileManager _temp = new(
        FileSystem.Shared,
        FileSystem.Shared.GetKnownPath(KnownPath.EntryDirectory).Combine($"Wabbajack.Tests-{Guid.NewGuid()}"));

    private readonly IDirectiveTransforms _transforms;

    public DirectiveTransformsTests() => _transforms = new DirectiveTransforms(_temp);

    public void Dispose() => _temp.Dispose();

    private static RelativePath P(string path) => RelativePath.FromUnsanitizedInput(path);

    [Fact]
    public async Task PatchingAppliesAnOctodiffDelta()
    {
        byte[] basis = [1, 2, 3];
        byte[] result = [1, 2, 3, 4, 5];
        using var output = new MemoryStream();

        await _transforms.Patch(new MemoryStream(basis), new MemoryStream(ListBuilder.Delta(basis, result)), output, CancellationToken.None);

        output.ToArray().Should().Equal(result);
    }

    [Fact]
    public async Task RecompressingResizesAndReEncodesToTheRecordedTarget()
    {
        using var source = new MemoryStream();
        TestTextures.WriteBc1(source, 64);
        source.Position = 0;
        using var output = new MemoryStream();

        await _transforms.Recompress(source, new ImageState(32, 32, 3, "BC7_UNORM"), output, CancellationToken.None);

        var dds = DdsHeader.Read(output.ToArray());
        dds.Should().Be(new DdsHeader(32, 32, 3, Bc7: true));
    }

    [Fact]
    public async Task RecompressingToAFormatTheListNamesButWeCannotWriteThrowsUnsupportedTextureFormat()
    {
        using var source = new MemoryStream();
        TestTextures.WriteBc1(source, 8);
        source.Position = 0;

        var failure = await Record.ExceptionAsync(() => _transforms.Recompress(source, new ImageState(4, 4, 1, "BC6H_UF16"), Stream.Null, CancellationToken.None));

        failure.Should().BeOfType<UnsupportedTextureFormatException>().Which.Message.Should().Contain("BC6H_UF16");
    }

    [Fact]
    public async Task RecompressingToAnUnknownFormatNameThrowsUnsupportedTextureFormat()
    {
        var failure = await Record.ExceptionAsync(() => _transforms.Recompress(Stream.Null, new ImageState(4, 4, 1, "NOT_A_FORMAT"), Stream.Null, CancellationToken.None));

        failure.Should().BeOfType<UnsupportedTextureFormatException>().Which.Message.Should().Contain("NOT_A_FORMAT");
    }

    private static Func<RelativePath, Task<Stream>> Inputs(Dictionary<string, byte[]> files)
        => path => Task.FromResult<Stream>(new MemoryStream(files[path.ToString()]));

    [Fact]
    public async Task BuildingAnArchiveWritesEveryInputAsAReadableMember()
    {
        var files = new Dictionary<string, byte[]> { ["meshes/one.nif"] = [1, 2, 3], ["textures/two.dds"] = [4, 5, 6, 7] };
        var states = files.Keys.Select((path, i) => ListBuilder.Tes5File(i, path)).ToArray();
        using var output = new MemoryStream();

        await _transforms.BuildBsa(ListBuilder.Tes5State(), states, Inputs(files), output, CancellationToken.None);

        output.CanRead.Should().BeTrue("the transform leaves the stream it was given open");
        await using var reader = await BethesdaArchive.Open(new MemoryStream(output.ToArray()), CancellationToken.None);
        reader.Entries.Should().HaveCount(2);
        foreach (var entry in reader.Entries)
        {
            using var copy = new MemoryStream();
            await entry.CopyToAsync(copy, CancellationToken.None);
            copy.ToArray().Should().Equal(files[entry.Path.ToString()]);
        }
    }

    [Fact]
    public async Task BuildingAnArchiveIntoAStreamThatCannotBeReadBackFailsLoudly()
    {
        await using var writeOnly = new System.IO.Compression.GZipStream(Stream.Null, System.IO.Compression.CompressionMode.Compress);

        var failure = await Record.ExceptionAsync(() => _transforms.BuildBsa(ListBuilder.Tes5State(), [], _ => Task.FromResult(Stream.Null), writeOnly, CancellationToken.None));

        failure.Should().BeOfType<ArgumentException>();
    }

    [Fact]
    public async Task AnUnknownArchiveStateIsABethesdaArchiveException()
    {
        var state = JsonDocument.Parse("{\"$type\":\"NotAnArchive\"}").RootElement;

        var failure = await Record.ExceptionAsync(() => _transforms.BuildBsa(state, [], _ => Task.FromResult(Stream.Null), new MemoryStream(), CancellationToken.None));

        failure.Should().BeOfType<BethesdaArchiveException>().Which.Message.Should().Contain("NotAnArchive");
    }

    private async Task<byte[]> Archive(Dictionary<string, byte[]> files)
    {
        var states = files.Keys.Select((path, i) => ListBuilder.Tes5File(i, path)).ToArray();
        using var output = new MemoryStream();
        await _transforms.BuildBsa(ListBuilder.Tes5State(), states, Inputs(files), output, CancellationToken.None);
        return output.ToArray();
    }

    [Fact]
    public async Task ReadingABuiltArchiveBackFindsAMemberWhoseBytesAreNotTheOnesWritten()
    {
        var archive = await Archive(new() { ["meshes/one.nif"] = [1, 2, 3] });
        var written = new Dictionary<string, Hash> { ["meshes/one.nif"] = Xx64Hasher.Hash([9, 9, 9]) };

        var failure = await Record.ExceptionAsync(() => BuiltArchiveCheck.Verify(new MemoryStream(archive), written, CancellationToken.None));

        failure.Should().BeOfType<ArchiveMismatchException>().Which.Message.Should().Contain("meshes/one.nif");
    }

    [Fact]
    public async Task ReadingABuiltArchiveBackFindsAMemberNoInputWasWrittenFor()
    {
        var archive = await Archive(new() { ["meshes/one.nif"] = [1, 2, 3] });

        var failure = await Record.ExceptionAsync(() => BuiltArchiveCheck.Verify(new MemoryStream(archive), new Dictionary<string, Hash>(), CancellationToken.None));

        failure.Should().BeOfType<ArchiveMismatchException>().Which.Message.Should().Contain("meshes/one.nif");
    }

    [Fact]
    public async Task ReadingABuiltArchiveBackFindsAnInputTheArchiveDoesNotHold()
    {
        var archive = await Archive(new() { ["meshes/one.nif"] = [1, 2, 3] });
        var written = new Dictionary<string, Hash>
        {
            ["meshes/one.nif"] = Xx64Hasher.Hash([1, 2, 3]),
            ["meshes/two.nif"] = Xx64Hasher.Hash([4, 5, 6]),
        };

        var failure = await Record.ExceptionAsync(() => BuiltArchiveCheck.Verify(new MemoryStream(archive), written, CancellationToken.None));

        failure.Should().BeOfType<ArchiveMismatchException>().Which.Entry.Should().Be("meshes/two.nif");
    }

    [Fact]
    public async Task ReadingABuiltArchiveBackAcceptsMembersThatMatchRegardlessOfCase()
    {
        var archive = await Archive(new() { ["meshes/one.nif"] = [1, 2, 3] });
        var written = new Dictionary<string, Hash> { [BuiltArchiveCheck.Fold("Meshes\\ONE.nif")] = Xx64Hasher.Hash([1, 2, 3]) };

        await BuiltArchiveCheck.Verify(new MemoryStream(archive), written, CancellationToken.None);
    }
}

/// <summary>The size, mip count and codec of a DDS written with a DX10 header, read straight from the header bytes.</summary>
internal readonly record struct DdsHeader(int Width, int Height, int MipLevels, bool Bc7)
{
    private const int Bc7UnormDxgi = 98;

    public static DdsHeader Read(byte[] dds)
    {
        BitConverter.ToUInt32(dds, 0).Should().Be(0x20534444u, "the file starts with the DDS magic");
        // Header fields after the 4 byte magic: height at 12, width at 16, mip count at 28; DX10 extension at 128.
        var format = BitConverter.ToUInt32(dds, 128);
        return new DdsHeader(BitConverter.ToInt32(dds, 16), BitConverter.ToInt32(dds, 12), BitConverter.ToInt32(dds, 28), format == Bc7UnormDxgi);
    }
}
