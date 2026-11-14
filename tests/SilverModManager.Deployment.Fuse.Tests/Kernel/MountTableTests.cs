using FluentAssertions;
using SilverModManager.Deployment.Fuse.Kernel;

namespace SilverModManager.Deployment.Fuse.Tests.Kernel;

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

    [Fact]
    public void MatchesByParentRealPathAndLeaf()
    {
        // /home is a link to /var/home; the kernel lists the resolved spelling.
        const string line = "512 29 0:54 / /var/home/u/game/Data rw,nosuid shared:300 - fuse.silvermod silvermod rw,user_id=1000";

        MountTable.Matches(line, "/var/home/u/game", "Data", "fuse.silvermod").Should().BeTrue();
        MountTable.Matches(line, "/var/home/u/game", "data", "fuse.silvermod").Should().BeFalse();
        MountTable.Matches(line, "/var/home/u/game", "Data", "fuse.other").Should().BeFalse();
        MountTable.Matches(line, "/var/home/u", "Data", "fuse.silvermod").Should().BeFalse();
    }

    [Fact]
    public void ARootLevelMountPointMatches()
    {
        const string line = "512 29 0:54 / /mnt rw - fuse.silvermod silvermod rw";

        MountTable.Matches(line, "/", "mnt", "fuse.silvermod").Should().BeTrue();
    }

    [Fact]
    public void SplitForLookup_ResolvesTheParentAndKeepsTheLeafVerbatim()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mount-table-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "real"));
        Directory.CreateSymbolicLink(Path.Combine(root, "alias"), "real");
        try
        {
            var (parent, leaf) = MountTable.SplitForLookup(Path.Combine(root, "alias", "Data"));

            leaf.Should().Be("Data");
            parent.Should().EndWith("/real");
            new FileInfo(parent).LinkTarget.Should().BeNull();

            var (_, linkLeaf) = MountTable.SplitForLookup(Path.Combine(root, "alias"));
            linkLeaf.Should().Be("alias", "the leaf is the name the caller gave, link or not");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
