using FluentAssertions;

namespace SilverModManager.Sdk.Tests;

public class TestScratchTests
{
    private static readonly string Fallback = Path.Combine(Path.GetTempPath(), "binaries");

    [Fact]
    public void Resolve_UsesTheEnvironmentValueWhenSet()
    {
        var folder = Path.Combine(Path.GetTempPath(), "scratch-root");
        TestScratch.Resolve(folder, Fallback).Should().Be(folder);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_FallsBackWhenTheEnvironmentValueIsMissing(string? value)
    {
        TestScratch.Resolve(value, Fallback).Should().Be(Fallback);
    }

    [Fact]
    public void Resolve_DropsATrailingSeparator()
    {
        var folder = Path.Combine(Path.GetTempPath(), "scratch-root");
        TestScratch.Resolve(folder + Path.DirectorySeparatorChar, Fallback).Should().Be(folder);
    }

    [Fact]
    public void Resolve_RejectsARelativeEnvironmentValue()
    {
        // A relative root would land beside whatever the working directory happens to be.
        var act = () => TestScratch.Resolve("scratch", Fallback);
        act.Should().Throw<InvalidOperationException>().WithMessage("*SILVER_TEST_SCRATCH*");
    }

    [Fact]
    public void Root_MatchesTheEnvironmentOrTheBinariesFolder()
    {
        var expected = TestScratch.Resolve(Environment.GetEnvironmentVariable(TestScratch.RootVariable), AppContext.BaseDirectory);
        TestScratch.Root.Should().Be(expected);
    }

    [Fact]
    public void ReflinkRoot_MatchesItsOwnEnvironmentVariable()
    {
        var expected = TestScratch.Resolve(Environment.GetEnvironmentVariable(TestScratch.ReflinkRootVariable), AppContext.BaseDirectory, TestScratch.ReflinkRootVariable);
        TestScratch.ReflinkRoot.Should().Be(expected);
    }

    [Fact]
    public void Resolve_NamesTheVariableItWasGiven()
    {
        var act = () => TestScratch.Resolve("scratch", Fallback, TestScratch.ReflinkRootVariable);
        act.Should().Throw<InvalidOperationException>().WithMessage("*SILVER_TEST_SCRATCH_REFLINK*");
    }

    [Fact]
    public void Combine_PutsTheRelativePathUnderTheRoot()
    {
        TestScratch.Combine("a", "b").Should().Be(Path.Combine(TestScratch.Root, "a", "b"));
    }

    [Fact]
    public void HostTemp_UsesTheHostVariableWhenSet()
    {
        var folder = Path.Combine(Path.GetTempPath(), "host-temp");
        TestScratch.ResolveHostTemp(folder).Should().Be(folder);
        TestScratch.ResolveHostTemp(null).Should().Be(Path.TrimEndingDirectorySeparator(Path.GetTempPath()));
    }
}
