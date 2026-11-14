using FluentAssertions;

namespace SilverModManager.Networking.ModIo.Tests;

public class ModIoFileTests
{
    private static ModIoFile File(int virusStatus, int virusPositive)
        => new(1, 2, 0, 10, null, "mod.zip", null, null, virusStatus, virusPositive);

    [Fact]
    public void AFileWhoseScanFinishedWithNothingFoundIsClean()
    {
        var file = File(virusStatus: 1, virusPositive: 0);

        file.IsClean.Should().BeTrue();
        file.ScanProblem.Should().BeNull();
    }

    [Theory]
    [InlineData(0, "has not scanned")]
    [InlineData(2, "still scanning")]
    [InlineData(3, "too large")]
    [InlineData(4, "could not find")]
    [InlineData(5, "failed")]
    [InlineData(9, "status 9")]
    public void AFileWithoutAFinishedScanIsNotClean(int virusStatus, string reason)
    {
        // Nothing found by a scan that never ran is not a clean result.
        var file = File(virusStatus, virusPositive: 0);

        file.IsClean.Should().BeFalse();
        file.ScanProblem.Should().Contain(reason);
    }

    [Theory]
    [InlineData(1, "malicious")]
    [InlineData(2, "potentially harmful")]
    public void AFileTheScanFlaggedIsNotClean(int virusPositive, string reason)
    {
        var file = File(virusStatus: 1, virusPositive);

        file.IsClean.Should().BeFalse();
        file.ScanProblem.Should().Contain(reason);
    }
}
