using FluentAssertions;

namespace SilverModManager.Deployment.Vfs.Tests;

public class VfsResultTests
{
    [Fact]
    public void Test_FailedResultReportsNotOkAndCarriesTheError()
    {
        var result = VfsResult<int>.Failure(Errno.ENOENT);
        result.Ok.Should().BeFalse();
        result.Error.Should().Be(Errno.ENOENT);
    }

    [Fact]
    public void Test_SuccessfulResultReportsOkAndCarriesTheValue()
    {
        var result = VfsResult<string>.Success("x");
        result.Ok.Should().BeTrue();
        result.Value.Should().Be("x");
    }

    [Fact]
    public void Test_FailedUnitResultReportsNotOkAndCarriesTheError()
    {
        var result = VfsResult.Failure(Errno.EIO);
        result.Ok.Should().BeFalse();
        result.Error.Should().Be(Errno.EIO);
        VfsResult.Success.Ok.Should().BeTrue();
    }
}
