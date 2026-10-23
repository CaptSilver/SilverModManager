using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Secrets;
using NexusMods.Networking.GitHub.DTOs;
using NexusMods.Sdk.Hashes;
using Xunit;

namespace NexusMods.Networking.GitHub.Tests;

public class ReleaseEndpointTests
{
    private const string DigestHex = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private sealed class RecordingHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static string AssetJson(string name, string? digest, int id = 1)
    {
        var digestJson = digest is null ? "null" : $"\"{digest}\"";
        return $$"""
        {
          "url": "https://api.github.com/repos/o/r/releases/assets/{{id}}",
          "id": {{id}},
          "name": "{{name}}",
          "content_type": "application/zip",
          "state": "uploaded",
          "size": 12,
          "download_count": 3,
          "created_at": "2025-01-01T00:00:00Z",
          "updated_at": "2025-01-01T00:00:00Z",
          "browser_download_url": "https://github.com/o/r/releases/download/v1.0.0/{{name}}",
          "digest": {{digestJson}}
        }
        """;
    }

    private static string ReleaseJson(params string[] assets) => $$"""
    {
      "url": "https://api.github.com/repos/o/r/releases/1",
      "assets_url": "https://api.github.com/repos/o/r/releases/1/assets",
      "html_url": "https://github.com/o/r/releases/tag/v1.0.0",
      "id": 1,
      "tag_name": "v1.0.0",
      "target_commitish": "main",
      "name": "One",
      "draft": false,
      "prerelease": false,
      "created_at": "2025-01-01T00:00:00Z",
      "published_at": "2025-01-01T00:00:00Z",
      "assets": [{{string.Join(",", assets)}}],
      "tarball_url": "https://api.github.com/repos/o/r/tarball/v1.0.0",
      "zipball_url": "https://api.github.com/repos/o/r/zipball/v1.0.0",
      "body": ""
    }
    """;

    private static GitHubApi CreateApi(RecordingHandler handler) => new(NullLogger<GitHubApi>.Instance, new HttpClient(handler), new InMemorySecretStore());

    [Fact]
    public async Task FetchReleaseByTagUsesTheRepositoryTagsEndpoint()
    {
        var handler = new RecordingHandler(ReleaseJson());
        var release = await CreateApi(handler).FetchReleaseByTag("o", "r", "v1.0.0");

        release.Should().NotBeNull();
        release!.TagName.Should().Be("v1.0.0");
        handler.Requests.Should().ContainSingle().Which.ToString().Should().Be("https://api.github.com/repos/o/r/releases/tags/v1.0.0");
    }

    [Fact]
    public async Task FetchMarkedLatestReleaseUsesTheRepositoryLatestEndpoint()
    {
        var handler = new RecordingHandler(ReleaseJson());
        var release = await CreateApi(handler).FetchMarkedLatestRelease("o", "r");

        release.Should().NotBeNull();
        handler.Requests.Should().ContainSingle().Which.ToString().Should().Be("https://api.github.com/repos/o/r/releases/latest");
    }

    [Fact]
    public async Task FetchLatestReleaseByIdUsesTheRepositoryIdEndpoint()
    {
        var handler = new RecordingHandler(ReleaseJson());
        var release = await CreateApi(handler).FetchLatestReleaseById(540883721);

        release.Should().NotBeNull();
        handler.Requests.Should().ContainSingle().Which.ToString().Should().Be("https://api.github.com/repositories/540883721/releases/latest");
    }

    [Fact]
    public async Task FetchReleaseByTagByIdUsesTheRepositoryIdTagsEndpoint()
    {
        var handler = new RecordingHandler(ReleaseJson());
        var release = await CreateApi(handler).FetchReleaseByTagById(514620889, "v0.8.0");

        release.Should().NotBeNull();
        handler.Requests.Should().ContainSingle().Which.ToString().Should().Be("https://api.github.com/repositories/514620889/releases/tags/v0.8.0");
    }

    [Fact]
    public async Task TagsWithReservedCharactersAreEscaped()
    {
        var handler = new RecordingHandler(ReleaseJson());
        await CreateApi(handler).FetchReleaseByTagById(1, "v1.0/rc 1");

        handler.Requests.Should().ContainSingle().Which.AbsoluteUri.Should().EndWith("/releases/tags/v1.0%2Frc%201");
    }

    [Fact]
    public async Task ANotFoundReleaseIsNull()
    {
        var handler = new RecordingHandler("{}", HttpStatusCode.NotFound);
        (await CreateApi(handler).FetchReleaseByTagById(1, "nope")).Should().BeNull();
    }

    [Fact]
    public async Task AssetDigestParsesToASha256Value()
    {
        var handler = new RecordingHandler(ReleaseJson(AssetJson("a.zip", "sha256:" + DigestHex)));
        var release = await CreateApi(handler).FetchReleaseByTag("o", "r", "v1.0.0");

        var asset = release!.Assets.Should().ContainSingle().Subject;
        asset.Digest.Should().Be("sha256:" + DigestHex);
        asset.GetSha256().Should().Be(Sha256Value.FromHex(DigestHex));
    }

    [Fact]
    public async Task AMissingAssetDigestStaysNull()
    {
        var handler = new RecordingHandler(ReleaseJson(AssetJson("a.zip", null)));
        var release = await CreateApi(handler).FetchReleaseByTag("o", "r", "v1.0.0");

        var asset = release!.Assets.Should().ContainSingle().Subject;
        asset.Digest.Should().BeNull();
        asset.GetSha256().Should().BeNull();
    }

    [Fact]
    public void ADigestOfAnotherAlgorithmIsNotASha256()
    {
        var asset = Asset("a.zip", "sha512:" + new string('a', 128));
        asset.GetSha256().Should().BeNull();
    }

    [Fact]
    public void AMalformedSha256DigestThrows()
    {
        var asset = Asset("a.zip", "sha256:nothex");
        var act = () => asset.GetSha256();
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void PickPrefersTheAssetWhoseNameContainsTheHint()
    {
        var release = Release(Asset("me3-debug.zip"), Asset("me3-release.zip"));
        ReleaseAssets.Pick(release, ".zip", "release")!.Name.Should().Be("me3-release.zip");
    }

    [Fact]
    public void PickFallsBackToTheFirstAssetWithTheSuffix()
    {
        var release = Release(Asset("notes.txt"), Asset("a.zip"), Asset("b.zip"));
        ReleaseAssets.Pick(release, ".zip", "missing")!.Name.Should().Be("a.zip");
        ReleaseAssets.Pick(release, ".zip", null)!.Name.Should().Be("a.zip");
    }

    [Fact]
    public void PickReturnsNullWhenNoAssetHasTheSuffix()
    {
        var release = Release(Asset("notes.txt"));
        ReleaseAssets.Pick(release, ".zip", "release").Should().BeNull();
    }

    [Fact]
    public void PickWithTheLinuxTarballSuffixNeverTakesAWindowsZipOrADebugBuild()
    {
        var release = Release(
            Asset("me3-windows-amd64.zip"), Asset("me3-windows-amd64-debug.zip"),
            Asset("me3-linux-amd64-debug.tar.gz"), Asset("me3-linux-amd64.tar.gz"), Asset("me3-installer.msi")
        );

        ReleaseAssets.Pick(release, "linux-amd64.tar.gz", "me3-linux-amd64")!.Name.Should().Be("me3-linux-amd64.tar.gz");
    }

    [Fact]
    public void PickWithTheLinuxTarballSuffixFindsNothingInAReleaseThatShipsOnlyWindowsAndDebugBuilds()
    {
        var release = Release(Asset("me3-windows-amd64.zip"), Asset("me3-linux-amd64-debug.tar.gz"));

        ReleaseAssets.Pick(release, "linux-amd64.tar.gz", "me3-linux-amd64").Should().BeNull();
    }

    private static Asset Asset(string name, string? digest = null) => new(
        Url: "u", Id: 1, Name: name, ContentType: "application/zip", State: "uploaded", Size: 1, DownloadCount: 0,
        CreatedAt: default, UpdatedAt: default, BrowserDownloadUrl: "https://example.invalid/" + name, Digest: digest
    );

    private static Release Release(params Asset[] assets) => new(
        Url: "u", AssetsUrl: "u", HtmlUrl: "u", Id: 1, TagName: "v1", TargetCommitish: "main", Name: "n",
        IsDraft: false, IsPrerelease: false, CreatedAt: default, PublishedAt: default, Assets: assets,
        TarballUrl: "u", ZipballUrl: "u", Body: ""
    );
}
