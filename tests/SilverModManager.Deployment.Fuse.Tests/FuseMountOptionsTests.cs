using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Deployment.Vfs;

namespace SilverModManager.Deployment.Fuse.Tests;

public class FuseMountOptionsTests
{
    // Recovery finds a mount left behind by an earlier run through the filesystem type the kernel lists
    // (fuse.silvermod, see LaunchStatus.VfsFileSystemType and MountTable). Renaming these two to the
    // product name would make a stale mount from an earlier build invisible to it.
    [Fact]
    public void DefaultsPinTheFilesystemTypeRecoveryLooksFor()
    {
        var options = FuseMountOptions.Default(FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory));

        options.FsName.Should().Be("silvermod");
        options.Subtype.Should().Be("silvermod");
    }
}
