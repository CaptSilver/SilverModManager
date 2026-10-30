using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Networking.Forums.Auth;
using SilverModManager.Networking.Forums.Downloads;
using SilverModManager.Sdk;

namespace SilverModManager.Networking.Forums.Tests;

public class ForumFilesClientTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private const string Listing = """{"files":[{"name":"mod.7z","url":"https://files.test/get/abc?key=k","size":1234},{"name":"readme.txt","url":"https://files.test/get/r","size":5}]}""";

    private readonly FakeTimeProvider _time = new(Start);
    private readonly StubHandler _forum = new();
    private readonly InMemorySecretStore _inner = new();
    private readonly ForumCredentials _credentials;

    public ForumFilesClientTests()
    {
        // The silent store hides writes from the cache, the way another process's refresh would be hidden.
        _credentials = new ForumCredentials(new SilentSecretStore(_inner), NullLoggerFactory.Instance);
    }

    public void Dispose() => _credentials.Dispose();

    private async Task<ForumFilesClient> Create(bool attemptsOne = false)
    {
        await _credentials.PrimeAsync(CancellationToken.None);
        var oauth = new ForumOAuth(new HttpClient(_forum), _credentials, site => ForumEndpoints.For(site) with { ClientId = "client" }, _time, NullLogger<ForumOAuth>.Instance);
        return new ForumFilesClient(new HttpClient(_forum), oauth, _credentials, _time, NullLogger<ForumFilesClient>.Instance)
        {
            Attempts = attemptsOne ? 1 : 4,
            RetryDelay = TimeSpan.Zero,
        };
    }

    private Task Store(OAuthToken token, ForumSite site = ForumSite.LoversLab)
        => _credentials.For(site).SetTokenAsync(token, CancellationToken.None).AsTask();

    private static OAuthToken Valid(string access = "A") => new(access, "refresh", Start + TimeSpan.FromHours(1));

    private static string TokenReply(string access = "new-access") => $$"""{"access_token":"{{access}}","refresh_token":"new-refresh","expires_in":3600}""";

    private IEnumerable<RecordedRequest> FilesRequests => _forum.Requests.Where(r => r.Uri.AbsolutePath.StartsWith("/api/downloads/files/"));

    [Fact]
    public async Task TheListingRequestCarriesTheBearerTokenAndTheAppsUserAgent()
    {
        _forum.Reply(HttpStatusCode.OK, Listing);
        var client = await Create();
        await Store(Valid());

        var listing = await client.GetFiles(ForumSite.LoversLab, 42, CancellationToken.None);

        var request = _forum.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Uri.Should().Be(new Uri("https://api.loverslab.com/api/downloads/files/42"));
        request.Authorization.Should().Be("Bearer A");
        request.UserAgent.Should().Contain(ApplicationConstants.UserAgent.ApplicationName);
        listing.Files.Select(f => (f.Name, f.Size)).Should().Equal(("mod.7z", 1234L), ("readme.txt", 5L));
    }

    [Fact]
    public async Task ASignedOutSiteIsToldSoWithoutAnyRequest()
    {
        var client = await Create();

        await client.Invoking(c => c.GetFiles(ForumSite.LoversLab, 42, CancellationToken.None)).Should().ThrowAsync<NotSignedInException>();

        _forum.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A401ReloadsTheStoredTokenAndRetriesOnceWithIt()
    {
        _forum.ReplyWhen(r => r.Authorization == "Bearer A", HttpStatusCode.Unauthorized, "{}", Task.CompletedTask);
        _forum.Reply(HttpStatusCode.OK, Listing);
        var client = await Create();
        await Store(Valid("A"));
        await _inner.SetAsync(ForumSecretKeys.LoversLab, Valid("B").ToJson(), CancellationToken.None);

        var listing = await client.GetFiles(ForumSite.LoversLab, 42, CancellationToken.None);

        listing.Files.Should().HaveCount(2);
        FilesRequests.Select(r => r.Authorization).Should().Equal("Bearer A", "Bearer B");
    }

    [Fact]
    public async Task ASecond401SignsTheUserOutAndAsksThemToSignInAgain()
    {
        _forum.Reply(HttpStatusCode.Unauthorized, "{}");
        var client = await Create();
        await Store(Valid());

        await client.Invoking(c => c.GetFiles(ForumSite.LoversLab, 42, CancellationToken.None)).Should()
            .ThrowAsync<SignInAgainException>().WithMessage("Your LoversLab session expired. Sign in again, then download again.");

        FilesRequests.Should().HaveCount(2);
        (await _credentials.For(ForumSite.LoversLab).GetTokenAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task A403IsReportedOnceWithoutRetries()
    {
        _forum.Reply(HttpStatusCode.Forbidden, "{}");
        var client = await Create();
        await Store(Valid());

        await client.Invoking(c => c.GetFiles(ForumSite.LoversLab, 42, CancellationToken.None)).Should()
            .ThrowAsync<ForumRefusedException>().WithMessage("LoversLab refused the request (403). Download it from the page.");

        FilesRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task A429WaitsForRetryAfterThenTriesAgain()
    {
        _forum.ReplyOnce(HttpStatusCode.TooManyRequests, "{}", retryAfterSeconds: 30);
        _forum.Reply(HttpStatusCode.OK, Listing);
        var client = await Create();
        await Store(Valid());

        var lookup = client.GetFiles(ForumSite.LoversLab, 42, CancellationToken.None);
        await WaitFor(() => FilesRequests.Count() == 1);
        await Task.Delay(100);
        _time.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(100);
        FilesRequests.Should().ContainSingle("the forum asked for thirty seconds");

        _time.Advance(TimeSpan.FromSeconds(2));
        var listing = await lookup.WaitAsync(TimeSpan.FromSeconds(10));

        listing.Files.Should().HaveCount(2);
        FilesRequests.Should().HaveCount(2);
    }

    [Fact]
    public async Task A429ThatNeverEasesIsReportedAsRateLimiting()
    {
        _forum.Reply(HttpStatusCode.TooManyRequests, "{}");
        var client = await Create();
        await Store(Valid());

        await client.Invoking(c => c.GetFiles(ForumSite.LoversLab, 42, CancellationToken.None)).Should()
            .ThrowAsync<ForumRateLimitedException>().WithMessage("LoversLab is rate-limiting downloads. Try again later or download it from the page.");

        FilesRequests.Should().HaveCount(4);
    }

    [Fact]
    public async Task AServerErrorIsRetried()
    {
        _forum.ReplyOnce(HttpStatusCode.BadGateway, "{}");
        _forum.Reply(HttpStatusCode.OK, Listing);
        var client = await Create();
        await Store(Valid());

        var listing = await client.GetFiles(ForumSite.LoversLab, 42, CancellationToken.None);

        listing.Files.Should().HaveCount(2);
    }

    [Fact]
    public async Task AnAnswerThatIsNotAListingIsReportedReadably()
    {
        _forum.Reply(HttpStatusCode.OK, "<html>maintenance</html>");
        var client = await Create();
        await Store(Valid());

        await client.Invoking(c => c.GetFiles(ForumSite.LoversLab, 42, CancellationToken.None)).Should().ThrowAsync<ForumReplyUnreadableException>();

        FilesRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task ManyLookupsOnAnExpiredTokenRefreshItOnceAndAllUseTheNewToken()
    {
        _forum.ReplyWhen(r => r.Uri == ForumEndpoints.For(ForumSite.LoversLab).Token, HttpStatusCode.OK, TokenReply(), Task.CompletedTask);
        _forum.Reply(HttpStatusCode.OK, Listing);
        var client = await Create();
        await Store(new OAuthToken("old", "refresh", Start - TimeSpan.FromMinutes(1)));

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetFiles(ForumSite.LoversLab, 42, CancellationToken.None)));

        results.Should().OnlyContain(r => r.Files.Count == 2);
        _forum.Requests.Count(r => r.Method == HttpMethod.Post).Should().Be(1);
        FilesRequests.Should().HaveCount(8).And.OnlyContain(r => r.Authorization == "Bearer new-access");
        (await _credentials.For(ForumSite.LoversLab).GetTokenAsync(CancellationToken.None))!.AccessToken.Should().Be("new-access");
    }

    [Fact]
    public async Task ManyLookupsThatAllGet401ReloadTheReplacedTokenAndNobodyIsSignedOut()
    {
        _forum.ReplyWhen(r => r.Authorization == "Bearer A", HttpStatusCode.Unauthorized, "{}", Task.CompletedTask);
        _forum.Reply(HttpStatusCode.OK, Listing);
        var client = await Create();
        await Store(Valid("A"));
        await _inner.SetAsync(ForumSecretKeys.LoversLab, Valid("B").ToJson(), CancellationToken.None);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetFiles(ForumSite.LoversLab, 42, CancellationToken.None)));

        results.Should().OnlyContain(r => r.Files.Count == 2);
        _forum.Requests.Count(r => r.Method == HttpMethod.Post).Should().Be(0);
        (await _credentials.For(ForumSite.LoversLab).GetTokenAsync(CancellationToken.None))!.AccessToken.Should().Be("B");
    }

    [Fact]
    public async Task ResolveReturnsTheListedAddressOfTheNamedFile()
    {
        _forum.Reply(HttpStatusCode.OK, Listing);
        var client = await Create();
        await Store(Valid());

        var url = await client.Resolve(ForumSite.LoversLab, 42, "mod.7z", 1234, CancellationToken.None);

        url.Should().Be(new Uri("https://files.test/get/abc?key=k"));
    }

    [Fact]
    public async Task ResolveMatchesTheNameExactly()
    {
        _forum.Reply(HttpStatusCode.OK, Listing);
        var client = await Create();
        await Store(Valid());

        await client.Invoking(c => c.Resolve(ForumSite.LoversLab, 42, "MOD.7z", 1234, CancellationToken.None)).Should()
            .ThrowAsync<ForumFileNotListedException>().WithMessage("LoversLab no longer lists 'MOD.7z' under this file");
    }

    [Fact]
    public async Task ResolveRefusesANameListedTwiceWithoutPickingOne()
    {
        _forum.Reply(HttpStatusCode.OK, """{"files":[{"name":"mod.7z","url":"https://files.test/1","size":1},{"name":"mod.7z","url":"https://files.test/2","size":1}]}""");
        var client = await Create();
        await Store(Valid());

        await client.Invoking(c => c.Resolve(ForumSite.LoversLab, 42, "mod.7z", 1, CancellationToken.None)).Should()
            .ThrowAsync<ForumFileAmbiguousException>().WithMessage("LoversLab lists 'mod.7z' more than once under this file; download it from the page");
    }

    [Fact]
    public async Task ResolveRefusesAFileOfAnotherSize()
    {
        _forum.Reply(HttpStatusCode.OK, Listing);
        var client = await Create();
        await Store(Valid());

        await client.Invoking(c => c.Resolve(ForumSite.LoversLab, 42, "mod.7z", 999, CancellationToken.None)).Should()
            .ThrowAsync<ForumFileSizeMismatchException>().WithMessage("LoversLab's copy of 'mod.7z' is a different size than the list expects");
    }

    [Fact]
    public async Task ASiteHasItsOwnApiHostAndItsOwnToken()
    {
        _forum.Reply(HttpStatusCode.OK, Listing);
        var client = await Create();
        await Store(Valid("ll"));
        await Store(Valid("vp"), ForumSite.VectorPlexus);

        await client.GetFiles(ForumSite.VectorPlexus, 7, CancellationToken.None);

        var request = _forum.Requests.Should().ContainSingle().Subject;
        request.Uri.Host.Should().Be("vectorplexus.com");
        request.Authorization.Should().Be("Bearer vp");
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition never held");
            await Task.Delay(10);
        }
    }
}
