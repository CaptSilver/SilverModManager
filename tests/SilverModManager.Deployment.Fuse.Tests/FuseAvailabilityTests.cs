using FluentAssertions;
using NexusMods.Deployment.Fuse.Availability;

namespace NexusMods.Deployment.Fuse.Tests;

public class FuseAvailabilityTests
{
    public sealed record FakeProbe(
        bool Device = true,
        bool Openable = true,
        string? Fusermount = "/usr/bin/fusermount3",
        (bool Loaded, string? Soname, int? Version, string? Error)? Library = null) : IFuseEnvironmentProbe
    {
        public bool DeviceExists() => Device;
        public bool CanOpenDevice() => Openable;
        public string? FusermountPath() => Fusermount;
        public (bool loaded, string? soname, int? version, string? error) LoadLibrary() => Library ?? (true, "libfuse3.so.4", 318, null);
    }

    [Fact]
    public void AllChecksPassing_IsUsable()
    {
        var report = FuseAvailability.Evaluate(new FakeProbe());

        report.Usable.Should().BeTrue();
        report.Reason.Should().BeNull();
        report.Lines.Should().HaveCount(4);
    }

    [Fact]
    public void NewerThanVerifiedLibrary_IsUsableAndSaysSo()
    {
        var report = FuseAvailability.Evaluate(new FakeProbe(Library: (true, "libfuse3.so.4", 319, null)));

        report.Usable.Should().BeTrue();
        report.Lines.Should().Contain(line => line.Contains("319") && line.Contains("newer"));
    }

    public static TheoryData<string, FakeProbe, string> FailingChecks => new()
    {
        { "no device", new FakeProbe(Device: false), "/dev/fuse does not exist" },
        { "device not openable", new FakeProbe(Openable: false), "/dev/fuse cannot be opened" },
        { "no fusermount3", new FakeProbe(Fusermount: null), "fusermount3 is not on PATH" },
        { "library missing", new FakeProbe(Library: (false, null, null, "no such file")), "libfuse3 could not be loaded" },
        { "unknown abi", new FakeProbe(Library: (true, "libfuse3.so.3", 318, null)), "libfuse3.so.3" },
        { "too old", new FakeProbe(Library: (true, "libfuse3.so.4", 317, null)), "317" },
    };

    [Theory]
    [MemberData(nameof(FailingChecks))]
    public void SingleFailingCheck_IsNotUsableAndNamesTheReason(string name, FakeProbe probe, string expected)
    {
        var report = FuseAvailability.Evaluate(probe);

        report.Usable.Should().BeFalse(name);
        report.Reason.Should().Contain(expected);
        report.Lines.Should().Contain(line => line.Contains(expected));
    }

    [Fact]
    public void MultipleFailures_ReportTheFirstInChainOrder()
    {
        var report = FuseAvailability.Evaluate(new FakeProbe(Device: false, Fusermount: null));

        report.Reason.Should().Contain("/dev/fuse does not exist");
        report.Lines.Should().Contain(line => line.Contains("fusermount3 is not on PATH"));
    }
}
