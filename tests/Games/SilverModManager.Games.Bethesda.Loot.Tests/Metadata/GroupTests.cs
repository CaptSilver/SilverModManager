using SilverModManager.Games.Bethesda.Loot.Metadata;

namespace SilverModManager.Games.Bethesda.Loot.Tests.Metadata;

// libloot's metadata/group.rs tests all go through YAML; these cover the model behaviour they rely on.
public class GroupTests
{
    [Fact]
    public void DefaultShouldUseTheDefaultNameAndNoAfterGroups()
    {
        var group = new Group();

        group.Name.Should().Be("default");
        group.Name.Should().Be(Group.DefaultName);
        group.Description.Should().BeNull();
        group.AfterGroups.Should().BeEmpty();
    }

    [Fact]
    public void ShouldSetAllGivenFields()
    {
        var group = new Group("group1") { Description = "desc", AfterGroups = ["group2", "group3"] };

        group.Name.Should().Be("group1");
        group.Description.Should().Be("desc");
        group.AfterGroups.Should().Equal("group2", "group3");
    }

    [Fact]
    public void ShouldCompareAfterGroupsByContent()
    {
        new Group("g") { AfterGroups = ["a"] }.Should().Be(new Group("g") { AfterGroups = ["a"] });
        new Group("g") { AfterGroups = ["a"] }.Should().NotBe(new Group("g") { AfterGroups = ["b"] });
        new Group("g").Should().Be(new Group("g") { AfterGroups = [] });
    }
}
