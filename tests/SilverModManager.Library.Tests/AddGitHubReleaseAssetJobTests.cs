using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Backend;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.GitHub;
using SilverModManager.Networking.GitHub.DTOs;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using System.Net.Http.Headers;
using Xunit;

namespace SilverModManager.Library.Tests;

public partial class AddGitHubReleaseAssetJobTests(IServiceProvider serviceProvider, IConnection connection, TemporaryFileManager temporaryFileManager)
{
    private const string AssetName = "tool-linux-amd64.tar.gz";
    private static readonly byte[] Payload = Enumerable.Range(0, 4000).Select(i => (byte)(i * 13 + 5)).ToArray();
    private static readonly Sha256Value PayloadDigest = Sha256Value.From(SHA256.HashData(Payload));
    private static readonly Sha256Value OtherDigest = Sha256Value.From(SHA256.HashData("other"u8));

    internal sealed class PayloadHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return Task.FromResult(response);
        }
    }

    internal sealed class StubApi(Release? release) : IGitHubApi
    {
        public AuthenticationHeaderValue? AuthenticationHeader { get; set; }
        public string? Requested { get; private set; }

        public ValueTask<Release[]?> FetchReleases(string organization, string repository, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<Release?> FetchLatestRelease(string organization, string repository, IComparer<Release>? comparer = null, CancellationToken cancellationToken = default)
        {
            Requested = $"highest tag {organization}/{repository}";
            return ValueTask.FromResult(release);
        }

        public ValueTask<Release?> FetchMarkedLatestRelease(string organization, string repository, CancellationToken cancellationToken = default)
        {
            Requested = $"latest {organization}/{repository}";
            return ValueTask.FromResult(release);
        }

        public ValueTask<Release?> FetchReleaseByTag(string organization, string repository, string tag, CancellationToken cancellationToken = default)
        {
            Requested = $"tag {organization}/{repository} {tag}";
            return ValueTask.FromResult(release);
        }

        public ValueTask<Release?> FetchLatestReleaseById(long repositoryId, CancellationToken cancellationToken = default)
        {
            Requested = $"latest #{repositoryId}";
            return ValueTask.FromResult(release);
        }

        public ValueTask<Release?> FetchReleaseByTagById(long repositoryId, string tag, CancellationToken cancellationToken = default)
        {
            Requested = $"tag #{repositoryId} {tag}";
            return ValueTask.FromResult(release);
        }
    }

    private static Release ReleaseWith(string tag, string? assetDigest)
    {
        var asset = new Asset(
            Url: "u", Id: 1, Name: AssetName, ContentType: "application/gzip", State: "uploaded", Size: Payload.Length,
            DownloadCount: 0, CreatedAt: default, UpdatedAt: default,
            BrowserDownloadUrl: "https://example.invalid/" + AssetName, Digest: assetDigest
        );
        var debug = asset with { Name = "tool-debug.zip", BrowserDownloadUrl = "https://example.invalid/tool-debug.zip" };
        return new Release("u", "u", "https://github.com/o/r/releases/tag/" + tag, 1, tag, "main", tag, false, false, default, default, [debug, asset], "u", "u", "");
    }

    private IJobTask<AddGitHubReleaseAssetJob, GitHubReleaseLibraryFile.ReadOnly> Run(
        StubApi api,
        GitHubRepositoryRef repository,
        string? tag,
        Sha256Value? expected)
    {
        return AddGitHubReleaseAssetJob.Create(
            serviceProvider, api, new HttpClient(new PayloadHandler(Payload)),
            repository, tag, suffix: ".tar.gz", nameHint: "linux", expected
        );
    }

    private static string Digest(Sha256Value value) => "sha256:" + value;

    private async Task<int> TemporaryEntryCount()
    {
        // Entries that sit next to a fresh temporary folder, i.e. everything the manager currently holds.
        var probe = temporaryFileManager.CreateFolder();
        var root = probe.Path.Parent.ToString();
        await probe.DisposeAsync();
        return Directory.GetFileSystemEntries(root).Length;
    }

    // Other tests in this class add the same asset to the shared database, so compare counts, not presence.
    private int LibraryCopiesOfThePayload() => LibraryFile.All(connection.Db).Count(file => file.FileName == AssetName);

    [Fact]
    public async Task AMatchingPinnedDigestAddsTheFileAndRecordsTheRelease()
    {
        var api = new StubApi(ReleaseWith("v1.2.3", Digest(PayloadDigest)));

        var result = await Run(api, GitHubRepositoryRef.FromId(540883721), "v1.2.3", PayloadDigest);

        api.Requested.Should().Be("tag #540883721 v1.2.3");
        result.Tag.Should().Be("v1.2.3");
        result.AssetName.Should().Be(AssetName);
        result.PinnedSha256.Should().Be(PayloadDigest);
        result.RepositoryId.Value.Should().Be(540883721UL);
        result.Organization.HasValue.Should().BeFalse();
        result.AsLibraryFile().FileName.Should().Be(AssetName);
        result.AsLibraryFile().Sha256.Value.Should().Be(PayloadDigest);
    }

    [Fact]
    public async Task AMismatchingPinnedDigestFailsAndLeavesNothingBehind()
    {
        var api = new StubApi(ReleaseWith("v1", assetDigest: null));
        var before = await TemporaryEntryCount();
        var copiesBefore = LibraryCopiesOfThePayload();

        var act = async () => await Run(api, GitHubRepositoryRef.FromName("o", "r"), "v1", OtherDigest);

        var failure = await act.Should().ThrowAsync<ReleaseAssetIntegrityException>();
        failure.Which.Message.Should().Contain(OtherDigest.ToString()).And.Contain(PayloadDigest.ToString()).And.Contain(AssetName);
        LibraryCopiesOfThePayload().Should().Be(copiesBefore);
        (await TemporaryEntryCount()).Should().Be(before);
    }

    [Fact]
    public async Task WithoutAPinTheComputedDigestBecomesThePin()
    {
        var api = new StubApi(ReleaseWith("v2", Digest(PayloadDigest)));

        var result = await Run(api, GitHubRepositoryRef.FromName("o", "r"), tag: null, expected: null);

        api.Requested.Should().Be("latest o/r", "by name, latest means the release GitHub marks as latest, as it does by id");
        result.Tag.Should().Be("v2", "the resolved tag is recorded, not the request for the latest one");
        result.PinnedSha256.Should().Be(PayloadDigest);
        result.Organization.Value.Should().Be("o");
        result.Repository.Value.Should().Be("r");
        result.RepositoryId.HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task AnAssetDigestThatDisagreesWithTheBytesFailsEvenWithoutAPin()
    {
        var api = new StubApi(ReleaseWith("v3", Digest(OtherDigest)));
        var copiesBefore = LibraryCopiesOfThePayload();

        var act = async () => await Run(api, GitHubRepositoryRef.FromId(1), tag: null, expected: null);

        await act.Should().ThrowAsync<ReleaseAssetIntegrityException>();
        api.Requested.Should().Be("latest #1");
        LibraryCopiesOfThePayload().Should().Be(copiesBefore);
    }

    [Fact]
    public async Task AReleaseWithoutAMatchingAssetFails()
    {
        var api = new StubApi(ReleaseWith("v4", null));

        var act = async () => await AddGitHubReleaseAssetJob.Create(
            serviceProvider, api, new HttpClient(new PayloadHandler(Payload)),
            GitHubRepositoryRef.FromId(1), "v4", suffix: ".7z", nameHint: null, expectedSha256: null
        );

        await act.Should().ThrowAsync<ReleaseAssetIntegrityException>().WithMessage("*.7z*");
    }

    [Fact]
    public async Task AMissingReleaseFails()
    {
        var api = new StubApi(release: null);
        var act = async () => await Run(api, GitHubRepositoryRef.FromId(1), "gone", expected: null);
        await act.Should().ThrowAsync<ReleaseAssetIntegrityException>().WithMessage("*gone*");
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            DIHelpers.ConfigureServices(services);
            services
                .AddLibraryModels()
                .AddOSInterop()
                .AddFileExtractors();
        }
    }
}
