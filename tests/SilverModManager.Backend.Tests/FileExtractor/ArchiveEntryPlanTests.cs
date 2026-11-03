using FluentAssertions;
using SilverModManager.Backend.FileExtractor;

namespace SilverModManager.Backend.Tests.FileExtractor;

public class ArchiveEntryPlanTests
{
    private static ArchiveEntryPlan Plan(params string[] names) =>
        new(names.Select(n => new ArchiveEntryPlan.Entry(n, IsDirectory: n.EndsWith('/'))).ToArray());

    // 7z drops "." and empty segments itself, so these need neither a separate run nor a warning. Each
    // separate run decompresses the whole archive again, which a tarball of thousands of "./" entries
    // can't afford.
    [Fact]
    public void NamesThat7zPlacesTheSameWayAsTheRuleAreLeftToTheBulkRun()
    {
        var plan = Plan("./", "./a/", "./a/x.txt", "a//y.txt", "a/./z.txt");

        plan.IsPlain.Should().BeTrue();
        plan.Rewrites.IsEmpty.Should().BeTrue();
    }

    // 7z drops "/" and ".." at the front the same way, so these are reported but still left to 7z.
    [Fact]
    public void ARewriteThat7zAlreadyMatchesIsReportedButNotMoved()
    {
        var plan = Plan("../x.txt", "/y.txt", "../../z/");

        plan.IsPlain.Should().BeTrue();
        plan.Rewrites.IsEmpty.Should().BeFalse();
    }

    [Theory]
    [InlineData("a/../x.txt", "x.txt")]
    [InlineData("a\\x.txt", "a/x.txt")]
    [InlineData("C:/x.txt", "x.txt")]
    [InlineData("c:x.txt", "x.txt")]
    public void ANameThat7zPlacesElsewhereIsMovedToTheCleanedPath(string raw, string cleaned)
    {
        var plan = Plan(raw, "kept.txt");

        plan.Moved.Should().Equal((raw, cleaned));
        plan.Excluded.Should().BeEquivalentTo(raw);
    }

    [Fact]
    public void AFolderThat7zPlacesElsewhereIsCreatedDirectly()
    {
        var plan = Plan("a\\b/", "./c/");

        plan.Folders.Should().Equal("a/b");
        plan.Excluded.Should().BeEquivalentTo("a\\b/");
    }

    [Fact]
    public void AnEntryOverwrittenByADifferentNameIsLeftOut()
    {
        var plan = Plan("x.txt", "./x.txt");

        plan.Excluded.Should().BeEquivalentTo("x.txt");
        plan.Moved.Should().BeEmpty();
        plan.Rewrites.IsEmpty.Should().BeFalse();
    }

    private static ArchiveEntryPlan Plan(params ArchiveEntryPlan.Entry[] entries) => new(entries);

    private static ArchiveEntryPlan.Entry File(string name) => new(name, IsDirectory: false);
    private static ArchiveEntryPlan.Entry Symlink(string name, string? target = null) => new(name, IsDirectory: false, SymlinkTarget: target, IsSymlink: true);
    private static ArchiveEntryPlan.Entry HardLink(string name, string target) => new(name, IsDirectory: false, HardLinkTarget: target);

    [Fact]
    public void ASymlinkEntryIsExcludedFromTheBulkRunAndListedAsALink()
    {
        var plan = Plan(File("sub/ok.txt"), Symlink("../up/link", "sub/ok.txt"));

        plan.IsPlain.Should().BeFalse();
        plan.Excluded.Should().BeEquivalentTo("../up/link");
        plan.Links.Should().Equal(("../up/link", "up/link", "sub/ok.txt"));
        plan.Moved.Should().BeEmpty();
    }

    [Fact]
    public void ASymlinkDoesNotSupersedeAFileAtItsCleanedPath()
    {
        var plan = Plan(File("a.txt"), Symlink("./a.txt", "b.txt"));

        plan.Excluded.Should().BeEquivalentTo("./a.txt");
        plan.Links.Should().ContainSingle();
    }

    [Fact]
    public void AnEntryBelowASymlinkIsExtractedAloneBecauseExcludingTheLinkWouldExcludeIt()
    {
        var plan = Plan(Symlink("link", "/outside"), File("link/escaped.txt"));

        plan.Moved.Should().Equal(("link/escaped.txt", "link/escaped.txt"));
    }

    [Fact]
    public void AHardLinkWithoutAKeptTargetIsExcluded()
    {
        var plan = Plan(HardLink("stolen.txt", "/outside/secret.txt"), File("ok.txt"));

        plan.Excluded.Should().BeEquivalentTo("stolen.txt");
        plan.DroppedHardLinks.Should().ContainSingle(d => d.Name == "stolen.txt");
    }

    [Fact]
    public void AHardLinkToAKeptEntryIsLeftToTheBulkRun()
    {
        var plan = Plan(File("sub/ok.txt"), HardLink("hard.txt", "sub/ok.txt"));

        plan.IsPlain.Should().BeTrue();
        plan.DroppedHardLinks.Should().BeEmpty();
    }

    [Fact]
    public void AHardLinkToAMovedEntryIsDropped()
    {
        var plan = Plan(File("a/../x.txt"), HardLink("hard.txt", "a/../x.txt"));

        plan.Excluded.Should().Contain("hard.txt");
        plan.DroppedHardLinks.Should().ContainSingle(d => d.Name == "hard.txt");
    }

    [Fact]
    public void AHardLinkToALaterEntryIsDropped()
    {
        var plan = Plan(HardLink("hard.txt", "ok.txt"), File("ok.txt"));

        plan.DroppedHardLinks.Should().ContainSingle(d => d.Name == "hard.txt");
    }

    [Fact]
    public void AHardLinkWithARewrittenNameIsDropped()
    {
        var plan = Plan(File("ok.txt"), HardLink("a\\hard.txt", "ok.txt"));

        plan.DroppedHardLinks.Should().ContainSingle(d => d.Name == "a\\hard.txt");
    }
}
