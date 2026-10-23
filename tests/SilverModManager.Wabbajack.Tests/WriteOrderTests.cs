using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Wabbajack.Format;

namespace SilverModManager.Wabbajack.Tests;

public class WriteOrderTests
{
    private static readonly RelativePath To = RelativePath.FromUnsanitizedInput("x");
    private static readonly Hash H = Hash.From(1);
    private static readonly ArchiveHashPath Source = new(H, []);

    [Fact]
    public void FileDirectivesComeBeforeBuiltArchivesAndMerges()
    {
        WriteOrder.Of(new PatchedFromArchive(To, H, 1, Source, H, "p")).Should().Be(0);
        WriteOrder.Of(new InlineFile(To, H, 1, "s")).Should().Be(0);
        WriteOrder.Of(new CreateBsa(To, H, 1, To, default, [])).Should().Be(1);
        WriteOrder.Of(new MergedPatch(To, H, 1, "p", [])).Should().Be(2);
    }

    [Fact]
    public void EveryOtherFileDirectiveWritesFirst()
    {
        WriteOrder.Of(new FromArchive(To, H, 1, Source)).Should().Be(0);
        WriteOrder.Of(new TransformedTexture(To, H, 1, Source, new ImageState(1, 1, 1, "BC7_UNORM"))).Should().Be(0);
        WriteOrder.Of(new RemappedInlineFile(To, H, 1, "s")).Should().Be(0);
        WriteOrder.Of(new PropertyFile(To, H, 1, "s", PropertyKind.Banner)).Should().Be(0);
        WriteOrder.Of(new ArchiveMeta(To, H, 1, "s")).Should().Be(0);
    }
}
