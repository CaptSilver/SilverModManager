using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Networking.HttpDownloader;
using SilverModManager.Networking.ModIo.Tests.Fixtures;

namespace SilverModManager.Networking.ModIo.Tests;

public class ModIoApiTests
{
    private const string Root = "/v1/games/6715";

    private static async Task<ModIoApi> Api(HttpMessageHandler handler, string? token = "tok-1", FakeTimeProvider? time = null)
    {
        var store = new InMemorySecretStore();
        if (token is not null) await store.SetAsync(ModIoSecretKeys.Token, token, CancellationToken.None);
        return new ModIoApi(new HttpClient(handler), store, new HostThrottle(), time ?? new FakeTimeProvider(), ModIoSettings.Default);
    }

    [Fact]
    public async Task ModsParsesCardsAndTheLatestModfile()
    {
        var handler = new RecordingHandler().Add(HttpMethod.Get, $"{Root}/mods", ModIoJson.Mods);

        var page = await (await Api(handler)).Mods(6715, new ModIoQuery("camera", "-downloads_total", 0, 30), CancellationToken.None);

        page.Total.Should().Be(61);
        page.Offset.Should().Be(0);
        page.Items.Select(mod => mod.Id).Should().Equal(4101u, 4102u);
        var mod = page.Items[0];
        mod.Name.Should().Be("Wider Camera");
        mod.Summary.Should().Be("Pulls the camera back.");
        mod.ProfileUrl.Should().Be("https://mod.io/g/baldursgate3/m/wider-camera");
        mod.Logo!.Thumb320x180.Should().Be("https://thumb.modcdn.io/mods/a/crop_320x180/logo.png");
        mod.DateUpdated.Should().Be(1710000500);
        mod.Stats!.DownloadsTotal.Should().Be(12345);
        mod.SubmittedBy!.Username.Should().Be("Camille");
        mod.Modfile!.Id.Should().Be(88001);
        mod.Modfile.FileHash!.Md5.Should().Be("180f264ed68955adf16924b9a7c29fd6");
        mod.Modfile.FileSize.Should().Be(2048);
        mod.Modfile.FileName.Should().Be("wider-camera.zip");
        mod.Modfile.Version.Should().Be("1.2.0");
        mod.Modfile.Download!.BinaryUrl.Should().EndWith("wider-camera.zip");
        mod.Modfile.IsClean.Should().BeTrue();
        page.Items[1].Modfile.Should().BeNull("a mod nobody uploaded a file for has none");
        page.Items[1].MaturityOption.Should().Be(4);

        var query = System.Web.HttpUtility.ParseQueryString(handler.Requests.Single().Uri.Query);
        query["_q"].Should().Be("camera");
        query["_sort"].Should().Be("-downloads_total");
        query["_limit"].Should().Be("30");
        query["_offset"].Should().Be("0");
    }

    [Fact]
    public async Task FilesListsEveryModfileOfAMod()
    {
        var handler = new RecordingHandler().Add(HttpMethod.Get, $"{Root}/mods/4101/files", ModIoJson.Files);

        var files = await (await Api(handler)).Files(6715, 4101, CancellationToken.None);

        files.Select(file => file.Id).Should().Equal(88001u, 87000u);
        files[1].DateAdded.Should().Be(1700000500);
    }

    [Fact]
    public async Task SubscribeSendsIncludeDependencies()
    {
        var handler = new RecordingHandler().Add(HttpMethod.Post, $"{Root}/mods/4101/subscribe", ModIoJson.Subscribed, HttpStatusCode.Created);

        await (await Api(handler)).Subscribe(6715, 4101, includeDependencies: true, CancellationToken.None);

        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.Body.Should().Be("include_dependencies=true");
    }

    [Fact]
    public async Task UnsubscribeIsADelete()
    {
        var handler = new RecordingHandler().Add(HttpMethod.Delete, $"{Root}/mods/4101/subscribe", "", HttpStatusCode.NoContent);

        await (await Api(handler)).Unsubscribe(6715, 4101, CancellationToken.None);

        handler.Requests.Single().Method.Should().Be(HttpMethod.Delete);
    }

    [Fact]
    public async Task AnAlreadySubscribedModIsNotAFailure()
    {
        // mod.io answers 200 with the mod rather than 201 when the user was already subscribed.
        var handler = new RecordingHandler().Add(HttpMethod.Post, $"{Root}/mods/4101/subscribe", ModIoJson.Subscribed);

        var act = async () => await (await Api(handler)).Subscribe(6715, 4101, includeDependencies: false, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A403OnSubscribeReadsAsAReadOnlyToken()
    {
        var handler = new RecordingHandler().Add(HttpMethod.Post, $"{Root}/mods/4101/subscribe", ModIoJson.ReadOnlyToken, HttpStatusCode.Forbidden);

        var act = async () => await (await Api(handler)).Subscribe(6715, 4101, true, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<ModIoRefusedException>();
        thrown.Which.Message.Should().Contain("write scope").And.Contain("mod.io/me/access");
    }

    [Fact]
    public async Task A403ForTheTermsPointsAtTheTerms()
    {
        var handler = new RecordingHandler().Add(HttpMethod.Post, $"{Root}/mods/4101/subscribe", ModIoJson.TermsNotAccepted, HttpStatusCode.Forbidden);

        var act = async () => await (await Api(handler)).Subscribe(6715, 4101, true, CancellationToken.None);

        (await act.Should().ThrowAsync<ModIoTermsNotAcceptedException>()).Which.Message.Should().Contain("docs.mod.io/terms");
    }

    [Fact]
    public async Task A429StopsWithRetryAfter()
    {
        var handler = new RecordingHandler().Add(HttpMethod.Get, $"{Root}/mods", """{"error":{"code":429,"error_ref":11009,"message":"slow down"}}""", HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(42));
        var api = await Api(handler);

        var act = async () => await api.Mods(6715, new ModIoQuery("", "-date_updated", 0, 30), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<ModIoRateLimitedException>();
        ((IRetryAfter)thrown.Which).RetryAfter.Should().Be(TimeSpan.FromSeconds(42));
        handler.Requests.Should().HaveCount(1, "continuing after a 429 can get the credentials revoked");
    }

    [Fact]
    public async Task TheBearerHeaderCarriesTheStoredToken()
    {
        var handler = new RecordingHandler().Add(HttpMethod.Get, $"{Root}/mods", ModIoJson.Mods);

        await (await Api(handler, "secret-token")).Mods(6715, new ModIoQuery("", "-date_updated", 0, 30), CancellationToken.None);

        handler.Requests.Single().Authorization.Should().Be("Bearer secret-token");
        handler.Requests.Single().Uri.Query.Should().NotContain("secret-token").And.NotContain("api_key");
    }

    [Fact]
    public async Task WithoutATokenNothingIsSentAndTheRowIsNamed()
    {
        var handler = new RecordingHandler();

        var act = async () => await (await Api(handler, token: null)).Mods(6715, new ModIoQuery("", "-date_updated", 0, 30), CancellationToken.None);

        (await act.Should().ThrowAsync<ModIoNotSignedInException>()).Which.Message.Should().Contain("mod.io");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ARejectedTokenIsNamedAsSuch()
    {
        var handler = new RecordingHandler().Add(HttpMethod.Get, $"{Root}/mods", """{"error":{"code":401,"error_ref":11005,"message":"expired"}}""", HttpStatusCode.Unauthorized);

        var act = async () => await (await Api(handler)).Mods(6715, new ModIoQuery("", "-date_updated", 0, 30), CancellationToken.None);

        await act.Should().ThrowAsync<ModIoTokenRejectedException>();
    }

    [Fact]
    public async Task MeReadsTheUserOfACandidateToken()
    {
        var handler = new RecordingHandler().Add(HttpMethod.Get, "/v1/me", ModIoJson.Me);

        var user = await (await Api(handler, token: null)).Me("candidate", CancellationToken.None);

        user.Username.Should().Be("Camille");
        handler.Requests.Single().Authorization.Should().Be("Bearer candidate");
    }

    [Fact]
    public async Task ListingsAreKeptForTheRefreshAge()
    {
        var handler = new RecordingHandler().Add(HttpMethod.Get, $"{Root}/mods", ModIoJson.Mods);
        var time = new FakeTimeProvider();
        var api = await Api(handler, time: time);
        var query = new ModIoQuery("", "-date_updated", 0, 30);

        await api.Mods(6715, query, CancellationToken.None);
        await api.Mods(6715, query, CancellationToken.None);
        handler.Requests.Should().HaveCount(1);

        time.Advance(TimeSpan.FromHours(2));
        await api.Mods(6715, query, CancellationToken.None);
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task SubscribedListsTheAccountsSubscriptionsOfOneGame()
    {
        var handler = new RecordingHandler().Add(HttpMethod.Get, "/v1/me/subscribed", Listing(total: 2, offset: 0, """{ "id": 4101, "name": "Wider Camera" }""", """{ "id": 4102, "name": "Quiet Camp" }"""));

        var mods = await (await Api(handler)).Subscribed(6715, CancellationToken.None);

        mods.Select(mod => mod.Id).Should().Equal(4101u, 4102u);
        var query = System.Web.HttpUtility.ParseQueryString(handler.Requests.Single().Uri.Query);
        query["game_id"].Should().Be("6715");
    }

    [Fact]
    public async Task SubscribedReadsEveryPageOfALongList()
    {
        // mod.io hands out at most 100 a page; a lock built from the first page alone would quietly miss mods.
        var handler = new RecordingHandler().Add(HttpMethod.Get, "/v1/me/subscribed", uri => PageOfMods(uri, total: 150));

        var mods = await (await Api(handler)).Subscribed(6715, CancellationToken.None);

        mods.Select(mod => mod.Id).Should().Equal(Enumerable.Range(1, 150).Select(id => (uint)id));
        handler.Requests.Select(request => System.Web.HttpUtility.ParseQueryString(request.Uri.Query)["_offset"]).Should().Equal("0", "100");
    }

    [Fact]
    public async Task FilesReadsEveryPageOfAModWithManyUploads()
    {
        var handler = new RecordingHandler().Add(HttpMethod.Get, $"{Root}/mods/4101/files", uri => PageOfFiles(uri, total: 120));

        var files = await (await Api(handler)).Files(6715, 4101, CancellationToken.None);

        files.Select(file => file.Id).Should().Equal(Enumerable.Range(1, 120).Select(id => (uint)id));
    }

    private static string Listing(int total, int offset, params string[] items)
        => $$"""{ "data": [{{string.Join(", ", items)}}], "result_offset": {{offset}}, "result_total": {{total}} }""";

    private static int OffsetOf(Uri uri) => int.Parse(System.Web.HttpUtility.ParseQueryString(uri.Query)["_offset"] ?? "0", System.Globalization.CultureInfo.InvariantCulture);

    private static string PageOfMods(Uri uri, int total)
    {
        var offset = OffsetOf(uri);
        var ids = Enumerable.Range(offset + 1, Math.Max(0, Math.Min(100, total - offset)));
        return Listing(total, offset, [.. ids.Select(id => $$"""{ "id": {{id}}, "name": "Mod {{id}}" }""")]);
    }

    private static string PageOfFiles(Uri uri, int total)
    {
        var offset = OffsetOf(uri);
        var ids = Enumerable.Range(offset + 1, Math.Max(0, Math.Min(100, total - offset)));
        return Listing(total, offset, [.. ids.Select(id => $$"""{ "id": {{id}}, "mod_id": 4101, "filename": "f{{id}}.zip" }""")]);
    }
}
