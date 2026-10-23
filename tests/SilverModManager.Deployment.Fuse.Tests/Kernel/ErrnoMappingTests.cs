using FluentAssertions;
using SilverModManager.Deployment.Fuse.Kernel;
using SilverModManager.Deployment.Vfs;

namespace SilverModManager.Deployment.Fuse.Tests.Kernel;

public class ErrnoMappingTests
{
    // EXDEV from VfsTree.Rename has to reach the caller as EXDEV: a mapping that "tidied" it into EIO
    // would make a cross-folder move fall back to copy-and-delete instead of failing as the kernel expects.
    public static IEnumerable<object[]> EveryErrno() => Enum.GetValues<Errno>().Select(e => new object[] { e });

    [Theory]
    [MemberData(nameof(EveryErrno))]
    public void EveryErrnoRepliesItsOwnNumber(Errno errno)
    {
        LibFuseLowLevelMount.ReplyCode(errno).Should().Be((int)errno);
    }

    [Fact]
    public void TheRenameFailureForAnotherFolderIsExdev()
    {
        LibFuseLowLevelMount.ReplyCode(Errno.EXDEV).Should().Be(18);
    }
}
