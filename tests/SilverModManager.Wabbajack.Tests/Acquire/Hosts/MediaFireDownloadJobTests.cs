using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Acquire.Hosts;

namespace SilverModManager.Wabbajack.Tests.Acquire.Hosts;

public class MediaFireDownloadJobTests(IServiceProvider provider, TemporaryFileManager files) : HostedJobTestBase(provider, files)
{
    private static readonly Uri Page = new("https://www.mediafire.com/file/abc123/Cool_Mod.7z/file");
    private const string Direct = "https://download1234.mediafire.com/xyz/abc123/Cool_Mod.7z";

    private MediaFireDownloadJob JobFor(FakeHost host, AbsolutePath destination) => new()
    {
        Client = new HttpClient(host),
        Logger = NullLogger<MediaFireDownloadJob>.Instance,
        Throttle = new HostThrottle(),
        Url = Page,
        Destination = destination,
    };

    private static string PageWith(string anchor) => $"<html><body><div class=\"dl\">{anchor}</div></body></html>";

    private static FakeHost Serving(string html, byte[] file) => new((request, _) => request.Uri == Page ? FakeHost.Html(html) : FakeHost.Bytes(file));

    [Fact]
    public async Task TheDownloadButtonsLinkIsFetchedAndSaved()
    {
        var plain = Content(50_000);
        var host = Serving(PageWith($"<a class=\"input popsok\" aria-label=\"Download file\" href=\"{Direct}\" id=\"downloadButton\">Download</a>"), plain);
        await using var destination = Files.CreateFile();

        await Begin(JobFor(host, destination));

        host.Seen.Select(r => r.Uri.ToString()).Should().Equal(Page.ToString(), Direct);
        (await destination.Path.ReadAllBytesAsync()).Should().Equal(plain);
    }

    [Fact]
    public async Task AScrambledLinkIsDecodedFromTheButton()
    {
        var plain = Content(1000);
        var scrambled = Convert.ToBase64String(Encoding.UTF8.GetBytes(Direct));
        var host = Serving(PageWith($"<a id=\"downloadButton\" href=\"#\" data-scrambled-url=\"{scrambled}\">Download</a>"), plain);
        await using var destination = Files.CreateFile();

        await Begin(JobFor(host, destination));

        host.Seen[1].Uri.ToString().Should().Be(Direct);
    }

    [Fact]
    public async Task AnAddressWithEscapedAmpersandsIsRequestedUnescaped()
    {
        var host = Serving(PageWith("<a id=\"downloadButton\" href=\"https://download1.mediafire.com/f?a=1&amp;b=2\">Download</a>"), Content(10));
        await using var destination = Files.CreateFile();

        await Begin(JobFor(host, destination));

        host.Seen[1].Uri.Query.Should().Be("?a=1&b=2");
    }

    [Fact]
    public async Task AFileTheHostServesDirectlyIsSavedWithoutLookingForAButton()
    {
        var plain = Content(300);
        var host = new FakeHost((_, _) => FakeHost.Bytes(plain));
        await using var destination = Files.CreateFile();

        await Begin(JobFor(host, destination));

        host.Seen.Should().ContainSingle();
        (await destination.Path.ReadAllBytesAsync()).Should().Equal(plain);
    }

    [Fact]
    public async Task ARemovedFileIsReportedAsRemoved()
    {
        var host = Serving("<html><body><div class=\"error\">Invalid or Deleted File.</div></body></html>", []);
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(JobFor(host, destination));

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("removed").And.Contain("MediaFire");
    }

    [Fact]
    public async Task AnUnrecognisedPageAsksForTheBrowser()
    {
        var host = Serving("<html><body>something new</body></html>", []);
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(JobFor(host, destination));

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("download link").And.Contain("browser");
        host.Seen.Should().ContainSingle();
    }

    [Fact]
    public async Task ALinkToAnotherSiteIsNotFollowed()
    {
        var host = Serving(PageWith("<a id=\"downloadButton\" href=\"https://evil.test/payload.exe\">Download</a>"), Content(10));
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(JobFor(host, destination));

        await act.Should().ThrowAsync<Exception>();
        host.Seen.Should().ContainSingle();
    }

    [Fact]
    public async Task ALinkWithoutHttpsIsNotFollowed()
    {
        var host = Serving(PageWith("<a id=\"downloadButton\" href=\"http://download1.mediafire.com/f\">Download</a>"), Content(10));
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(JobFor(host, destination));

        await act.Should().ThrowAsync<Exception>();
        host.Seen.Should().ContainSingle();
    }
}
