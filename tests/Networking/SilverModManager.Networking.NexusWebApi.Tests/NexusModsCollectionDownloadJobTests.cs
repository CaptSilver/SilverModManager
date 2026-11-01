using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.DTOs;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Networking.HttpDownloader.Tests;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Networking.NexusWebApi.Tests;

/// <summary>
/// Nexus returns several CDN links for a collection and the one listed first may be unreachable from the user's
/// network. These run the real job system, <see cref="Networking.HttpDownloader.HttpDownloadJob"/> and library against
/// local listeners, with only the Nexus API client and the HTTP timeout substituted.
/// </summary>
public class NexusModsCollectionDownloadJobTests : IDisposable
{
    private static readonly CollectionSlug Slug = CollectionSlug.From("testslug");
    private static readonly RevisionNumber Revision = RevisionNumber.From(7);

    private readonly List<HttpClient> _clients = [];
    private readonly IServiceProvider _serviceProvider;
    private readonly ILibraryService _libraryService;
    private readonly TemporaryFileManager _temporaryFileManager;

    public NexusModsCollectionDownloadJobTests(IServiceProvider serviceProvider, ILibraryService libraryService, TemporaryFileManager temporaryFileManager)
    {
        _serviceProvider = serviceProvider;
        _libraryService = libraryService;
        _temporaryFileManager = temporaryFileManager;
    }

    public void Dispose()
    {
        foreach (var client in _clients) client.Dispose();
    }

    private static CollectionLink Link(string shortName, Uri uri) => new() { Name = shortName, ShortName = shortName, Uri = uri };

    private IServiceProvider ProviderWithLinks(params CollectionLink[] links)
    {
        var api = Substitute.For<INexusApiClient>();
        api.CollectionDownloadLinksAsync(Slug, Revision, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new Response<CollectionDownloadLinks>
            {
                Data = new CollectionDownloadLinks { DownloadLinks = links },
                Metadata = new ResponseMetadata(),
                StatusCode = HttpStatusCode.OK,
            });

        // The shipped client waits 100 s on a host that doesn't answer; a test can't.
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        _clients.Add(client);
        return new OverridingServiceProvider(_serviceProvider, (typeof(INexusApiClient), api), (typeof(HttpClient), client));
    }

    private IJobTask<NexusModsCollectionDownloadJob, AbsolutePath> CreateJob(IServiceProvider provider, AbsolutePath destination)
        => NexusModsCollectionDownloadJob.Create(provider, Slug, Revision, destination);

    [Fact]
    public async Task WhenTheFirstLinkIsUnreachable_TheNextLinkIsDownloaded()
    {
        var content = "second server's file"u8.ToArray();
        using var dead = new SilentListener();
        using var alive = new SingleFileServer(content);
        var provider = ProviderWithLinks(Link("dead", dead.UriWithSignature("A")), Link("alive", alive.Uri));
        await using var destination = _temporaryFileManager.CreateFile();

        var libraryFile = await _libraryService.AddDownload(CreateJob(provider, destination.Path), UnassignedAcquisition.Instance);

        libraryFile.Size.Value.Should().Be((ulong)content.Length);
        File.ReadAllBytes(destination.Path.ToString()).Should().Equal(content);
    }

    [Fact]
    public async Task WhenEveryLinkIsUnreachable_TheJobFailsNamingEachHostWithoutTheirQueries()
    {
        using var first = new SilentListener();
        using var second = new SilentListener();
        var provider = ProviderWithLinks(Link("first", first.UriWithSignature("SECRET1")), Link("second", second.UriWithSignature("SECRET2")));
        await using var destination = _temporaryFileManager.CreateFile();
        var job = CreateJob(provider, destination.Path);

        var thrown = await FluentActions.Awaiting(async () => await job).Should().ThrowAsync<Exception>();

        job.Job.Status.Should().Be(JobStatus.Failed);
        thrown.Which.Should().NotBeAssignableTo<OperationCanceledException>();
        thrown.Which.Message.Should().Contain($"127.0.0.1:{first.Port}").And.Contain($"127.0.0.1:{second.Port}").And.NotContain("SECRET");
    }

    [Fact]
    public async Task WhenNexusReturnsNoLinks_TheJobFailsWithAReadableMessage()
    {
        var provider = ProviderWithLinks();
        await using var destination = _temporaryFileManager.CreateFile();
        var job = CreateJob(provider, destination.Path);

        var thrown = await FluentActions.Awaiting(async () => await job).Should().ThrowAsync<Exception>();

        thrown.Which.Message.Should().Contain("no download links");
    }

    /// <summary>
    /// Resolves a few services to substitutes and everything else from the real container.
    /// </summary>
    private sealed class OverridingServiceProvider(IServiceProvider inner, params (Type Type, object Instance)[] overrides) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            foreach (var (type, instance) in overrides)
                if (type == serviceType) return instance;
            return inner.GetService(serviceType);
        }
    }
}
