using FluentAssertions;
using NexusMods.Games.Unity.BepInEx;
using Xunit;

namespace NexusMods.Games.Unity.Tests;

public class BepInExPackageFolderTests
{
    [Theory]
    [InlineData("Zehs-REPOLib-4.2.0.zip", "Zehs-REPOLib")]
    [InlineData("BepInEx-BepInExPack_PEAK-5.4.75301.zip", "BepInEx-BepInExPack_PEAK")]
    [InlineData("Some-Legacy-Team-Mod-1.0.0.zip", "Some-Legacy-Team-Mod")]
    [InlineData("loose.7z", "loose")]
    public void TheVersionSuffixIsDroppedFromTheFileName(string archive, string expected) =>
        BepInExPackageFolder.For(archive).Should().Be(expected);

    [Fact]
    public void AManifestWithoutNamespaceGivesJustTheName() =>
        BepInExPackageFolder.For("REPOConfig.zip", null, "REPOConfig").Should().Be("REPOConfig");

    [Fact]
    public void AManifestNamespaceIsPrefixed() =>
        BepInExPackageFolder.For("REPOLib.zip", "Zehs", "REPOLib").Should().Be("Zehs-REPOLib");

    [Fact]
    public async Task TheManifestIsNotReadWhenTheNameCarriesAVersion()
    {
        var read = false;
        var folder = await BepInExPackageFolder.FromLibraryItemName("Zehs-REPOLib-4.2.0.zip", () => { read = true; return ValueTask.FromResult<BepInExPackageFolder.Manifest?>(null); });

        folder.Should().Be("Zehs-REPOLib");
        read.Should().BeFalse();
    }

    [Fact]
    public async Task TheManifestIsReadWhenTheNameHasNoVersion()
    {
        var folder = await BepInExPackageFolder.FromLibraryItemName("MyMod.zip", () => ValueTask.FromResult<BepInExPackageFolder.Manifest?>(new("Me", "MyMod")));

        folder.Should().Be("Me-MyMod");
    }

    [Theory]
    [InlineData("../../x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("/etc/cron.d")]
    [InlineData("C:\\Windows")]
    [InlineData("has space")]
    public void AManifestNameThatIsNotAThunderstoreNameFallsBackToTheArchiveName(string name) =>
        BepInExPackageFolder.For("Mod.zip", null, name).Should().Be("Mod");

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("..")]
    public void AManifestNamespaceThatIsNotAThunderstoreNamespaceFallsBackToTheArchiveName(string ns) =>
        BepInExPackageFolder.For("Mod.zip", ns, "Name").Should().Be("Mod");

    [Fact]
    public void ANamespaceAndNameThatAreValidMakeTheAuthorNameFolder() =>
        BepInExPackageFolder.For("Mod.zip", "Author", "Name").Should().Be("Author-Name");

    [Fact]
    public void ANamespaceMayContainDashes() =>
        BepInExPackageFolder.For("Mod.zip", "Some-Team", "Name_2").Should().Be("Some-Team-Name_2");

    [Theory]
    [InlineData("a\\b.zip")]
    [InlineData("C:evil.zip")]
    [InlineData("..zip")]
    [InlineData(".zip")]
    [InlineData("..-1.0.zip")]
    [InlineData("a\\b-1.0.zip")]
    public void AnArchiveNameThatCannotBeAFolderNameIsRefusedNamingTheArchive(string archive)
    {
        var act = () => BepInExPackageFolder.For(archive);

        act.Should().Throw<BepInExLayoutException>().Which.Message.Should().Contain(archive);
    }

    [Fact]
    public void AnEmptyArchiveNameIsRefused()
    {
        var act = () => BepInExPackageFolder.For("");

        act.Should().Throw<BepInExLayoutException>();
    }
}
