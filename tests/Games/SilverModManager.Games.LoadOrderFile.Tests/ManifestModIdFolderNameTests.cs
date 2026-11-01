using System.Text;
using FluentAssertions;

namespace SilverModManager.Games.LoadOrderFile.Tests;

public class ManifestModIdFolderNameTests
{
    private static string? NameOf(string manifest, bool bom = false)
        => new ManifestModIdFolderName().FolderName([.. bom ? Encoding.UTF8.GetPreamble() : [], .. Encoding.UTF8.GetBytes(manifest)]);

    [Fact]
    public void TheModIdIsTheName()
    {
        NameOf("<kcd_mod><info><name>Some Name</name><modid>some_id</modid></info></kcd_mod>").Should().Be("some_id");
    }

    [Fact]
    public void TheModNameIsUsedWhenThereIsNoModId()
    {
        NameOf("<kcd_mod><info><name>Some Name</name></info></kcd_mod>").Should().Be("Some Name");
    }

    [Fact]
    public void ABlankModIdFallsBackToTheName()
    {
        NameOf("<kcd_mod><info><name>Some Name</name><modid>  </modid></info></kcd_mod>").Should().Be("Some Name");
    }

    [Fact]
    public void SurroundingWhitespaceIsTrimmed()
    {
        NameOf("<kcd_mod><info><modid>\n  some_id  \n</modid></info></kcd_mod>").Should().Be("some_id");
    }

    [Fact]
    public void ATagNestedDeeperIsFoundToo()
    {
        NameOf("<kcd_mod><meta><info><modid>deep</modid></info></meta></kcd_mod>").Should().Be("deep");
    }

    [Fact]
    public void AByteOrderMarkDoesNotHideTheManifest()
    {
        NameOf("<?xml version=\"1.0\" encoding=\"utf-8\"?><kcd_mod><info><modid>some_id</modid></info></kcd_mod>", bom: true).Should().Be("some_id");
    }

    [Theory]
    [InlineData("not xml")]
    [InlineData("")]
    [InlineData("<kcd_mod><info/></kcd_mod>")]
    [InlineData("<kcd_mod><info><modid>a/b</modid></info></kcd_mod>")]
    [InlineData("<kcd_mod><info><modid>..</modid></info></kcd_mod>")]
    [InlineData("<kcd_mod><info><modid>a\\b</modid></info></kcd_mod>")]
    public void ABlankUnreadableOrUnusableManifestNamesNothing(string manifest)
    {
        NameOf(manifest).Should().BeNull();
    }
}
