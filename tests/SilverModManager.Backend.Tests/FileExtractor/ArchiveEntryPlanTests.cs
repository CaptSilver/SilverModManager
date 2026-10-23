using FluentAssertions;
using NexusMods.Backend.FileExtractor;

namespace NexusMods.Backend.Tests.FileExtractor;

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
}
