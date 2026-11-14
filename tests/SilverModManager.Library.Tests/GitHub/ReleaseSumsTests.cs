using FluentAssertions;
using SilverModManager.Library.GitHub;
using Xunit;

namespace SilverModManager.Library.Tests.GitHub;

public class ReleaseSumsTests
{
    private const string A = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private const string B = "60303ae22b998861bce3b28f33eec1be758a213c86c93c076dbe9f558c11c752";

    [Fact]
    public void ACoreutilsListGivesOneDigestPerFileName()
    {
        var sums = ReleaseSums.Parse("SHA256SUMS", $"{A}  tool-linux.tar.gz\n{B} *tool-win.zip\n");

        sums.For("tool-linux.tar.gz")!.Value.ToString().Should().Be(A);
        sums.For("tool-win.zip")!.Value.ToString().Should().Be(B, "a leading star only marks binary mode");
        sums.For("other.zip").Should().BeNull();
    }

    [Fact]
    public void NamesAreMatchedWithoutRegardToCase()
    {
        ReleaseSums.Parse("SHA256SUMS", $"{A.ToUpperInvariant()}  Tool.7z").For("tool.7z")!.Value.ToString().Should().Be(A);
    }

    [Fact]
    public void ABsdStyleListIsReadToo()
    {
        ReleaseSums.Parse("checksums.txt", $"SHA256 (tool.7z) = {A}").For("tool.7z")!.Value.ToString().Should().Be(A);
    }

    [Fact]
    public void ABareDigestInAnAssetSha256FileBelongsToTheAssetItIsNamedAfter()
    {
        ReleaseSums.Parse("tool.7z.sha256", A + "\n").For("tool.7z")!.Value.ToString().Should().Be(A);
    }

    [Fact]
    public void LinesThatAreNotDigestsAreSkipped()
    {
        var sums = ReleaseSums.Parse("SHA256SUMS", $"# built by CI\nnot a digest  tool.7z\n{A[..60]}  short.7z\n{B}  tool.7z\n");

        sums.For("tool.7z")!.Value.ToString().Should().Be(B);
        sums.For("short.7z").Should().BeNull();
    }

    [Theory]
    [InlineData("SHA256SUMS", true)]
    [InlineData("tool_SHA256SUMS.txt", true)]
    [InlineData("tool.7z.sha256", true)]
    [InlineData("tool.7z.sha256sum", true)]
    [InlineData("tool.7z", false)]
    [InlineData("README.md", false)]
    public void TheChecksumAssetIsRecognisedByName(string name, bool isSidecar)
    {
        ReleaseSums.IsSidecarName(name).Should().Be(isSidecar);
    }
}
