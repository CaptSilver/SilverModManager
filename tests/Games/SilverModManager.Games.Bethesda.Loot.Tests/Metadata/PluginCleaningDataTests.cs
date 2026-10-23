using SilverModManager.Games.Bethesda.Loot.Metadata;

namespace SilverModManager.Games.Bethesda.Loot.Tests.Metadata;

// Test names follow libloot's metadata/plugin_cleaning_data.rs; its YAML and emitter tests are not ported here.
public class PluginCleaningDataTests
{
    [Fact]
    public void ShouldDefaultCountsToZeroAndLeaveOptionalFieldsEmpty()
    {
        var data = new PluginCleaningData(0x1234_5678, "cleaner");

        data.Crc.Should().Be(0x1234_5678u);
        data.CleaningUtility.Should().Be("cleaner");
        data.ItmCount.Should().Be(0u);
        data.DeletedReferenceCount.Should().Be(0u);
        data.DeletedNavmeshCount.Should().Be(0u);
        data.Detail.Should().BeEmpty();
        data.Condition.Should().BeNull();
    }

    [Fact]
    public void ShouldSetAllGivenFields()
    {
        var data = new PluginCleaningData(0x12345678, "cleaner")
        {
            ItmCount = 2,
            DeletedReferenceCount = 3,
            DeletedNavmeshCount = 4,
            Detail = [new MessageContent("details")],
            Condition = "file(\"Foo.esp\")",
        };

        data.ItmCount.Should().Be(2u);
        data.DeletedReferenceCount.Should().Be(3u);
        data.DeletedNavmeshCount.Should().Be(4u);
        data.Detail.Should().Equal(new MessageContent("details"));
        data.Condition.Should().Be("file(\"Foo.esp\")");
    }

    [Fact]
    public void ShouldNotErrorIfOneDetailIsGivenAndItIsNotEnglish()
    {
        var data = new PluginCleaningData(1, "u") { Detail = [new MessageContent("content1") { Language = "fr" }] };

        data.Detail.Should().ContainSingle();
    }

    [Fact]
    public void ShouldErrorIfMultipleDetailsAreGivenAndNoneAreEnglish()
    {
        var act = () => new PluginCleaningData(1, "u")
        {
            Detail = [new MessageContent("a") { Language = "de" }, new MessageContent("b") { Language = "fr" }],
        };

        act.Should().Throw<MultilingualMessageContentException>();
    }

    [Fact]
    public void ShouldCompareByAllFields()
    {
        new PluginCleaningData(1, "u").Should().Be(new PluginCleaningData(1, "u"));
        new PluginCleaningData(1, "u").Should().NotBe(new PluginCleaningData(2, "u"));
        new PluginCleaningData(1, "u") { ItmCount = 1 }.Should().NotBe(new PluginCleaningData(1, "u"));
    }
}
