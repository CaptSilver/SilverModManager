using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Library;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Acquire.Hosts;

namespace SilverModManager.Wabbajack.Tests.Acquire.Hosts;

public class GoogleDriveDownloadJobTests(IServiceProvider provider, TemporaryFileManager files) : HostedJobTestBase(provider, files)
{
    private const string FileId = "1AbC_dEf-123";
    private static readonly Uri Page = new($"https://drive.google.com/uc?id={FileId}&export=download");

    private const string Interstitial = """
        <html><body><p>Google Drive can't scan this file for viruses.</p>
        <form id="download-form" action="https://drive.usercontent.google.com/download" method="get">
        <input type="submit" id="uc-download-link" value="Download anyway">
        <input type="hidden" name="id" value="1AbC_dEf-123">
        <input type="hidden" name="export" value="download">
        <input type="hidden" name="authuser" value="0">
        <input type="hidden" name="confirm" value="t">
        <input type="hidden" name="uuid" value="9f1c-uuid">
        <input type="hidden" name="at" value="TOKEN&amp;1">
        </form></body></html>
        """;

    private GoogleDriveDownloadJob JobFor(FakeHost host, AbsolutePath destination) => new()
    {
        Client = new HttpClient(host),
        Logger = NullLogger<GoogleDriveDownloadJob>.Instance,
        Throttle = new HostThrottle(),
        Url = Page,
        FileId = FileId,
        Destination = destination,
        RetryDelay = TimeSpan.FromMilliseconds(1),
    };

    [Fact]
    public async Task TheFileIsRequestedFromTheDownloadHostWithTheConfirmFlagAndAddedToTheLibrary()
    {
        var plain = Content(70_000);
        var host = new FakeHost((_, _) => FakeHost.Bytes(plain));
        await using var destination = Files.CreateFile();

        var file = await Provider.GetRequiredService<ILibraryService>().AddDownload(Begin(JobFor(host, destination)));

        host.Seen.Should().ContainSingle().Which.Uri.ToString().Should().Be($"https://drive.usercontent.google.com/download?id={FileId}&export=download&confirm=t");
        (await destination.Path.ReadAllBytesAsync()).Should().Equal(plain);
        file.XxHash64Identity().Should().Be(Xx64.Of(plain));
        file.AsLibraryItem().TryGetAsDownloadedFile(out var downloaded).Should().BeTrue();
        downloaded.DownloadPageUri.Should().Be(Page);
    }

    [Fact]
    public async Task TheVirusScanPageIsAnsweredByItsOwnFormOnce()
    {
        var plain = Content(5000);
        var host = new FakeHost((request, earlier) => request.Uri.Query.Contains("uuid") ? FakeHost.Bytes(plain) : FakeHost.Html(Interstitial));
        await using var destination = Files.CreateFile();

        await Begin(JobFor(host, destination));

        host.Seen.Should().HaveCount(2);
        var second = host.Seen[1].Uri;
        second.GetLeftPart(UriPartial.Path).Should().Be("https://drive.usercontent.google.com/download");
        var query = System.Web.HttpUtility.ParseQueryString(second.Query);
        query["id"].Should().Be(FileId);
        query["confirm"].Should().Be("t");
        query["uuid"].Should().Be("9f1c-uuid");
        query["at"].Should().Be("TOKEN&1");
        (await destination.Path.ReadAllBytesAsync()).Should().Equal(plain);
    }

    [Fact]
    public async Task APageInsteadOfTheFileAfterTheFormFailsAskingForTheBrowser()
    {
        var host = new FakeHost((_, _) => FakeHost.Html(Interstitial));
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(JobFor(host, destination));

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("web page").And.Contain("browser");
        host.Seen.Should().HaveCount(2);
        destination.Path.FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task AFileOverItsDownloadLimitSaysSoRatherThanAWebPage()
    {
        var host = new FakeHost((_, _) => FakeHost.Html("<html><body>Sorry, you can't view or download this file at this time. Too many users have viewed or downloaded this file recently.</body></html>"));
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(JobFor(host, destination));

        (await act.Should().ThrowAsync<Exception>()).Which.Message
            .Should().Be("Google Drive's download limit for this file is used up; try later or download in your browser");
    }

    [Fact]
    public async Task AFileThatIsNotSharedPubliclySaysToSignInInTheBrowser()
    {
        var host = new FakeHost((_, _) => FakeHost.Html("""<html><body><a href="https://accounts.google.com/ServiceLogin?continue=x">Sign in</a></body></html>"""));
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(JobFor(host, destination));

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("not shared publicly").And.Contain("browser");
    }

    [Fact]
    public async Task AMissingFileFailsWithTheStatus()
    {
        var host = new FakeHost((_, _) => FakeHost.Html("not found", HttpStatusCode.NotFound));
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(JobFor(host, destination));

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("404");
    }

    [Fact]
    public async Task ABodyCutShortLeavesNoFileBehind()
    {
        var host = new FakeHost((_, _) =>
        {
            var response = FakeHost.Bytes(Content(100));
            response.Content.Headers.ContentLength = 100;
            return new HttpResponseMessage { Content = new TruncatedContent(100, 40) };
        });
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(JobFor(host, destination));

        await act.Should().ThrowAsync<Exception>();
        destination.Path.FileExists.Should().BeFalse();
    }

    /// <summary>Claims a length and delivers less, the way a dropped connection does.</summary>
    private sealed class TruncatedContent(long claimed, int delivered) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(new byte[delivered]).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = claimed;
            return true;
        }
    }

    [Fact(Timeout = 60000)]
    public async Task AScanPageThatGoesQuietAfterItsHeadersFailsNamingTheHost()
    {
        var host = new FakeHost((_, _) => FakeHost.SilentBody("text/html"));
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(JobFor(host, destination) with { IdleTimeout = TimeSpan.FromMilliseconds(150) });

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Be("Google Drive stopped sending data");
    }

    [Fact(Timeout = 60000)]
    public async Task AWarningPageThatGoesQuietAfterItsHeadersFailsNamingTheHost()
    {
        var host = new FakeHost((request, earlier) => earlier == 0 ? FakeHost.Html(Interstitial) : FakeHost.SilentBody("text/html"));
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(JobFor(host, destination) with { IdleTimeout = TimeSpan.FromMilliseconds(150) });

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Be("Google Drive stopped sending data");
    }
}
