using NexusMods.Games.Bethesda.Loot.Metadata;

namespace NexusMods.Games.Bethesda.Loot.Tests.Metadata;

// libloot's metadata/tag.rs tests all go through YAML; these cover the model behaviour they rely on.
public class TagTests
{
    [Fact]
    public void NameAndSuggestion_ShouldReturnAnAdditionForAPlainName()
    {
        Tag.NameAndSuggestion("Relev").Should().Be(("Relev", TagSuggestion.Addition));
    }

    [Fact]
    public void NameAndSuggestion_ShouldStripALeadingHyphenAndReturnARemoval()
    {
        Tag.NameAndSuggestion("-Relev").Should().Be(("Relev", TagSuggestion.Removal));
    }

    [Fact]
    public void NameAndSuggestion_ShouldOnlyStripOneHyphen()
    {
        Tag.NameAndSuggestion("--Relev").Should().Be(("-Relev", TagSuggestion.Removal));
    }

    [Fact]
    public void IsAddition_ShouldFollowTheSuggestion()
    {
        new Tag("Relev").IsAddition.Should().BeTrue();
        new Tag("Relev", TagSuggestion.Removal).IsAddition.Should().BeFalse();
    }

    [Fact]
    public void ShouldCompareByNameSuggestionAndCondition()
    {
        new Tag("Relev").Should().Be(new Tag("Relev"));
        new Tag("Relev").Should().NotBe(new Tag("Relev", TagSuggestion.Removal));
        new Tag("Relev").Should().NotBe(new Tag("Relev") { Condition = "c" });
    }
}
