using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Library;
using SilverModManager.Networking.Forums;
using SilverModManager.Networking.Forums.Auth;
using SilverModManager.Networking.Forums.Downloads;
using SilverModManager.Wabbajack.Acquire.Hosts;
using SilverModManager.Networking.HttpDownloader;
using NexusMods.Paths;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;

namespace SilverModManager.Wabbajack.Tests.Acquire;

/// <summary>The real job factory, with an HTTP client that serves a plain file host and the Wabbajack CDN.</summary>
public class ArchiveDownloadJobsTests(ITestOutputHelper helper) : AcquireTestBase<ArchiveDownloadJobsTests>(helper)
{
    private static readonly byte[] PlainBytes = "served by a plain file host"u8.ToArray();
    private static readonly Uri PlainUrl = new("https://files.test/mods/plain.7z");
    private static readonly Uri CdnUrl = new("https://authored-files.wabbajack.org/Some%20Mod-123-1-0.7z_abc");

    private static readonly byte[] ForumBytes = "served by a forum's file host"u8.ToArray();
    private static readonly Uri ForumPage = new("https://www.loverslab.com/files/file/1234-some-mod/?do=download&r=77&csrfKey=secret");

    private readonly CdnFixture _cdn = new();
    private readonly ForumHandler _forum = new(ForumBytes);

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        var client = new HttpClient(new RoutingHandler(PlainBytes, new ForumHandler.Chain(_forum, new CdnHandler(_cdn))));
        return base.AddServices(services)
            .AddSingleton(client)
            // The forum lookup goes through the same fake network instead of the client AddForums builds.
            .AddSingleton(sp => new ForumFilesClient(client, sp.GetRequiredService<ForumOAuth>(), sp.GetRequiredService<IForumCredentials>(),
                TimeProvider.System, NullLogger<ForumFilesClient>.Instance));
    }

    private async Task SignedIn(ForumSite site = ForumSite.LoversLab)
    {
        var cache = ServiceProvider.GetRequiredService<IForumCredentials>().For(site);
        await cache.SetTokenAsync(new OAuthToken("the-token", null, DateTimeOffset.UtcNow + TimeSpan.FromHours(1)), CancellationToken.None);
    }

    private ArchivePlanRow ForumRow(DownloadState state, byte[]? bytes = null)
        => new(Entry(state, Hash64(bytes ?? ForumBytes), (bytes ?? ForumBytes).Length, "mod.7z"), ArchiveState.Automatic, null, null, null);

    private IArchiveDownloadJobs Jobs => ServiceProvider.GetRequiredService<IArchiveDownloadJobs>();

    private static ArchivePlanRow AutomaticRow(DownloadState state) => new(Entry(state), ArchiveState.Automatic, null, null, null);

    [Fact]
    public async Task AnHttpRowIsFetchedFromItsUrlWhichIsAlsoTheDownloadPage()
    {
        await using var destination = TemporaryFileManager.CreateFile();

        var job = await Jobs.Create(AutomaticRow(new HttpState(PlainUrl, [])), destination.Path, CancellationToken.None);
        var file = await LibraryService.AddDownload(job);

        file.XxHash64Identity().Should().Be(Hash64(PlainBytes));
        var http = job.JobDefinition.Should().BeOfType<HttpDownloadJob>().Subject;
        http.Uri.Should().Be(PlainUrl);
        http.DownloadPageUri.Should().Be(PlainUrl);
    }

    [Fact]
    public async Task ACdnRowIsFetchedPartByPart()
    {
        await using var destination = TemporaryFileManager.CreateFile();

        var job = await Jobs.Create(AutomaticRow(new WabbajackCdnState(CdnUrl)), destination.Path, CancellationToken.None);
        var file = await LibraryService.AddDownload(job);

        file.XxHash64Identity().Should().Be(_cdn.FileHash);
        file.FileName.Should().Be(RelativePath.FromUnsanitizedInput(_cdn.OriginalFileName));
    }

    [Fact]
    public async Task ANexusRowForAGameWithoutADomainIsRefusedNamingTheGame()
    {
        await using var destination = TemporaryFileManager.CreateFile();

        var act = () => Jobs.Create(AutomaticRow(new NexusState("NotAGame", 1, 2, null, null, null, false)), destination.Path, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*NotAGame*");
    }

    [Fact]
    public async Task ASourceThatIsNotDownloadedAutomaticallyIsRefused()
    {
        await using var destination = TemporaryFileManager.CreateFile();

        var act = () => Jobs.Create(AutomaticRow(new ManualState(new Uri("https://example.test/m"), "get it")), destination.Path, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ManualState*");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALoversLabRowIsFetchedFromTheAddressTheFilesApiListsAndKeepsTheQueryOutOfThePage(bool listHasPage)
    {
        await SignedIn();
        await using var destination = TemporaryFileManager.CreateFile();
        var state = new LoversLabState(listHasPage ? ForumPage : null, "mod.7z", 42, false);

        var job = await Jobs.Create(ForumRow(state), destination.Path, CancellationToken.None);
        var file = await LibraryService.AddDownload(job);

        file.XxHash64Identity().Should().Be(Hash64(ForumBytes));
        file.FileName.Should().Be(RelativePath.FromUnsanitizedInput("mod.7z"));
        job.JobDefinition.Should().BeOfType<ForumDownloadJob>();
        file.AsLibraryItem().TryGetAsDownloadedFile(out var downloaded).Should().BeTrue();
        downloaded.DownloadPageUri.Query.Should().BeEmpty();
        downloaded.DownloadPageUri.Should().Be(listHasPage
            ? new Uri("https://www.loverslab.com/files/file/1234-some-mod/")
            : new Uri("https://files.loverslab.test/get/mod"));
        _forum.ListingRequests.Should().ContainSingle().Which.Authorization.Should().Be("Bearer the-token");
    }

    [Fact]
    public async Task AVectorPlexusRowUsesItsOwnSite()
    {
        await SignedIn(ForumSite.VectorPlexus);
        await using var destination = TemporaryFileManager.CreateFile();

        var job = await Jobs.Create(ForumRow(new VectorPlexusState(null, "mod.7z", 42, false)), destination.Path, CancellationToken.None);
        var file = await LibraryService.AddDownload(job);

        file.XxHash64Identity().Should().Be(Hash64(ForumBytes));
        _forum.ListingRequests.Single().Host.Should().Be("vectorplexus.com");
    }

    [Fact]
    public async Task TheFileRequestCarriesNoAuthorization()
    {
        await SignedIn();
        await using var destination = TemporaryFileManager.CreateFile();

        await LibraryService.AddDownload(await Jobs.Create(ForumRow(new LoversLabState(null, "mod.7z", 42, false)), destination.Path, CancellationToken.None));

        _forum.FileRequests.Should().ContainSingle().Which.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task AFileOfAnotherSizeIsRefusedBeforeAnyByteIsRequested()
    {
        await SignedIn();
        await using var destination = TemporaryFileManager.CreateFile();
        var row = ForumRow(new LoversLabState(null, "mod.7z", 42, false), new byte[ForumBytes.Length + 1]);

        var act = () => Jobs.Create(row, destination.Path, CancellationToken.None);

        await act.Should().ThrowAsync<ForumFileSizeMismatchException>().WithMessage("*different size*");
        _forum.FileRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task AFileTheListingDoesNotNameIsRefusedNamingIt()
    {
        await SignedIn();
        await using var destination = TemporaryFileManager.CreateFile();
        var row = new ArchivePlanRow(Entry(new LoversLabState(null, "gone.7z", 42, false), Hash64(ForumBytes), ForumBytes.Length, "gone.7z"), ArchiveState.Automatic, null, null, null);

        var act = () => Jobs.Create(row, destination.Path, CancellationToken.None);

        await act.Should().ThrowAsync<ForumFileNotListedException>().WithMessage("*'gone.7z'*");
        _forum.FileRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task AFileListedTwiceIsRefusedWithTheReadableMessage()
    {
        await SignedIn();
        _forum.Listing = """{"files":[{"name":"mod.7z","url":"https://files.loverslab.test/get/1","size":29},{"name":"mod.7z","url":"https://files.loverslab.test/get/2","size":29}]}""";
        await using var destination = TemporaryFileManager.CreateFile();

        var act = () => Jobs.Create(ForumRow(new LoversLabState(null, "mod.7z", 42, false)), destination.Path, CancellationToken.None);

        await act.Should().ThrowAsync<ForumFileAmbiguousException>().WithMessage("LoversLab lists 'mod.7z' more than once under this file; download it from the page");
    }

    [Fact]
    public async Task AForumAttachmentIsNotDownloadedAutomatically()
    {
        await SignedIn();
        await using var destination = TemporaryFileManager.CreateFile();

        var act = () => Jobs.Create(ForumRow(new LoversLabState(ForumPage, "mod.7z", 42, true)), destination.Path, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*LoversLabState*not downloaded automatically*");
        _forum.ListingRequests.Should().BeEmpty();
    }

    /// <summary>Answers HEAD and GET for files.test with fixed bytes; everything else goes to the CDN handler.</summary>
    private sealed class RoutingHandler(byte[] plain, HttpMessageHandler cdn) : DelegatingHandler(cdn)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host != PlainUrl.Host) return base.SendAsync(request, cancellationToken);

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : plain),
            };
            response.Content.Headers.ContentLength = plain.Length;
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// The forums' files API and the file host behind it: <c>/api/downloads/files/{id}</c> lists one file at a signed
    /// address, and that address serves the bytes.
    /// </summary>
    private sealed class ForumHandler(byte[] bytes) : HttpMessageHandler
    {
        private readonly object _lock = new();
        private readonly List<Seen> _seen = [];

        public record Seen(Uri Uri, string Host, string? Authorization);

        public string? Listing { get; set; }

        public IReadOnlyList<Seen> ListingRequests => Snapshot().Where(r => r.Uri.AbsolutePath.StartsWith("/api/downloads/files/", StringComparison.Ordinal)).ToArray();

        public IReadOnlyList<Seen> FileRequests => Snapshot().Where(r => r.Host == "files.loverslab.test").ToArray();

        private Seen[] Snapshot()
        {
            lock (_lock) return _seen.ToArray();
        }

        public static bool Owns(Uri uri) => uri.Host is "api.loverslab.com" or "vectorplexus.com" or "files.loverslab.test";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (_lock) _seen.Add(new Seen(uri, uri.Host, request.Headers.Authorization?.ToString()));

            if (uri.AbsolutePath.StartsWith("/api/downloads/files/", StringComparison.Ordinal))
            {
                var listing = Listing ?? $$"""{"files":[{"name":"mod.7z","url":"https://files.loverslab.test/get/mod?key=signed","size":{{bytes.Length}}}]}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(listing) });
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.Content.Headers.ContentLength = bytes.Length;
            return Task.FromResult(response);
        }

        /// <summary>Sends the forum's hosts to the forum fake and everything else on.</summary>
        public sealed class Chain(ForumHandler forum, HttpMessageHandler next) : DelegatingHandler(next)
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Owns(request.RequestUri!)
                    ? forum.SendForChain(request, cancellationToken)
                    : base.SendAsync(request, cancellationToken);
        }

        private Task<HttpResponseMessage> SendForChain(HttpRequestMessage request, CancellationToken cancellationToken) => SendAsync(request, cancellationToken);
    }
}
