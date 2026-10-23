using System.Text.Json;
using FluentAssertions;
using NexusMods.Networking.Thunderstore.DTOs;
using NexusMods.Networking.Thunderstore.Tests.Fixtures;
using NexusMods.Sdk.Thunderstore;
using Xunit;

namespace NexusMods.Networking.Thunderstore.Tests;

public class CatalogPackageLookupTests
{
    private static readonly ThunderstorePackageRef BepInExPack = new("BepInEx", "BepInExPack");
    private static readonly ThunderstorePackageRef RepoLib = new("Zehs", "REPOLib");
    private static readonly ThunderstorePackageRef RepoConfig = new("nickklmao", "REPOConfig");

    private static PackageListing Parse(string json) => JsonSerializer.Deserialize<PackageListing>(json, ThunderstoreApi.JsonOptions)!;

    private static ThunderstoreVersionNumber V(string text)
    {
        ThunderstoreVersionNumber.TryParse(text, out var version).Should().BeTrue();
        return version;
    }

    private static CatalogPackageLookup Lookup(params string[] listings)
        => new(new ThunderstoreCatalog("repo", listings.Select(Parse)));

    [Fact]
    public async Task Lookup_ReturnsNewestActiveVersion()
    {
        var lookup = Lookup(ThunderstoreJson.BepInExPack);

        var detail = await lookup.Lookup(BepInExPack, CancellationToken.None);

        detail!.Namespace.Should().Be("BepInEx");
        detail.Name.Should().Be("BepInExPack");
        detail.Latest.VersionNumber.Should().Be("5.4.2305");
    }

    [Fact]
    public async Task Lookup_UnknownPackage_ReturnsNull()
    {
        (await Lookup().Lookup(RepoLib, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task LookupVersion_ReturnsThatVersion()
    {
        var lookup = Lookup(ThunderstoreJson.BepInExPack);

        var version = await lookup.LookupVersion(new ThunderstoreVersionRef(BepInExPack, V("5.4.2100")), CancellationToken.None);

        version!.VersionNumber.Should().Be("5.4.2100");
    }

    [Fact]
    public async Task LookupVersion_UnknownVersion_ReturnsNull()
    {
        var lookup = Lookup(ThunderstoreJson.BepInExPack);

        (await lookup.LookupVersion(new ThunderstoreVersionRef(BepInExPack, V("9.9.9")), CancellationToken.None)).Should().BeNull();
    }

    // Serves the same listings the way the experimental endpoints do, so the API-backed lookup can be compared.
    private static RoutingHandler ApiServing(params string[] listings)
    {
        var handler = new RoutingHandler();
        foreach (var listing in listings.Select(Parse))
        {
            var @namespace = listing.Owner!;
            var url = $"https://thunderstore.io/api/experimental/package/{@namespace}/{listing.Name}/";
            var detail = new PackageDetail
            {
                Namespace = @namespace,
                Name = listing.Name,
                Latest = listing.Versions[0] with { Namespace = @namespace },
            };
            handler.Add(url, JsonSerializer.Serialize(detail, ThunderstoreApi.JsonOptions));
            foreach (var version in listing.Versions)
                handler.Add($"{url}{version.VersionNumber}/", JsonSerializer.Serialize(version with { Namespace = @namespace }, ThunderstoreApi.JsonOptions));
        }
        return handler;
    }

    [Fact]
    public async Task ResolvingAGraph_MatchesTheApiBackedLookup_WithoutAnyRequest()
    {
        string[] listings = [ThunderstoreJson.BepInExPack, ThunderstoreJson.RepoLib, ThunderstoreJson.RepoConfig, ThunderstoreJson.MenuLib];
        var root = new ThunderstoreVersionRef(RepoConfig, V("1.2.6"));
        var request = new ThunderstoreDependencyResolver.ResolveRequest(root, DependencyPolicy.Exact, new ThunderstorePackageRef("Unused", "Loader"), [], new Dictionary<ThunderstorePackageRef, ThunderstoreVersionNumber>());

        var handler = ApiServing(listings);
        var viaApi = await new ThunderstoreDependencyResolver(new ApiPackageLookup(new ThunderstoreApi(new HttpClient(handler))))
            .Resolve(request, CancellationToken.None);
        handler.Requests.Should().NotBeEmpty("the comparison is only meaningful if the API path was exercised");

        // CatalogPackageLookup has no HttpClient to call: it can only answer from the catalog.
        var viaCatalog = await new ThunderstoreDependencyResolver(Lookup(listings)).Resolve(request, CancellationToken.None);

        viaCatalog.Should().Equal(viaApi);
        viaCatalog.Should().Equal(
            new ThunderstoreVersionRef(BepInExPack, V("5.4.2100")),
            new ThunderstoreVersionRef(new ThunderstorePackageRef("nickklmao", "MenuLib"), V("2.5.2")),
            root);
    }

    [Fact]
    public async Task ResolvingAGraphAtLatest_MatchesTheApiBackedLookup()
    {
        string[] listings = [ThunderstoreJson.BepInExPack, ThunderstoreJson.RepoLib, ThunderstoreJson.RepoConfig, ThunderstoreJson.MenuLib];
        var root = new ThunderstoreVersionRef(RepoConfig, V("1.2.6"));
        var request = new ThunderstoreDependencyResolver.ResolveRequest(root, DependencyPolicy.Latest, new ThunderstorePackageRef("Unused", "Loader"), [], new Dictionary<ThunderstorePackageRef, ThunderstoreVersionNumber>());

        var viaApi = await new ThunderstoreDependencyResolver(new ApiPackageLookup(new ThunderstoreApi(new HttpClient(ApiServing(listings)))))
            .Resolve(request, CancellationToken.None);
        var viaCatalog = await new ThunderstoreDependencyResolver(Lookup(listings)).Resolve(request, CancellationToken.None);

        viaCatalog.Should().Equal(viaApi);
        viaCatalog.Should().Contain(new ThunderstoreVersionRef(BepInExPack, V("5.4.2305")), "Latest takes the newest loader, not the one the manifest names");
    }
}
