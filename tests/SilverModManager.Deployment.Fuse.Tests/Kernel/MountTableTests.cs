using FluentAssertions;
using NexusMods.Deployment.Fuse.Kernel;

namespace NexusMods.Deployment.Fuse.Tests.Kernel;

public class MountTableTests
{
    private const string Line = "512 29 0:54 / /tmp/a\\040b/mount rw,nosuid,nodev,relatime shared:300 - fuse.silvermod silvermod rw,user_id=1000,group_id=1000,default_permissions";

    [Fact]
    public void MatchesTheMountPointAndTypeAfterTheSeparator() =>
        MountTable.Matches(Line, "/tmp/a b/mount", "fuse.silvermod").Should().BeTrue();

    [Fact]
    public void ADifferentTypeOnTheSamePathDoesNotMatch() =>
        MountTable.Matches(Line, "/tmp/a b/mount", "fuse.other").Should().BeFalse();

    [Fact]
    public void ADifferentPathDoesNotMatch() =>
        MountTable.Matches(Line, "/tmp/a b/other", "fuse.silvermod").Should().BeFalse();

    [Fact]
    public void AMalformedLineDoesNotMatch() =>
        MountTable.Matches("512 29 0:54", "/tmp", "fuse.silvermod").Should().BeFalse();
}
