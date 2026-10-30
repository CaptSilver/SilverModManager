using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Acquire.Hosts;

namespace SilverModManager.Wabbajack.Tests.Acquire.Hosts;

public class ModDbDownloadJobTests(IServiceProvider provider, TemporaryFileManager files) : HostedJobTestBase(provider, files)
{
    private static readonly Uri AddonPage = new("https://www.moddb.com/mods/cool-mod/addons/cool-patch");
    private static readonly Uri StartPage = new("https://www.moddb.com/downloads/start/123456");
    private static readonly Uri Mirror = new("https://www.moddb.com/downloads/mirror/123456/118/0123abcd/?referer=x");

    private ModDbDownloadJob JobFor(FakeHost host, AbsolutePath destination, Uri? url = null) => new()
    {
        Client = new HttpClient(host),
        Logger = NullLogger<ModDbDownloadJob>.Instance,
        Throttle = new HostThrottle(),
        Url = url ?? AddonPage,
        Destination = destination,
    };

    private static string Page(string body) => $"<html><body>{body}</body></html>";

    private static FakeHost Site(byte[] file, string addon, string start = "", string? mirrorAnswer = null) => new((request, _) =>
    {
        if (request.Uri == AddonPage) return FakeHost.Html(Page(addon));
        if (request.Uri == StartPage) return FakeHost.Html(Page(start));
        if (request.Uri.AbsolutePath.StartsWith("/downloads/mirror/", StringComparison.Ordinal)) return mirrorAnswer is null ? FakeHost.Bytes(file) : FakeHost.Html(mirrorAnswer);
        return FakeHost.Html("not found", System.Net.HttpStatusCode.NotFound);
    });

    [Fact]
    public async Task TheMirrorLinkOnTheAddonPageIsFollowedToTheFile()
    {
        var plain = Content(40_000);
        var host = Site(plain, $"<a id=\"downloadmirrorstoggle\" href=\"{Mirror}\">Download</a>");
        await using var destination = Files.CreateFile();

        await Begin(JobFor(host, destination));

        host.Seen.Select(r => r.Uri).Should().Equal(AddonPage, Mirror);
        (await destination.Path.ReadAllBytesAsync()).Should().Equal(plain);
    }

    [Fact]
    public async Task ADownloadPageIsFollowedToItsMirrorWhenTheAddonPageOnlyLinksThere()
    {
        var plain = Content(900);
        var host = Site(plain, "<a href=\"/downloads/start/123456\">Download now</a>", $"<a href=\"/downloads/mirror/123456/118/0123abcd/?referer=x\">Mirror</a>");
        await using var destination = Files.CreateFile();

        await Begin(JobFor(host, destination));

        host.Seen.Select(r => r.Uri).Should().Equal(AddonPage, StartPage, Mirror);
        (await destination.Path.ReadAllBytesAsync()).Should().Equal(plain);
    }

    [Fact]
    public async Task AMirrorAddressIsFetchedWithoutReadingAPageFirst()
    {
        var plain = Content(10);
        var host = Site(plain, "");
        await using var destination = Files.CreateFile();

        await Begin(JobFor(host, destination, Mirror));

        host.Seen.Select(r => r.Uri).Should().Equal(Mirror);
    }

    [Fact]
    public async Task TheMirrorIsAskedForWithThePageItWasFoundOnAsReferer()
    {
        var host = Site(Content(10), $"<a href=\"{Mirror}\">Download</a>");
        await using var destination = Files.CreateFile();

        await Begin(JobFor(host, destination));

        host.Seen[1].Headers["Referer"].Should().Be(AddonPage.ToString());
    }

    [Fact]
    public async Task APageWithNoMirrorLinkAsksForTheBrowser()
    {
        var host = Site([], "<p>This file has been removed</p>");
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(JobFor(host, destination));

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("mirror").And.Contain("browser");
    }

    [Fact]
    public async Task AMirrorOnAnotherSiteIsNotFollowed()
    {
        var host = Site(Content(10), "<a href=\"https://evil.test/downloads/mirror/1/2/3/\">Download</a>");
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(JobFor(host, destination));

        await act.Should().ThrowAsync<Exception>();
        host.Seen.Should().ContainSingle();
    }

    [Fact]
    public async Task AMirrorThatAnswersWithAPageFailsAskingForTheBrowser()
    {
        var host = Site([], $"<a href=\"{Mirror}\">Download</a>", mirrorAnswer: "<html>Please wait</html>");
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(JobFor(host, destination));

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("web page").And.Contain("browser");
        destination.Path.FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task ThePageChainStopsAfterTwoHops()
    {
        var host = new FakeHost((request, _) => FakeHost.Html(Page("<a href=\"/downloads/start/9\">again</a>")));
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(JobFor(host, destination));

        await act.Should().ThrowAsync<Exception>();
        host.Seen.Count.Should().BeLessThanOrEqualTo(3);
    }

    [Fact(Timeout = 60000)]
    public async Task AnAddonPageThatGoesQuietAfterItsHeadersFailsNamingTheHost()
    {
        var host = new FakeHost((_, _) => FakeHost.SilentBody("text/html"));
        await using var destination = Files.CreateFile();

        var act = async () => await Begin(JobFor(host, destination) with { IdleTimeout = TimeSpan.FromMilliseconds(150) });

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Be("ModDB stopped sending data");
    }
}
