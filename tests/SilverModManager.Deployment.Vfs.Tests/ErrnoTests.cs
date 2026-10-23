using FluentAssertions;

namespace NexusMods.Deployment.Vfs.Tests;

public class ErrnoTests
{
    [Theory]
    [InlineData(Errno.EPERM, 1)]
    [InlineData(Errno.ENOENT, 2)]
    [InlineData(Errno.EIO, 5)]
    [InlineData(Errno.EBADF, 9)]
    [InlineData(Errno.EACCES, 13)]
    [InlineData(Errno.EEXIST, 17)]
    [InlineData(Errno.EXDEV, 18)]
    [InlineData(Errno.ENOTDIR, 20)]
    [InlineData(Errno.EISDIR, 21)]
    [InlineData(Errno.EINVAL, 22)]
    [InlineData(Errno.ENOSPC, 28)]
    [InlineData(Errno.EROFS, 30)]
    [InlineData(Errno.ENOSYS, 38)]
    [InlineData(Errno.ENOTEMPTY, 39)]
    public void NumbersAreTheLinuxOnes(Errno errno, int number) => ((int)errno).Should().Be(number);
}
