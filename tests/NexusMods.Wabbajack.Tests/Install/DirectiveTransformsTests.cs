using System.Text.Json;
using FluentAssertions;
using NexusMods.Wabbajack.Format;
using NexusMods.Wabbajack.Install;
using NexusMods.Wabbajack.Transforms;

namespace NexusMods.Wabbajack.Tests.Install;

public class DirectiveTransformsTests
{
    private readonly IDirectiveTransforms _transforms = new DirectiveTransforms();

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

    [Fact]
    public async Task BuildingAnArchiveSaysThisBuildCannotBuildArchivesYet()
    {
        var failure = await Record.ExceptionAsync(() => _transforms.BuildBsa(JsonDocument.Parse("{}").RootElement, [], _ => Task.FromResult(Stream.Null), Stream.Null, CancellationToken.None));

        failure.Should().BeOfType<TransformUnavailableException>().Which.Message.Should().Be("this build cannot build archives yet");
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
