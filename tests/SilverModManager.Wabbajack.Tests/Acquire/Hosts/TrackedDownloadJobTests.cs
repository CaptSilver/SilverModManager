using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.Networking.HttpDownloader;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Acquire.Hosts;

namespace SilverModManager.Wabbajack.Tests.Acquire.Hosts;

/// <summary>What a download from a file host tells the Downloads page: its name, where it comes from, and how far it got.</summary>
public class TrackedDownloadJobTests(IServiceProvider provider, TemporaryFileManager files) : HostedJobTestBase(provider, files)
{
    private static readonly Uri FileUrl = new("https://files.test/mods/a.7z?key=secret");

    private static HeaderedHttpDownloadJob JobFor(FakeHost host, AbsolutePath destination) => new()
    {
        Client = new HttpClient(host),
        Logger = NullLogger<HeaderedHttpDownloadJob>.Instance,
        Throttle = new HostThrottle(),
        Url = FileUrl,
        Headers = ["Accept: */*"],
        Destination = destination,
        FileName = "Some Archive.7z",
    };

    [Fact]
    public void EveryDownloadJobOfTheAssemblyIsListedOnTheDownloadsPage()
    {
        var jobs = typeof(HostedDownloadJob).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IDownloadJob).IsAssignableFrom(t));

        jobs.Should().OnlyContain(t => typeof(ITrackedDownloadJob).IsAssignableFrom(t));
        jobs.Should().NotBeEmpty();
    }

    [Fact]
    public async Task AHostJobNamesItselfItsHostAndAPageWithoutTheQuery()
    {
        await using var destination = Files.CreateFile();
        ITrackedDownloadJob job = JobFor(new FakeHost((_, _) => FakeHost.Bytes([])), destination);

        job.DisplayName.Should().Be("Some Archive.7z");
        job.SourceName.Should().Be("files.test");
        job.PageUri.Should().Be(new Uri("https://files.test/mods/a.7z"));
        job.Transfer.Should().BeNull("the job moves the bytes itself");
        job.SupportsPausing.Should().BeFalse("a host download can only start over");
    }

    [Fact]
    public async Task AHostJobReportsHowManyBytesItHasWrittenOutOfHowMany()
    {
        var plain = Content(3_000_000);
        var host = new FakeHost((_, _) => FakeHost.Bytes(plain));
        await using var destination = Files.CreateFile();
        var job = JobFor(host, destination);

        await Begin(job);

        var state = job.GetJobStateData().Should().BeAssignableTo<IHttpDownloadState>().Subject;
        state.ContentLength.Value.Value.Should().Be((ulong)plain.Length);
        state.TotalBytesDownloaded.Value.Should().Be((ulong)plain.Length);
    }

    [Fact]
    public async Task ACdnJobNamesItselfItsSourceAndAPageWithoutTheQuery()
    {
        await using var destination = Files.CreateFile();
        ITrackedDownloadJob job = new WabbajackCdnDownloadJob
        {
            Client = new HttpClient(new CdnHandler(new CdnFixture())),
            Logger = NullLogger<WabbajackCdnDownloadJob>.Instance,
            Url = new Uri("https://authored-files.wabbajack.org/Some%20Mod-123-1-0.7z_abc?key=secret"),
            Destination = destination,
            FileName = "Some Mod.7z",
        };

        job.DisplayName.Should().Be("Some Mod.7z");
        job.SourceName.Should().Be("Wabbajack CDN");
        job.PageUri.Should().Be(new Uri("https://authored-files.wabbajack.org/Some%20Mod-123-1-0.7z_abc"));
        job.Transfer.Should().BeNull();
    }

    [Fact]
    public async Task ACdnJobReportsHowManyBytesItHasWrittenOutOfHowMany()
    {
        var fixture = new CdnFixture();
        await using var destination = Files.CreateFile();
        var job = new WabbajackCdnDownloadJob
        {
            Client = new HttpClient(new CdnHandler(fixture)),
            Logger = NullLogger<WabbajackCdnDownloadJob>.Instance,
            Url = new Uri("https://authored-files.wabbajack.org/Some%20Mod-123-1-0.7z_abc"),
            Destination = destination,
            RetryDelay = TimeSpan.FromMilliseconds(1),
        };

        await Begin(job);

        var state = job.GetJobStateData().Should().BeAssignableTo<IHttpDownloadState>().Subject;
        state.ContentLength.Value.Value.Should().Be((ulong)fixture.Content.Length);
        state.TotalBytesDownloaded.Value.Should().Be((ulong)fixture.Content.Length);
    }

    [Fact]
    public async Task ACdnJobWithManyPartsNeverReportsFewerBytesThanItDidBefore()
    {
        for (var run = 0; run < 20; run++)
        {
            var fixture = new CdnFixture(length: CdnFixture.PartSize * 200);
            await using var destination = Files.CreateFile();
            var job = new WabbajackCdnDownloadJob
            {
                Client = new HttpClient(new CdnHandler(fixture)),
                Logger = NullLogger<WabbajackCdnDownloadJob>.Instance,
                Url = new Uri("https://authored-files.wabbajack.org/Some%20Mod-123-1-0.7z_abc"),
                Destination = destination,
                RetryDelay = TimeSpan.FromMilliseconds(1),
            };
            var seen = new List<ulong>();
            job.State.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(job.State.TotalBytesDownloaded)) lock (seen) seen.Add(job.State.TotalBytesDownloaded.Value);
            };

            await Begin(job);

            seen.Should().BeInAscendingOrder("parts finish in any order but the count only grows");
            job.State.TotalBytesDownloaded.Value.Should().Be((ulong)fixture.Content.Length);
        }
    }

    [Fact]
    public async Task ACdnJobWithoutAFileNameIsNamedAfterItsDestination()
    {
        await using var destination = Files.CreateFile();
        ITrackedDownloadJob job = new WabbajackCdnDownloadJob
        {
            Client = new HttpClient(new CdnHandler(new CdnFixture())),
            Logger = NullLogger<WabbajackCdnDownloadJob>.Instance,
            Url = new Uri("https://authored-files.wabbajack.org/list.wabbajack_abc"),
            Destination = destination,
        };

        job.DisplayName.Should().Be(destination.Path.FileName);
    }
}
