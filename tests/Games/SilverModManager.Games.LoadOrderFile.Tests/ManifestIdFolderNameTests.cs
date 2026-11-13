using System.Text;
using FluentAssertions;
using SilverModManager.Games.LoadOrderFile.Manifests;

namespace SilverModManager.Games.LoadOrderFile.Tests;

public class ManifestIdFolderNameTests
{
    private static readonly ManifestIdFolderName RimWorld = new(new AboutXmlManifestReader(), "RimWorld", "packageId");

    private static readonly ManifestIdFolderName Starsector = new(new ModInfoJsonManifestReader(), "Starsector", "id");

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void TheRimWorldNameIsThePackageIdLowerCased()
    {
        RimWorld.FolderName(Bytes("<ModMetaData><packageId>Brrainz.Harmony</packageId></ModMetaData>")).Should().Be("brrainz.harmony");
    }

    [Fact]
    public void TheStarsectorNameIsTheIdVerbatim()
    {
        Starsector.FolderName(Bytes("""{"id":"LazyLib"}""")).Should().Be("LazyLib");
    }

    [Theory]
    [InlineData("<ModMetaData/>")]
    [InlineData("not xml")]
    [InlineData("<ModMetaData><packageId>a/b</packageId></ModMetaData>")]
    [InlineData("<ModMetaData><packageId>..</packageId></ModMetaData>")]
    public void ANameThatIsNoFolderNameIsNull(string manifest)
    {
        RimWorld.FolderName(Bytes(manifest)).Should().BeNull();
    }

    [Fact]
    public void TheRefusalNamesTheFileTheFieldAndTheGame()
    {
        RimWorld.RefusalReason.Should().Be("The archive's About.xml has no packageId, so RimWorld could not list the mod");
        Starsector.RefusalReason.Should().Be("The archive's mod_info.json has no id, so Starsector could not list the mod");
    }

    [Fact]
    public void TheNameIsRequiredOnlyWhenAskedFor()
    {
        RimWorld.RequireName.Should().BeFalse();
        new ManifestIdFolderName(new AboutXmlManifestReader(), "RimWorld", "packageId") { RequireName = true }.RequireName.Should().BeTrue();
    }
}
