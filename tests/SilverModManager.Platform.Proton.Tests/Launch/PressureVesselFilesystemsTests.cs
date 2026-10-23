using FluentAssertions;
using SilverModManager.Platform.Proton.Launch;

namespace SilverModManager.Platform.Proton.Tests.Launch;

public class PressureVesselFilesystemsTests
{
    private const string Variable = "PRESSURE_VESSEL_FILESYSTEMS_RW";

    [Fact]
    public void RootsAreAddedWhenTheVariableIsUnset()
    {
        var env = new Dictionary<string, string>();

        PressureVesselFilesystems.Merge(env, ["/a", "/b"]);

        env[Variable].Should().Be("/a:/b");
    }

    [Fact]
    public void ExistingEntriesStayFirstAndDuplicatesAreDropped()
    {
        var env = new Dictionary<string, string> { [Variable] = "/a:/b" };

        PressureVesselFilesystems.Merge(env, ["/b", "/c", "/c"]);

        env[Variable].Should().Be("/a:/b:/c");
    }

    [Fact]
    public void EmptyEntriesInTheExistingValueAreDropped()
    {
        var env = new Dictionary<string, string> { [Variable] = "/a::/b:" };

        PressureVesselFilesystems.Merge(env, ["/c"]);

        env[Variable].Should().Be("/a:/b:/c");
    }

    [Fact]
    public void NoRootsLeavesTheEnvironmentAlone()
    {
        var env = new Dictionary<string, string> { [Variable] = "/a::/b" };

        PressureVesselFilesystems.Merge(env, []);

        env[Variable].Should().Be("/a::/b");
    }

    [Fact]
    public void NoRootsDoesNotCreateTheVariable()
    {
        var env = new Dictionary<string, string>();

        PressureVesselFilesystems.Merge(env, []);

        env.Should().BeEmpty();
    }

    [Fact]
    public void MergingTheSameRootsTwiceChangesNothing()
    {
        var env = new Dictionary<string, string>();

        PressureVesselFilesystems.Merge(env, ["/store"]);
        PressureVesselFilesystems.Merge(env, ["/store"]);

        env[Variable].Should().Be("/store");
    }
}
