using FluentAssertions;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Sdk.Native;

namespace SilverModManager.Games.FromSoft.Tests.Build;

public class PeFileVersionOnFakePeTests
{
    [Fact]
    public void AMinimalPeCarryingFileVersion2_7_1_0ReadsAs2_7_1_0()
    {
        using var stream = new MemoryStream(FakePe.WithVersion(new Version(2, 7, 1, 0)));

        PeFileVersion.Read(stream).Should().Be("2.7.1.0");
    }

    [Fact]
    public void TheFileVersionIsTheFixedFileInfoNotTheStringTable()
    {
        using var stream = new MemoryStream(FakePe.WithVersion(new Version(2, 7, 1, 0), productVersionString: "9.9.9.9"));

        PeFileVersion.Read(stream).Should().Be("2.7.1.0");
    }
}
