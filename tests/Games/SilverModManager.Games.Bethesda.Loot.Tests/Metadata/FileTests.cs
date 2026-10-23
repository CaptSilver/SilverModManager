using SilverModManager.Games.Bethesda.Loot.Metadata;

namespace SilverModManager.Games.Bethesda.Loot.Tests.Metadata;

// Test names follow libloot's metadata/file.rs; its YAML tests are not ported here.
public class FileTests
{
    [Fact]
    public void FileEq_ShouldBeCaseInsensitiveOnName()
    {
        new File("name").Should().Be(new File("name"));
        new File("name").Should().Be(new File("NAME"));
        new File("name1").Should().NotBe(new File("name2"));
    }

    [Fact]
    public void FileEq_ShouldCompareTheOtherFields()
    {
        new File("name") { Condition = "a" }.Should().NotBe(new File("name") { Condition = "b" });
        new File("name") { Detail = [new MessageContent("x")] }.Should().Be(new File("name") { Detail = [new MessageContent("x")] });
        new File("name") { Detail = [new MessageContent("x")] }.Should().NotBe(new File("name"));
    }

    [Fact]
    public void FilenameEq_ShouldBeCaseInsensitiveOnName()
    {
        new Filename("name").Should().Be(new Filename("name"));
        new Filename("name").Should().Be(new Filename("NAME"));
        new Filename("name1").Should().NotBe(new Filename("name2"));
    }

    [Fact]
    public void Filename_ShouldHashAndOrderCaseInsensitively()
    {
        new Filename("Blank.ESM").GetHashCode().Should().Be(new Filename("blank.esm").GetHashCode());
        new Filename("a").CompareTo(new Filename("B")).Should().BeNegative();
        new Filename("A").CompareTo(new Filename("a")).Should().Be(0);
    }

    [Fact]
    public void Filename_DefaultShouldBeEmpty()
    {
        default(Filename).Value.Should().BeEmpty();
        default(Filename).Should().Be(new Filename(""));
    }

    [Fact]
    public void Detail_ShouldThrowIfMultipleContentsAreGivenAndNoneAreEnglish()
    {
        var act = () => new File("name")
        {
            Detail = [new MessageContent("a") { Language = "de" }, new MessageContent("b") { Language = "fr" }],
        };

        act.Should().Throw<MultilingualMessageContentException>();
    }

    [Fact]
    public void Detail_ShouldNotThrowIfOneDetailIsGivenAndItIsNotEnglish()
    {
        var file = new File("name") { Detail = [new MessageContent("a") { Language = "de" }] };

        file.Detail.Should().ContainSingle();
    }
}
