using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Install;

namespace SilverModManager.Wabbajack.Tests.Install;

public class WorkEstimateTests
{
    private static readonly FileIdentity SomeHash = FileIdentity.XxHash3(Hash.From(1));

    private static TransformedTexture Texture(int side)
        => new(RelativePath.FromUnsanitizedInput("t.dds"), SomeHash, 0, new ArchiveHashPath(SomeHash, []), new ImageState(side, side, 1, "BC7_UNORM"));

    private static ResolvedMember Member(long size)
        => new(SomeHash, null, Size.FromLong(size), () => Task.FromResult<Stream>(Stream.Null));

    [Fact]
    public void AnArchiveReservesAtLeastWhatItsLargestTextureInputReservesOnItsOwn()
    {
        var texture = Texture(4096);
        var member = Member(256L * 1024 * 1024);

        var alone = WorkEstimate.Of(texture, member);
        var inArchive = WorkEstimate.OfArchive([(texture, member)]);

        inArchive.Should().BeGreaterThanOrEqualTo(alone, "building the archive decodes that texture just as a top-level one does");
    }

    [Fact]
    public void AnArchiveWithoutAResolvedInputMemberCountsTheTexturePixelsAlone()
    {
        var texture = Texture(4096);

        WorkEstimate.OfArchive([(texture, (ResolvedMember?)null)]).Should().Be(WorkEstimate.Of(texture, null) + 64L * 1024 * 1024);
    }

    [Fact]
    public void AnArchiveWithNoInputsCostsTheBuilderAlone()
        => WorkEstimate.OfArchive([]).Should().Be(64L * 1024 * 1024);
}
