using System.Net;
using FluentAssertions;
using Xunit;
using SilverModManager.Networking.Steam.Workshop;

namespace SilverModManager.Networking.Steam.Tests;

public class SteamWorkshopApiTests
{
    private const string Endpoint = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";

    private sealed class RecordingHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "GetPublishedFileDetails_2009463077.json"));

    private static SteamWorkshopApi Api(RecordingHandler handler) => new(new HttpClient(handler));

    [Fact]
    public async Task DetailsPostsEveryIdAndParsesTitleAndUpdatedTime()
    {
        var handler = new RecordingHandler(Fixture);

        var details = await Api(handler).Details([2009463077, 1], CancellationToken.None);

        var (request, body) = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri.Should().Be(new Uri(Endpoint));
        var form = System.Web.HttpUtility.ParseQueryString(body);
        form["itemcount"].Should().Be("2");
        form["publishedfileids[0]"].Should().Be("2009463077");
        form["publishedfileids[1]"].Should().Be("1");

        var item = details[0];
        item.Id.Should().Be(2009463077UL);
        item.Title.Should().Be("Harmony");
        item.TimeUpdated.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1792065891));
        item.FileSize.Should().Be(8306239);
        item.ConsumerAppId.Should().Be(294100u);
        item.PreviewUrl.Should().Be(new Uri("https://images.steamusercontent.com/ugc/779624067826598337/7EED02FFB4AE4DA8D029623C2C82F5D8F0FD77FF/"));
        item.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task ARemovedItemIsReportedByItsResultCode()
    {
        var details = await Api(new RecordingHandler(Fixture)).Details([2009463077, 1], CancellationToken.None);

        var gone = details.Single(item => item.Id == 1);
        gone.Result.Should().Be(9);
        gone.IsAvailable.Should().BeFalse();
        gone.Title.Should().BeNull();
    }

    [Fact]
    public async Task TheRequestCarriesNoKey()
    {
        var handler = new RecordingHandler(Fixture);

        await Api(handler).Details([2009463077], CancellationToken.None);

        var (request, body) = handler.Requests.Single();
        request.RequestUri!.Query.Should().BeEmpty();
        request.Headers.Authorization.Should().BeNull();
        body.Should().NotContainEquivalentOf("key");
    }

    [Fact]
    public async Task NoIdsMeansNoRequest()
    {
        var handler = new RecordingHandler(Fixture);

        (await Api(handler).Details([], CancellationToken.None)).Should().BeEmpty();

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ManyIdsGoInBatches()
    {
        var handler = new RecordingHandler("""{"response":{"result":1,"resultcount":0,"publishedfiledetails":[]}}""");

        await Api(handler).Details(Enumerable.Range(1, 250).Select(i => (ulong)i).ToArray(), CancellationToken.None);

        handler.Requests.Select(r => System.Web.HttpUtility.ParseQueryString(r.Body)["itemcount"]).Should().Equal("100", "100", "50");
    }

    [Fact]
    public async Task AReplyThatIsNotTheDetailsShapeIsAnUnreadableReply()
    {
        var act = () => Api(new RecordingHandler("<html>maintenance</html>")).Details([1], CancellationToken.None);

        await act.Should().ThrowAsync<SteamWorkshopReplyUnreadableException>();
    }

    [Fact]
    public async Task AServerErrorSaysSteamDidNotAnswer()
    {
        var act = () => Api(new RecordingHandler("", HttpStatusCode.ServiceUnavailable)).Details([1], CancellationToken.None);

        await act.Should().ThrowAsync<SteamWorkshopUnavailableException>();
    }
}
