using FluentAssertions;
using Xunit;
using System.Net;
using System.Text;
using System.Text.Json;
using NexusMods.Networking.Thunderstore.DTOs;
using NexusMods.Networking.Thunderstore.Tests.Fixtures;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Thunderstore;

namespace NexusMods.Networking.Thunderstore.Tests;

public class ThunderstoreApiTests
{
    private static readonly ThunderstorePackageRef BepInExPack = new("BepInEx", "BepInExPack");
    private static readonly ThunderstoreVersionRef RepoLib420 = new(new ThunderstorePackageRef("Zehs", "REPOLib"), new ThunderstoreVersionNumber(4, 2, 0));

    private static ThunderstoreApi Api(RoutingHandler handler) => new(new HttpClient(handler));

    [Fact]
    public async Task FetchPackage_MapsLatestAndCommunityListings()
    {
        var handler = new RoutingHandler().Add("https://thunderstore.io/api/experimental/package/BepInEx/BepInExPack/", ThunderstoreJson.ExperimentalPackage);

        var package = await Api(handler).FetchPackage(BepInExPack, CancellationToken.None);

        handler.Requests.Should().Equal(new Uri("https://thunderstore.io/api/experimental/package/BepInEx/BepInExPack/"));
        package.Should().NotBeNull();
        package!.Namespace.Should().Be("BepInEx");
        package.Latest.VersionNumber.Should().Be("5.4.2305");
        package.Latest.Dependencies.Should().BeEmpty();
        package.Latest.DownloadUrl.Should().Be("https://thunderstore.io/package/download/BepInEx/BepInExPack/5.4.2305/");
        package.CommunityListings!.Select(l => l.Community).Should().Equal("riskofrain2", "repo");
        package.CommunityListings![1].Categories.Should().Equal("Libraries");
    }

    [Fact]
    public async Task FetchPackage_UnknownPackage_ReturnsNull()
    {
        var package = await Api(new RoutingHandler()).FetchPackage(BepInExPack, CancellationToken.None);
        package.Should().BeNull();
    }

    [Fact]
    public async Task FetchPackage_ServerError_ThrowsWithUrl()
    {
        var handler = new RoutingHandler().Add("https://thunderstore.io/api/experimental/package/BepInEx/BepInExPack/", "{}", HttpStatusCode.InternalServerError);

        var act = () => Api(handler).FetchPackage(BepInExPack, CancellationToken.None);

        (await act.Should().ThrowAsync<HttpRequestException>())
            .WithMessage("*https://thunderstore.io/api/experimental/package/BepInEx/BepInExPack/*");
    }

    [Fact]
    public async Task FetchVersion_MapsDependenciesAndToleratesMissingFileSize()
    {
        var handler = new RoutingHandler().Add("https://thunderstore.io/api/experimental/package/Zehs/REPOLib/4.2.0/", ThunderstoreJson.ExperimentalVersion);

        var version = await Api(handler).FetchVersion(RepoLib420, CancellationToken.None);

        version.Should().NotBeNull();
        version!.Dependencies.Should().Equal("BepInEx-BepInExPack-5.4.2305");
        version.DownloadUrl.Should().Be("https://thunderstore.io/package/download/Zehs/REPOLib/4.2.0/");
        version.IsActive.Should().BeTrue();
        version.FileSize.Should().BeNull();
        version.Namespace.Should().Be("Zehs");
    }

    [Fact]
    public async Task FetchVersion_UnknownVersion_ReturnsNull()
    {
        var version = await Api(new RoutingHandler()).FetchVersion(RepoLib420, CancellationToken.None);
        version.Should().BeNull();
    }

    [Fact]
    public void PackageVersion_ChunkShapedRecordWithoutNamespace_Deserialises()
    {
        var version = JsonSerializer.Deserialize<PackageVersion>(
            ThunderstoreJson.ChunkVersion("Zehs", "REPOLib", "4.2.0", ["BepInEx-BepInExPack-5.4.2305"]),
            ThunderstoreApi.JsonOptions);

        version!.Namespace.Should().BeNull();
        version.FileSize.Should().Be(4096);
        version.VersionNumber.Should().Be("4.2.0");
    }

    [Fact]
    public async Task FetchListingIndex_ReturnsFinalBlobAndChunks()
    {
        var body = Gzip.Compress("""["https://cdn.example/sha256/aa/chunk0","https://cdn.example/sha256/bb/chunk1"]""");
        var hex = Gzip.HexDigest(body);
        var final = new Uri($"https://cdn.example/sha256/{hex}/index");
        var handler = new RoutingHandler().Add("https://thunderstore.io/c/repo/api/v1/package-listing-index/", body, "application/octet-stream", final);

        var index = await Api(handler).FetchListingIndex("repo", CancellationToken.None);

        index.Index.Uri.Should().Be(final);
        index.Index.Sha256.Should().Be(Sha256Value.FromHex(hex));
        index.Chunks.Should().Equal(new Uri("https://cdn.example/sha256/aa/chunk0"), new Uri("https://cdn.example/sha256/bb/chunk1"));
    }

    [Fact]
    public async Task FetchListingIndex_DigestMismatch_Throws()
    {
        var body = Gzip.Compress("[]");
        var wrong = new string('0', 64);
        var handler = new RoutingHandler().Add("https://thunderstore.io/c/repo/api/v1/package-listing-index/", body, "application/octet-stream", new Uri($"https://cdn.example/sha256/{wrong}/index"));

        var act = () => Api(handler).FetchListingIndex("repo", CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<ThunderstoreBlobIntegrityException>();
        thrown.Which.Message.Should().Contain(wrong).And.Contain(Gzip.HexDigest(body));
    }

    [Fact]
    public async Task FetchListingIndex_NoDigestSegment_UsesComputedDigest()
    {
        var body = Gzip.Compress("""["https://cdn.example/chunk0"]""");
        var handler = new RoutingHandler().Add("https://thunderstore.io/c/repo/api/v1/package-listing-index/", body, "application/octet-stream");

        var index = await Api(handler).FetchListingIndex("repo", CancellationToken.None);

        index.Index.Uri.Should().Be(new Uri("https://thunderstore.io/c/repo/api/v1/package-listing-index/"));
        index.Index.Sha256.Should().Be(Sha256Value.FromHex(Gzip.HexDigest(body)));
        index.Chunks.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task FetchListingIndex_BlankCommunity_Throws(string community)
    {
        var act = () => Api(new RoutingHandler()).FetchListingIndex(community, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ReadChunk_YieldsPackagesInOrder()
    {
        var body = Gzip.Compress($"[{ThunderstoreJson.RepoLib},{ThunderstoreJson.RepoConfig}]");
        var handler = new RoutingHandler().Add("https://cdn.example/chunk0", body, "application/octet-stream");

        var names = new List<string>();
        await foreach (var package in Api(handler).ReadChunk(new Uri("https://cdn.example/chunk0"), CancellationToken.None))
        {
            names.Add(package.FullName);
            package.Versions.Should().HaveCount(1);
        }

        names.Should().Equal("Zehs-REPOLib", "nickklmao-REPOConfig");
    }

    [Fact]
    public async Task ReadChunk_MalformedTrailingRecord_ThrowsJsonExceptionNamingChunk()
    {
        var body = Gzip.Compress($"[{ThunderstoreJson.RepoLib},{{\"name\": ");
        var handler = new RoutingHandler().Add("https://cdn.example/chunk0", body, "application/octet-stream");
        var chunk = new Uri("https://cdn.example/chunk0");

        var seen = new List<string>();
        var act = async () =>
        {
            await foreach (var package in Api(handler).ReadChunk(chunk, CancellationToken.None)) seen.Add(package.FullName);
        };

        (await act.Should().ThrowAsync<JsonException>()).WithMessage("*https://cdn.example/chunk0*");
        seen.Should().Equal("Zehs-REPOLib");
    }

    [Fact]
    public async Task ReadChunk_DigestMismatch_ThrowsBeforeYielding()
    {
        var body = Gzip.Compress($"[{ThunderstoreJson.RepoLib}]");
        var chunk = new Uri($"https://cdn.example/sha256/{new string('0', 64)}/chunk0");
        var handler = new RoutingHandler().Add(chunk.ToString(), body, "application/octet-stream");

        var seen = 0;
        var act = async () =>
        {
            await foreach (var _ in Api(handler).ReadChunk(chunk, CancellationToken.None)) seen++;
        };

        await act.Should().ThrowAsync<ThunderstoreBlobIntegrityException>();
        seen.Should().Be(0);
    }

    [Fact]
    public void DownloadUri_IsTheDownloadEndpoint()
    {
        Api(new RoutingHandler()).DownloadUri(RepoLib420)
            .Should().Be(new Uri("https://thunderstore.io/package/download/Zehs/REPOLib/4.2.0/"));
    }

    [Fact]
    public void DigestFromUri_ReadsSha256Segment()
    {
        var hex = new string('a', 64);
        ChunkReader.DigestFromUri(new Uri($"https://cdn.example/x/sha256/{hex}/y")).Should().Be(Sha256Value.FromHex(hex));
        ChunkReader.DigestFromUri(new Uri("https://cdn.example/x/y")).Should().BeNull();
    }

    [Fact]
    [Trait("RequiresNetworking", "True")]
    public async Task Live_FetchPackage_BepInExPackIsListedInRepo()
    {
        var package = await new ThunderstoreApi(new HttpClient()).FetchPackage(BepInExPack, CancellationToken.None);
        package!.CommunityListings!.Select(l => l.Community).Should().Contain("repo");
    }
}
