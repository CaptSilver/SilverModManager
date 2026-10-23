using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Library;
using NexusMods.Networking.HttpDownloader;
using NexusMods.Paths;
using NexusMods.Sdk.Library;
using NexusMods.Wabbajack.Acquire;
using NexusMods.Wabbajack.Format;

namespace NexusMods.Wabbajack.Tests.Acquire;

/// <summary>The real job factory, with an HTTP client that serves a plain file host and the Wabbajack CDN.</summary>
public class ArchiveDownloadJobsTests(ITestOutputHelper helper) : AcquireTestBase<ArchiveDownloadJobsTests>(helper)
{
    private static readonly byte[] PlainBytes = "served by a plain file host"u8.ToArray();
    private static readonly Uri PlainUrl = new("https://files.test/mods/plain.7z");
    private static readonly Uri CdnUrl = new("https://authored-files.wabbajack.org/Some%20Mod-123-1-0.7z_abc");

    private readonly CdnFixture _cdn = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services).AddSingleton(new HttpClient(new RoutingHandler(PlainBytes, new CdnHandler(_cdn))));
    }

    private IArchiveDownloadJobs Jobs => ServiceProvider.GetRequiredService<IArchiveDownloadJobs>();

    private static ArchivePlanRow AutomaticRow(DownloadState state) => new(Entry(state), ArchiveState.Automatic, null, null, null);

    [Fact]
    public async Task AnHttpRowIsFetchedFromItsUrlWhichIsAlsoTheDownloadPage()
    {
        await using var destination = TemporaryFileManager.CreateFile();

        var job = await Jobs.Create(AutomaticRow(new HttpState(PlainUrl, [])), destination.Path, CancellationToken.None);
        var file = await LibraryService.AddDownload(job);

        file.XxHash64.Value.Should().Be(Hash64(PlainBytes));
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

        file.XxHash64.Value.Should().Be(_cdn.FileHash);
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
}
