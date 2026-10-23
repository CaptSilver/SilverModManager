using FluentAssertions;
using NexusMods.Platform.Proton.Launch;

namespace NexusMods.Platform.Proton.Tests.Launch;

public class SecretsFileOptionTests
{
    private static string? Find(params string[] args)
    {
        SecretsFileOption.TryFind(args, out var path, out var error).Should().BeTrue(error);
        return path?.GetFullPath();
    }

    private static string Fail(params string[] args)
    {
        SecretsFileOption.TryFind(args, out var path, out var error).Should().BeFalse();
        path.Should().BeNull();
        return error!;
    }

    [Fact]
    public void FindsTheOptionInBothForms()
    {
        Find("--secrets-file", "/a/s.env").Should().Be("/a/s.env");
        Find("--secrets-file=/a/s.env").Should().Be("/a/s.env");
        Find("other", "--secrets-file", "/a/s.env", "more").Should().Be("/a/s.env");
    }

    [Fact]
    public void IsAbsentWhenNotGiven()
    {
        Find().Should().BeNull();
        Find("--other", "x").Should().BeNull();
    }

    [Fact]
    public void LeavesWhatFollowsASeparatorAlone()
    {
        Find("--", "--secrets-file", "/a/s.env").Should().BeNull();
    }

    [Fact]
    public void ResolvesARelativePathAgainstTheWorkingDirectory()
    {
        Find("--secrets-file=s.env").Should().Be(Path.GetFullPath("s.env"));
    }

    [Fact]
    public void RejectsAMissingEmptyOrRepeatedValue()
    {
        Fail("--secrets-file").Should().Contain("needs a value");
        Fail("--secrets-file=").Should().Contain("needs a value");
        Fail("--secrets-file", " ").Should().Contain("needs a value");
        Fail("--secrets-file", "/a", "--secrets-file=/b").Should().Contain("more than once");
    }
}
