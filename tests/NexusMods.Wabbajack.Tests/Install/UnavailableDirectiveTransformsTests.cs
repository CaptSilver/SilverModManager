using System.Text.Json;
using FluentAssertions;
using NexusMods.Wabbajack.Format;
using NexusMods.Wabbajack.Install;

namespace NexusMods.Wabbajack.Tests.Install;

public class UnavailableDirectiveTransformsTests
{
    private readonly IDirectiveTransforms _transforms = new UnavailableDirectiveTransforms();

    [Fact]
    public async Task PatchingSaysThisBuildCannotPatchYet()
    {
        var failure = await Record.ExceptionAsync(() => _transforms.Patch(Stream.Null, Stream.Null, Stream.Null, CancellationToken.None));

        failure.Should().BeOfType<TransformUnavailableException>().Which.Message.Should().Be("this build cannot patch files yet");
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
