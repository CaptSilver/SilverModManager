using FluentAssertions;
using SilverModManager.Sdk.Thunderstore;

namespace SilverModManager.Sdk.Tests.Thunderstore;

public class ThunderstoreRefTests
{
    [Fact]
    public void DependencyString_WithUnambiguousName_SplitsIntoOneCandidate()
    {
        ThunderstoreDependencyString.TryParse("BepInEx-BepInExPack-5.4.2305", out var dependency).Should().BeTrue();

        dependency.UnsplitName.Should().Be("BepInEx-BepInExPack");
        dependency.VersionNumber.Should().Be(new ThunderstoreVersionNumber(5, 4, 2305));
        dependency.Candidates().Should().Equal(new ThunderstorePackageRef("BepInEx", "BepInExPack"));
        dependency.AsVersionRef(out var versionRef).Should().BeTrue();
        versionRef.Should().Be(new ThunderstoreVersionRef(new ThunderstorePackageRef("BepInEx", "BepInExPack"), new ThunderstoreVersionNumber(5, 4, 2305)));
    }

    [Fact]
    public void DependencyString_WithHyphenatedTeam_ListsCandidatesShortestNamespaceFirst()
    {
        ThunderstoreDependencyString.TryParse("Legacy-Team-Mod-1.0.0", out var dependency).Should().BeTrue();

        dependency.UnsplitName.Should().Be("Legacy-Team-Mod");
        dependency.Candidates().Should().Equal(
            new ThunderstorePackageRef("Legacy", "Team-Mod"),
            new ThunderstorePackageRef("Legacy-Team", "Mod")
        );
        dependency.AsVersionRef(out _).Should().BeFalse("the namespace/name split needs a lookup");
    }

    [Theory]
    [InlineData("NoVersion")]
    [InlineData("a-b")]
    [InlineData("a-b-x.y")]
    [InlineData("a-b-1.0")]
    [InlineData("a-b-1.0.x")]
    [InlineData("-b-1.0.0")]
    [InlineData("a--1.0.0")]
    [InlineData("")]
    public void DependencyString_WithoutThreePartVersion_Fails(string text)
    {
        ThunderstoreDependencyString.TryParse(text, out _).Should().BeFalse();
    }

    [Fact]
    public void DependencyString_RoundTripsThroughToString()
    {
        ThunderstoreDependencyString.TryParse("Legacy-Team-Mod-1.0.0", out var dependency).Should().BeTrue();
        dependency.ToString().Should().Be("Legacy-Team-Mod-1.0.0");
    }

    [Fact]
    public void VersionRef_ArchiveFileName_RoundTrips()
    {
        var versionRef = new ThunderstoreVersionRef(new ThunderstorePackageRef("Zehs", "REPOLib"), new ThunderstoreVersionNumber(4, 2, 0));

        versionRef.ArchiveFileName.Should().Be("Zehs-REPOLib-4.2.0.zip");
        ThunderstoreVersionRef.TryParseArchiveFileName(versionRef.ArchiveFileName, out var parsed).Should().BeTrue();
        parsed.Should().Be(versionRef);
    }

    [Fact]
    public void ArchiveFileName_WithAmbiguousName_ParsesToDependencyStringOnly()
    {
        ThunderstoreVersionRef.TryParseArchiveFileName("Legacy-Team-Mod-1.0.0.zip", out _).Should().BeFalse();

        ThunderstoreDependencyString.TryParseArchiveFileName("Legacy-Team-Mod-1.0.0.zip", out var dependency).Should().BeTrue();
        dependency.UnsplitName.Should().Be("Legacy-Team-Mod");
    }

    [Theory]
    [InlineData("Zehs-REPOLib-4.2.0")]
    [InlineData("Zehs-REPOLib-4.2.0.rar")]
    public void ArchiveFileName_WithoutZipExtension_Fails(string fileName)
    {
        ThunderstoreVersionRef.TryParseArchiveFileName(fileName, out _).Should().BeFalse();
        ThunderstoreDependencyString.TryParseArchiveFileName(fileName, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("REPOLib", true)]
    [InlineData("BepInExPack_PEAK", true)]
    [InlineData("Bad Name", false)]
    [InlineData("Bad-Name", false)]
    [InlineData("", false)]
    public void PackageRef_TryCreate_ChecksNameCharset(string name, bool valid)
    {
        ThunderstorePackageRef.TryCreate("Zehs", name, out var package).Should().Be(valid);
        if (valid) package.Name.Should().Be(name);
    }

    [Fact]
    public void PackageRef_TryCreate_RefusesEmptyNamespace()
    {
        ThunderstorePackageRef.TryCreate("", "REPOLib", out _).Should().BeFalse();
    }

    [Fact]
    public void PackageRef_ToString_IsNamespaceDashName()
    {
        new ThunderstorePackageRef("Zehs", "REPOLib").ToString().Should().Be("Zehs-REPOLib");
    }

    [Fact]
    public void VersionNumber_ComparesNumerically()
    {
        var ordered = new[] { "5.4.2305", "5.4.2100", "5.4.75", "1.0.10", "1.0.1" }
            .Select(text => ThunderstoreVersionNumber.TryParse(text, out var v) ? v : throw new Exception(text))
            .ToArray();

        for (var i = 0; i < ordered.Length - 1; i++)
        {
            ordered[i].Should().BeGreaterThan(ordered[i + 1]);
            (ordered[i] > ordered[i + 1]).Should().BeTrue();
            ordered[i].CompareTo(ordered[i + 1]).Should().BePositive();
        }
    }

    [Fact]
    public void VersionNumber_Equality()
    {
        ThunderstoreVersionNumber.TryParse("1.2.3", out var a).Should().BeTrue();
        ThunderstoreVersionNumber.TryParse("1.2.3", out var b).Should().BeTrue();
        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.ToString().Should().Be("1.2.3");
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.0.a")]
    [InlineData("1.0.0.0")]
    [InlineData("1.-1.0")]
    [InlineData("1..0")]
    [InlineData("")]
    public void VersionNumber_WithoutThreeNumericParts_Fails(string text)
    {
        ThunderstoreVersionNumber.TryParse(text, out _).Should().BeFalse();
    }
}
