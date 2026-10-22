using System.Text.Json;
using FluentAssertions;
using NexusMods.Networking.Thunderstore.DTOs;
using NexusMods.Networking.Thunderstore.Tests.Fixtures;
using NexusMods.Sdk.Thunderstore;
using Xunit;

namespace NexusMods.Networking.Thunderstore.Tests;

public class ThunderstoreCatalogTests
{
    private static PackageListing Parse(string json) => JsonSerializer.Deserialize<PackageListing>(json, ThunderstoreApi.JsonOptions)!;

    private static PackageListing Patch(PackageListing listing, bool deprecated = false, double rating = 0)
        => listing with { IsDeprecated = deprecated, RatingScore = rating };

    private static ThunderstoreCatalog Catalog(params PackageListing[] packages) => new("repo", packages);

    [Fact]
    public void Latest_SkipsInactiveVersions()
    {
        var listing = Parse(ThunderstoreJson.BepInExPack);
        var inactive = listing.Versions[0] with { IsActive = false };
        var catalog = Catalog(listing with { Versions = [inactive, listing.Versions[1]] });

        var latest = catalog.Latest(new ThunderstorePackageRef("BepInEx", "BepInExPack"));

        latest!.VersionNumber.Should().Be("5.4.2100");
    }

    [Fact]
    public void Latest_DeprecatedPackage_OnlyWhenAsked()
    {
        var catalog = Catalog(Patch(Parse(ThunderstoreJson.RepoLib), deprecated: true));
        var package = new ThunderstorePackageRef("Zehs", "REPOLib");

        catalog.Latest(package).Should().BeNull();
        catalog.Latest(package, includeDeprecated: true)!.VersionNumber.Should().Be("4.2.0");
    }

    [Fact]
    public void Latest_UnknownPackage_ReturnsNull()
    {
        Catalog().Latest(new ThunderstorePackageRef("Zehs", "REPOLib")).Should().BeNull();
    }

    [Fact]
    public void Search_MatchesNameCaseInsensitively()
    {
        var catalog = Catalog(Parse(ThunderstoreJson.RepoLib), Parse(ThunderstoreJson.RepoConfig));

        catalog.Search("repolib").Select(p => p.FullName).Should().Equal("Zehs-REPOLib");
    }

    [Fact]
    public void Search_MatchesOwner()
    {
        var catalog = Catalog(Parse(ThunderstoreJson.RepoLib), Parse(ThunderstoreJson.RepoConfig));

        catalog.Search("NICKKLMAO").Select(p => p.FullName).Should().Equal("nickklmao-REPOConfig");
    }

    [Fact]
    public void Search_OrdersByRatingAndExcludesDeprecatedByDefault()
    {
        var catalog = Catalog(
            Patch(Parse(ThunderstoreJson.RepoLib), rating: 5),
            Patch(Parse(ThunderstoreJson.RepoConfig), rating: 9),
            Patch(Parse(ThunderstoreJson.BepInExPack), deprecated: true, rating: 100));

        catalog.Search("e").Select(p => p.FullName).Should().Equal("nickklmao-REPOConfig", "Zehs-REPOLib");
        catalog.Search("e", includeDeprecated: true).Select(p => p.FullName).First().Should().Be("BepInEx-BepInExPack");
    }

    [Fact]
    public void Resolve_PicksTheSplitWhoseOwnerAndNameExist()
    {
        var legacy = Parse(ThunderstoreJson.Listing("Legacy-Team", "Mod", ThunderstoreJson.ChunkVersion("Legacy-Team", "Mod", "1.0.0", [])));
        var catalog = Catalog(legacy);
        ThunderstoreDependencyString.TryParse("Legacy-Team-Mod-1.0.0", out var dependency).Should().BeTrue();

        var resolved = catalog.Resolve(dependency);

        resolved.Package.Should().Be(new ThunderstorePackageRef("Legacy-Team", "Mod"));
        resolved.Version.Should().Be(new ThunderstoreVersionNumber(1, 0, 0));
    }

    [Fact]
    public void Resolve_BothSplitsExist_FailsNamingTheString()
    {
        var ambiguous = new ThunderstoreCatalog("repo", [
            Parse(ThunderstoreJson.Listing("Legacy", "Team-Mod", ThunderstoreJson.ChunkVersion("Legacy", "Team-Mod", "1.0.0", []))),
            Parse(ThunderstoreJson.Listing("Legacy-Team", "Mod", ThunderstoreJson.ChunkVersion("Legacy-Team", "Mod", "1.0.0", []))),
        ]);
        ThunderstoreDependencyString.TryParse("Legacy-Team-Mod-1.0.0", out var dependency).Should().BeTrue();

        var act = () => ambiguous.Resolve(dependency);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Legacy-Team-Mod-1.0.0*ambiguous*");
    }

    [Fact]
    public void Resolve_NoSplitExists_FailsNamingTheString()
    {
        ThunderstoreDependencyString.TryParse("Nobody-Nothing-1.0.0", out var dependency).Should().BeTrue();

        var act = () => Catalog().Resolve(dependency);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Nobody-Nothing-1.0.0*");
    }

    [Fact]
    public void VersionsAndCategories_ComeFromTheListing()
    {
        var catalog = Catalog(Parse(ThunderstoreJson.BepInExPack));
        var package = new ThunderstorePackageRef("BepInEx", "BepInExPack");

        catalog.Versions(package).Select(v => v.VersionNumber).Should().Equal("5.4.2305", "5.4.2100");
        catalog.Categories(package).Should().Equal("Mods");
        catalog.Versions(new ThunderstorePackageRef("No", "Body")).Should().BeEmpty();
    }
}
