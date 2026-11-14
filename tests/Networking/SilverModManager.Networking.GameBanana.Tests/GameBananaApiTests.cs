using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.Networking.GameBanana.Tests.Fixtures;
using SilverModManager.Networking.HttpDownloader;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Networking.GameBanana.Tests;

public class GameBananaApiTests
{
    private const string Root = "https://gamebanana.com/apiv11";

    private static GameBananaApi Api(HttpMessageHandler handler, FakeTimeProvider? time = null, HostThrottle? throttle = null)
        => new(new HttpClient(handler), throttle ?? new HostThrottle(), time ?? new FakeTimeProvider(), GameBananaSettings.Default);

    [Fact]
    public async Task IndexParsesTheRecordsAndTheirUpdateTimestamps()
    {
        var handler = new RoutingHandler().Add(
            $"{Root}/Mod/Index?_nPage=1&_nPerpage=3&_aFilters[Generic_Game]=297&_sSort=Generic_MostDownloaded", GameBananaJson.ModIndex);

        var listing = await Api(handler).Index(297, GameBananaSection.Mod, "Generic_MostDownloaded", 1, 3, CancellationToken.None);

        listing.RecordCount.Should().BeGreaterThan(3);
        listing.IsComplete.Should().BeFalse();
        listing.Records.Select(record => record.Id).Should().Equal(74812, 26852, 206285);
        var withStamp = listing.Records[2];
        withStamp.DateUpdated.Should().Be(1665167342);
        withStamp.DateModified.Should().Be(1665169541);
        withStamp.ProfileUrl.Should().Be("https://gamebanana.com/mods/206285");
        listing.Records[0].DateUpdated.Should().BeNull("a mod nobody updated carries no update stamp");
        listing.Records[0].Version.Should().Be("RC2");
        listing.Records[0].Submitter!.Name.Should().NotBeNullOrEmpty();
        listing.Records[0].PreviewMedia!.Thumbnail.Should().NotBeNull();
    }

    [Fact]
    public async Task TheSubfeedAndASearchAnswerTheSameShape()
    {
        var handler = new RoutingHandler()
            .Add($"{Root}/Game/297/Subfeed?_nPage=2&_nPerpage=3", GameBananaJson.Subfeed)
            .Add($"{Root}/Util/Search/Results?_sModelName=Mod&_sOrder=best_match&_idGameRow=297&_sSearchString=head%20hud&_nPage=1", GameBananaJson.Search);

        var subfeed = await Api(handler).Subfeed(297, 2, 3, CancellationToken.None);
        var search = await Api(handler).Search(297, "head hud", 1, CancellationToken.None);

        subfeed.Records.Should().HaveCount(3).And.OnlyContain(record => record.ModelName == "Mod");
        search.Records.Should().HaveCount(3).And.OnlyContain(record => record.Name.Length > 0);
    }

    [Fact]
    public async Task AProfileListsFilesWithMd5SizeAndDownloadUrl()
    {
        var handler = new RoutingHandler().Add($"{Root}/Mod/44334/ProfilePage", GameBananaJson.Profile44334);

        var profile = await Api(handler).Profile(44334, CancellationToken.None);

        profile.DateModified.Should().Be(1641411766);
        profile.DateUpdated.Should().Be(1622678310);
        profile.Game!.Id.Should().BeGreaterThan(0);
        var file = profile.AllFiles.Should().ContainSingle().Subject;
        file.Id.Should().Be(583960);
        file.Md5Checksum.Should().Be("180f264ed68955adf16924b9a7c29fd6");
        file.Size.Should().BeGreaterThan(0);
        file.DownloadUrl.Should().Be("https://gamebanana.com/dl/583960");
        file.IsClean.Should().BeTrue();
        profile.AlternateFileSources.Should().NotBeEmpty("the page lists the author's own mirrors");
        profile.LicenseChecklist!.ForbidsInstalling.Should().BeFalse();
    }

    [Fact]
    public async Task AProfileWithoutAnalysisWarningsParses()
    {
        var handler = new RoutingHandler().Add($"{Root}/Mod/206285/ProfilePage", GameBananaJson.Profile206285);

        var profile = await Api(handler).Profile(206285, CancellationToken.None);

        profile.AllFiles.Should().HaveCount(2);
        profile.AllFiles.Should().OnlyContain(file => file.WarningLines.Count == 0);
    }

    [Fact]
    public async Task APrivateModIsReportedUnavailable()
    {
        var handler = new RoutingHandler().Add($"{Root}/Mod/572595/ProfilePage", GameBananaJson.PrivateStub);

        var act = () => Api(handler).Profile(572595, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<GameBananaUnavailableException>()).Which;
        thrown.ModId.Should().Be(572595);
        thrown.Message.Should().Contain("Black Mesa Handpads");
    }

    [Fact]
    public async Task AModTheSiteDoesNotKnowIsReportedUnavailable()
    {
        var handler = new RoutingHandler().Add($"{Root}/Mod/99999999/ProfilePage", """{"_sErrorCode":"NO_SUCH_RECORD"}""", HttpStatusCode.NotFound);

        var act = () => Api(handler).Profile(99999999, CancellationToken.None);

        await act.Should().ThrowAsync<GameBananaUnavailableException>();
    }

    [Fact]
    public async Task AnalysisWarningsAreParsed()
    {
        var handler = new RoutingHandler().Add($"{Root}/Mod/44334/ProfilePage", GameBananaJson.Profile44334);

        var file = (await Api(handler).Profile(44334, CancellationToken.None)).AllFiles.Single();

        file.AnalysisWarnings!["contains_exe"].Should().NotBeEmpty();
        file.WarningLines.Should().ContainSingle().Which.Should().StartWith("contains_exe:");
    }

    [Fact]
    public async Task AnEmptyWarningListIsAnEmptyObjectOrAnArrayAlike()
    {
        // The site writes an empty object as [], which a plain dictionary type cannot read.
        var handler = new RoutingHandler().Add($"{Root}/Mod/1/ProfilePage",
            """{"_idRow":1,"_sName":"m","_aFiles":[{"_idRow":9,"_sFile":"a.zip","_aAnalysisWarnings":[]}],"_aLicenseChecklist":[]}""");

        var profile = await Api(handler).Profile(1, CancellationToken.None);

        profile.AllFiles.Single().WarningLines.Should().BeEmpty();
        profile.LicenseChecklist.Should().BeNull();
    }

    [Fact]
    public async Task A429WithRetryAfterStopsAndSurfacesTheWait()
    {
        var handler = new RoutingHandler().Add($"{Root}/Mod/1/ProfilePage", "{}", HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(90));
        var api = Api(handler);

        var act = () => api.Profile(1, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<GameBananaRateLimitedException>()).Which;
        ((IRetryAfter)thrown).RetryAfter.Should().Be(TimeSpan.FromSeconds(90));
        handler.Requests.Should().HaveCount(1, "the client does not ask again on its own");
    }

    [Fact]
    public async Task AnUnreadableReplyIsAUserFacingFailure()
    {
        var handler = new RoutingHandler().Add($"{Root}/Mod/1/ProfilePage", "<html>maintenance</html>");

        var act = () => Api(handler).Profile(1, CancellationToken.None);

        await act.Should().ThrowAsync<GameBananaReplyUnreadableException>();
    }

    [Fact]
    public async Task ARepeatedListingWithinTheHourIsNotAskedAgain()
    {
        var time = new FakeTimeProvider();
        var url = $"{Root}/Game/297/Subfeed?_nPage=1&_nPerpage=3";
        var handler = new RoutingHandler().Add(url, GameBananaJson.Subfeed);
        var api = Api(handler, time);

        await api.Subfeed(297, 1, 3, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(59));
        await api.Subfeed(297, 1, 3, CancellationToken.None);
        handler.Requests.Should().HaveCount(1);

        time.Advance(TimeSpan.FromMinutes(2));
        await api.Subfeed(297, 1, 3, CancellationToken.None);
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task RequestsTakeAThrottleSlot()
    {
        var gate = new GatedHandler();
        var api = Api(gate);

        var requests = Enumerable.Range(1, 3).Select(id => api.Profile(id, CancellationToken.None)).ToArray();
        await gate.WaitForInFlight(2);
        await Task.Delay(50);

        gate.InFlight.Should().Be(2, "a third request waits for a slot");
        gate.MaxInFlight.Should().Be(HostThrottle.PerHost);

        gate.Release();
        await Task.WhenAll(requests);
        gate.MaxInFlight.Should().Be(HostThrottle.PerHost);
    }

    private sealed class GatedHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inFlight;
        private int _max;

        public int InFlight => Volatile.Read(ref _inFlight);
        public int MaxInFlight => Volatile.Read(ref _max);

        public void Release() => _open.SetResult();

        public async Task WaitForInFlight(int count)
        {
            while (InFlight < count) await Task.Delay(5);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while ((seen = Volatile.Read(ref _max)) < now && Interlocked.CompareExchange(ref _max, now, seen) != seen) { }
            try
            {
                await _open.Task;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"_idRow":1,"_sName":"m"}"""), RequestMessage = request };
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}
