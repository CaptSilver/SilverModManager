using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Backend;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.Thunderstore;
using SilverModManager.Networking.Thunderstore.DTOs;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Thunderstore;
using System.Net.Http.Headers;
using Xunit;

namespace SilverModManager.Library.Tests;

public partial class ThunderstoreDownloadJobTests(IServiceProvider serviceProvider, IConnection connection, TemporaryFileManager temporaryFileManager)
{
    private const string ArchiveName = "Zehs-REPOLib-4.2.0.zip";
    private static readonly Uri DownloadUri = new("https://thunderstore.io/package/download/Zehs/REPOLib/4.2.0/");
    private static readonly ThunderstoreVersionRef RepoLib = new(new ThunderstorePackageRef("Zehs", "REPOLib"), new ThunderstoreVersionNumber(4, 2, 0));
    private static readonly byte[] Payload = Enumerable.Range(0, 3000).Select(i => (byte)(i * 7 + 3)).ToArray();
    private static readonly Sha256Value PayloadDigest = Sha256Value.From(SHA256.HashData(Payload));
    private static readonly Sha256Value OtherDigest = Sha256Value.From(SHA256.HashData("other"u8));

    private sealed class PayloadHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (request.RequestUri != DownloadUri) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload), RequestMessage = request };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            return Task.FromResult(response);
        }
    }

    // Holds the response open until the request is cancelled, so the job is mid-download when it is cancelled.
    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class StubApi(PackageVersion? version) : IThunderstoreApi
    {
        public Task<PackageVersion?> FetchVersion(ThunderstoreVersionRef requested, CancellationToken cancellationToken) => Task.FromResult(version);
        public Uri DownloadUri(ThunderstoreVersionRef requested) => ThunderstoreDownloadJobTests.DownloadUri;

        public Task<PackageDetail?> FetchPackage(ThunderstorePackageRef package, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ListingIndex> FetchListingIndex(string community, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<PackageListing> ReadChunk(Uri chunk, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<byte[]> DownloadBlob(Uri blob, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static PackageVersion Version(bool isActive = true, string? uuid = "8f1d0f3e-0000-4000-8000-000000000001") => new()
    {
        Namespace = "Zehs",
        Name = "REPOLib",
        FullName = "Zehs-REPOLib-4.2.0",
        VersionNumber = "4.2.0",
        DownloadUrl = DownloadUri.ToString(),
        IsActive = isActive,
        Uuid4 = uuid,
    };

    private IJobTask<ThunderstoreDownloadJob, ThunderstoreLibraryFile.ReadOnly> Run(
        PackageVersion? version,
        Sha256Value? expected = null,
        PayloadHandler? handler = null)
    {
        return ThunderstoreDownloadJob.Create(serviceProvider, new StubApi(version), new HttpClient(handler ?? new PayloadHandler()), RepoLib, "repo", expected);
    }

    private int LibraryCopies() => LibraryFile.All(connection.Db).Count(file => file.FileName == ArchiveName);
    private int ThunderstoreRows() => ThunderstoreLibraryFile.All(connection.Db).Count(file => file.Name == "REPOLib" && file.VersionNumber == "4.2.0");

    private async Task<int> TemporaryEntryCount()
    {
        var probe = temporaryFileManager.CreateFolder();
        var root = probe.Path.Parent.ToString();
        await probe.DisposeAsync();
        return Directory.GetFileSystemEntries(root).Length;
    }

    [Fact]
    public async Task DownloadingAVersionAddsTheZipAndRecordsThePackage()
    {
        var result = await Run(Version());

        result.Namespace.Should().Be("Zehs");
        result.Name.Should().Be("REPOLib");
        result.VersionNumber.Should().Be("4.2.0");
        result.Community.Should().Be("repo");
        result.PinnedSha256.Should().Be(PayloadDigest);
        result.VersionUuid.Value.Should().Be("8f1d0f3e-0000-4000-8000-000000000001");

        var libraryFile = result.AsLibraryFile();
        libraryFile.FileName.Should().Be(ArchiveName);
        libraryFile.AsLibraryItem().Name.Should().Be(ArchiveName);
        libraryFile.Sha256.Value.Should().Be(PayloadDigest);
        (await serviceProvider.GetRequiredService<IFileStore>().HaveFile(libraryFile.Hash)).Should().BeTrue();
    }

    [Fact]
    public async Task AVersionRecordWithoutAUuidLeavesTheUuidOut()
    {
        var result = await Run(Version(uuid: null));
        result.VersionUuid.HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task AMatchingExpectedDigestAddsTheFile()
    {
        var result = await Run(Version(), PayloadDigest);
        result.PinnedSha256.Should().Be(PayloadDigest);
        result.AsLibraryFile().FileName.Should().Be(ArchiveName);
    }

    [Fact]
    public async Task AMismatchingExpectedDigestFailsAndCommitsNothing()
    {
        var before = await TemporaryEntryCount();
        var copiesBefore = LibraryCopies();
        var rowsBefore = ThunderstoreRows();

        var act = async () => await Run(Version(), OtherDigest);

        var failure = await act.Should().ThrowAsync<ThunderstoreIntegrityException>();
        failure.Which.Message.Should().Contain(OtherDigest.ToString()).And.Contain(PayloadDigest.ToString()).And.Contain("Zehs-REPOLib-4.2.0");
        LibraryCopies().Should().Be(copiesBefore);
        ThunderstoreRows().Should().Be(rowsBefore);
        (await TemporaryEntryCount()).Should().Be(before, "the per-download folder is removed");
    }

    [Fact]
    public async Task AnUnknownVersionFailsNamingItAndDownloadsNothing()
    {
        var handler = new PayloadHandler();
        var copiesBefore = LibraryCopies();

        var act = async () => await Run(version: null, handler: handler);

        await act.Should().ThrowAsync<ThunderstoreIntegrityException>().WithMessage("*Zehs-REPOLib-4.2.0*");
        handler.Requests.Should().BeEmpty();
        LibraryCopies().Should().Be(copiesBefore);
    }

    [Fact]
    public async Task AWithdrawnVersionFailsSayingSo()
    {
        var handler = new PayloadHandler();
        var copiesBefore = LibraryCopies();

        var act = async () => await Run(Version(isActive: false), handler: handler);

        await act.Should().ThrowAsync<ThunderstoreIntegrityException>().WithMessage("*Zehs-REPOLib-4.2.0*withdrawn*");
        handler.Requests.Should().BeEmpty();
        LibraryCopies().Should().Be(copiesBefore);
    }

    [Fact]
    public async Task CancellingTheJobStopsTheDownloadAndAddsNothing()
    {
        var handler = new BlockingHandler();
        var copiesBefore = LibraryCopies();

        var task = ThunderstoreDownloadJob.Create(serviceProvider, new StubApi(Version()), new HttpClient(handler), RepoLib, "repo", expected: null);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        serviceProvider.GetRequiredService<IJobMonitor>().Cancel(task);

        var act = async () => await task;
        await act.Should().ThrowAsync<OperationCanceledException>();
        LibraryCopies().Should().Be(copiesBefore);
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
