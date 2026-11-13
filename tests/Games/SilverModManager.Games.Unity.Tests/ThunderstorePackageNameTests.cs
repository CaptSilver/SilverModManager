using FluentAssertions;

namespace SilverModManager.Games.Unity.Tests;

public class ThunderstorePackageNameTests
{
    [Theory]
    [InlineData("Zehs-REPOLib-4.2.0.zip", "Zehs-REPOLib")]
    [InlineData("BepInEx-BepInExPack_PEAK-5.4.75301.zip", "BepInEx-BepInExPack_PEAK")]
    [InlineData("Some-Legacy-Team-Mod-1.0.0.zip", "Some-Legacy-Team-Mod")]
    [InlineData("loose.7z", null)]
    public void TheVersionSuffixOfTheFileNameIsDropped(string archive, string? expected) =>
        ThunderstorePackageName.FromArchiveName(archive).Should().Be(expected);

    [Theory]
    [InlineData("Some-Mod-1.0.0", "Some-Mod")]
    [InlineData("Some-Mod", "Some-Mod")]
    [InlineData("Some-Mod-", "Some-Mod-")]
    [InlineData("-1.0", "-1.0")]
    public void StripVersionCutsOnlyAFinalDigitsAndDotsPart(string stem, string expected) =>
        ThunderstorePackageName.StripVersion(stem).Should().Be(expected);

    [Theory]
    [InlineData("REPO_Lib", true)]
    [InlineData("a b", false)]
    [InlineData("../x", false)]
    [InlineData("", false)]
    public void ANameIsLettersDigitsAndUnderscores(string value, bool expected) =>
        ThunderstorePackageName.IsName(value).Should().Be(expected);

    [Theory]
    [InlineData("Some-Team", true)]
    [InlineData("Some_Team", true)]
    [InlineData("Some Team", false)]
    [InlineData("a/b", false)]
    public void ANamespaceMayAlsoHoldDashes(string value, bool expected) =>
        ThunderstorePackageName.IsNamespace(value).Should().Be(expected);
}
