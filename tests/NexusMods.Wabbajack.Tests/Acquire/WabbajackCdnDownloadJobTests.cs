using System.IO.Compression;
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Library;
using NexusMods.Paths;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Library;
using NexusMods.Wabbajack.Acquire;

namespace NexusMods.Wabbajack.Tests.Acquire;

public class WabbajackCdnDownloadJobTests(IServiceProvider provider, TemporaryFileManager temporaryFileManager)
{
    private static readonly Uri CdnUrl = new("https://authored-files.wabbajack.org/Some%20Mod-123-1-0.7z_abc");

    private WabbajackCdnDownloadJob JobFor(CdnHandler handler, AbsolutePath destination, Uri? url = null) => new()
    {
        Client = new HttpClient(handler),
        Logger = NullLogger<WabbajackCdnDownloadJob>.Instance,
        Url = url ?? CdnUrl,
        Destination = destination,
        RetryDelay = TimeSpan.FromMilliseconds(1),
    };

    private IJobTask<WabbajackCdnDownloadJob, AbsolutePath> Begin(WabbajackCdnDownloadJob job)
        => provider.GetRequiredService<IJobMonitor>().Begin<WabbajackCdnDownloadJob, AbsolutePath>(job);

    [Fact]
    public async Task EveryPartIsFetchedAndTheFileIsWrittenAndAddedToTheLibraryWithItsHash()
    {
        var fixture = new CdnFixture();
        var handler = new CdnHandler(fixture);
        await using var destination = temporaryFileManager.CreateFile();

        var library = provider.GetRequiredService<ILibraryService>();
        var file = await library.AddDownload(Begin(JobFor(handler, destination)));

        destination.Path.FileExists.Should().BeTrue();
        (await destination.Path.ReadAllBytesAsync()).Should().Equal(fixture.Content);
        file.XxHash64.HasValue.Should().BeTrue();
        file.XxHash64.Value.Should().Be(fixture.FileHash);
        file.FileName.Should().Be(RelativePath.FromUnsanitizedInput(fixture.OriginalFileName));
        file.AsLibraryItem().TryGetAsDownloadedFile(out var downloaded).Should().BeTrue();
        downloaded.DownloadPageUri.Should().Be(CdnUrl);
    }

    [Fact]
    public async Task ACorruptedPartFailsTheJobNamingThePart()
    {
        var fixture = new CdnFixture();
        var handler = new CdnHandler(fixture)
        {
            Respond = (path, _) => path.EndsWith("/parts/1", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[CdnFixture.PartSize]) }
                : null,
        };
        await using var destination = temporaryFileManager.CreateFile();

        var act = async () => await Begin(JobFor(handler, destination));

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("part 1");
    }

    [Fact]
    public async Task ABunnyCdnAddressIsRequestedFromTheWabbajackHostWithThatHostHeader()
    {
        var fixture = new CdnFixture();
        var handler = new CdnHandler(fixture);
        await using var destination = temporaryFileManager.CreateFile();

        await Begin(JobFor(handler, destination, new Uri("https://wabbajack.b-cdn.net/Some%20Mod-123-1-0.7z_abc")));

        handler.Requests.Should().NotBeEmpty();
        handler.Requests.Should().OnlyContain(r => r.Uri.Host == "authored-files.wabbajack.org" && r.Host == "authored-files.wabbajack.org");
    }

    [Fact]
    public async Task APartThatFailsTwiceThenSucceedsIsRequestedThreeTimes()
    {
        var fixture = new CdnFixture();
        var handler = new CdnHandler(fixture)
        {
            Respond = (path, earlier) => path.EndsWith("/parts/2", StringComparison.Ordinal) && earlier < 2
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : null,
        };
        await using var destination = temporaryFileManager.CreateFile();

        await Begin(JobFor(handler, destination));

        handler.RequestsFor("/parts/2").Should().Be(3);
        (await destination.Path.ReadAllBytesAsync()).Should().Equal(fixture.Content);
    }

    [Fact]
    public async Task AMissingDefinitionFailsAfterOneRequest()
    {
        var handler = new CdnHandler(new CdnFixture())
        {
            Respond = (path, _) => path.EndsWith("/definition.json.gz", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.NotFound) : null,
        };
        await using var destination = temporaryFileManager.CreateFile();

        var act = async () => await Begin(JobFor(handler, destination));

        await act.Should().ThrowAsync<Exception>();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task APartThatAlwaysFailsIsGivenUpOnAfterSixRequests()
    {
        var handler = new CdnHandler(new CdnFixture(length: 50))
        {
            Respond = (path, _) => path.EndsWith("/parts/0", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : null,
        };
        await using var destination = temporaryFileManager.CreateFile();

        var act = async () => await Begin(JobFor(handler, destination));

        await act.Should().ThrowAsync<Exception>();
        handler.RequestsFor("/parts/0").Should().Be(6);
    }

    [Fact]
    public async Task ADefinitionWithoutAFileNameFailsNamingTheField()
    {
        var gz = new MemoryStream();
        using (var gzip = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write("{\"OriginalFileName\":null,\"Size\":0,\"Hash\":\"AAAAAAAAAAA=\",\"Parts\":[]}"u8);
        var handler = new CdnHandler(new CdnFixture())
        {
            Respond = (path, _) => path.EndsWith("/definition.json.gz", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(gz.ToArray()) }
                : null,
        };
        await using var destination = temporaryFileManager.CreateFile();

        var act = async () => await Begin(JobFor(handler, destination));

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*OriginalFileName*");
    }

    [Fact]
    public void MakeMessageRewritesOnlyTheKnownBunnyHostsAndAlwaysSetsHost()
    {
        foreach (var (from, to) in WabbajackCdnDownloadJob.DomainRemaps)
        {
            using var message = WabbajackCdnDownloadJob.MakeMessage(new Uri($"https://{from}/file/definition.json.gz"));
            message.RequestUri!.Host.Should().Be(to);
            message.Headers.Host.Should().Be(to);
        }

        using var unchanged = WabbajackCdnDownloadJob.MakeMessage(new Uri("https://mirror.wabbajack.org/x"));
        unchanged.RequestUri!.Host.Should().Be("mirror.wabbajack.org");
        unchanged.Headers.Host.Should().Be("mirror.wabbajack.org");
    }

    [Fact]
    public void TheRemapTableMatchesTheCdnsAddresses()
    {
        WabbajackCdnDownloadJob.DomainRemaps.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["wabbajack.b-cdn.net"] = "authored-files.wabbajack.org",
            ["wabbajack-mirror.b-cdn.net"] = "mirror.wabbajack.org",
            ["wabbajack-patches.b-cdn.net"] = "patches.wabbajack.org",
            ["wabbajacktest.b-cdn.net"] = "test-files.wabbajack.org",
        });
    }
}
