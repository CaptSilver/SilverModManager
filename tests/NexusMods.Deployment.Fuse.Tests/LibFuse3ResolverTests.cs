using FluentAssertions;
using NexusMods.Deployment.Fuse.Native;

namespace NexusMods.Deployment.Fuse.Tests;

public class LibFuse3ResolverTests(ITestOutputHelper output)
{
    [Fact]
    public void ProbeOrder_IsNewestSonameFirst()
    {
        LibFuse3Resolver.ProbeOrder.Should().Equal("libfuse3.so.4", "libfuse3.so.3");
    }

    [Theory]
    [InlineData("libfuse3.so.4", 318, false)]
    [InlineData("libfuse3.so.4", 319, true)]
    [InlineData("libfuse3.so.4", 399, true)]
    public void Check_AcceptsSupportedSonameWithinTheMajor(string soname, int version, bool newerThanVerified)
    {
        var result = FuseAbi.Check(soname, version);

        result.Ok.Should().BeTrue(result.Reason);
        result.NewerThanVerified.Should().Be(newerThanVerified);
    }

    [Theory]
    [InlineData("libfuse3.so.4", 317)]
    [InlineData("libfuse3.so.4", 299)]
    [InlineData("libfuse3.so.4", 400)]
    [InlineData("libfuse3.so.3", 318)]
    [InlineData("libfuse3.so.2", 318)]
    public void Check_RefusesAndNamesSonameAndVersion(string soname, int version)
    {
        var result = FuseAbi.Check(soname, version);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Contain(soname).And.Contain(version.ToString());
    }

    [Fact]
    public void LoadsInstalledLibrary()
    {
        RequiresFuse.SkipUnlessLibraryInstalled();

        // The version is whatever the package manager ships, so assert the pair is acceptable, not a number.
        var version = LibFuse3.fuse_version();
        var soname = LibFuse3Resolver.LoadedSoname;
        output.WriteLine($"{soname} fuse_version()={version} pkgversion={LibFuse3.PackageVersion()}");

        soname.Should().Be("libfuse3.so.4");
        FuseAbi.Check(soname!, version).Ok.Should().BeTrue();
    }
}
