using System.Text.Json;
using FluentAssertions;
using NexusMods.Wabbajack.Format;
using NexusMods.Wabbajack.Install;

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
    public async Task RecompressingSaysThisBuildCannotRecompressTexturesYet()
    {
        var failure = await Record.ExceptionAsync(() => _transforms.Recompress(Stream.Null, new ImageState(1, 1, 1, "BC7_UNORM"), Stream.Null, CancellationToken.None));

        failure.Should().BeOfType<TransformUnavailableException>().Which.Message.Should().Be("this build cannot recompress textures yet");
    }

    [Fact]
    public async Task BuildingAnArchiveSaysThisBuildCannotBuildArchivesYet()
    {
        var failure = await Record.ExceptionAsync(() => _transforms.BuildBsa(JsonDocument.Parse("{}").RootElement, [], _ => Task.FromResult(Stream.Null), Stream.Null, CancellationToken.None));

        failure.Should().BeOfType<TransformUnavailableException>().Which.Message.Should().Be("this build cannot build archives yet");
    }
}
