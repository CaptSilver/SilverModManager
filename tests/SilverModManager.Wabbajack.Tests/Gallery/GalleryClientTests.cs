using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NexusMods.Paths;
using SilverModManager.Wabbajack.Gallery;

namespace SilverModManager.Wabbajack.Tests.Gallery;

public class GalleryClientTests(TemporaryFileManager temporaryFileManager)
{
    private sealed class Rig : IAsyncDisposable
    {
        private readonly TemporaryPath _folder;
        public GalleryHandler Handler { get; }
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        public AbsolutePath CacheFile { get; }
        public WabbajackGalleryClient Client { get; }

        public Rig(TemporaryFileManager files, GalleryHandler? handler = null)
        {
            _folder = files.CreateFolder();
            Handler = handler ?? GalleryHandler.WithFixtures();
            CacheFile = _folder.Path.Combine("cache/gallery.json");
            Client = Create();
        }

        /// <summary>A second client over the same cache file, as after an app restart.</summary>
        public WabbajackGalleryClient Create() => new(new HttpClient(Handler), CacheFile, Time, NullLogger<WabbajackGalleryClient>.Instance);

        public ValueTask DisposeAsync() => _folder.DisposeAsync();
    }

    [Fact]
    public async Task CombinesTheListsOfEveryRepositoryThatAnswers()
    {
        await using var rig = new Rig(temporaryFileManager);

        var listing = await rig.Client.Get(cancellationToken: TestContext.Current.CancellationToken);

        listing.IsStale.Should().BeFalse();
        listing.Entries.Select(e => e.MachineUrl).Should().BeEquivalentTo(
            ["example-skyrim", "spicy-fallout", "taken-down", "tool", "sdv", "community-oblivion"]);
        listing.Entries.Single(e => e.MachineUrl == "community-oblivion").Repository.Should().Be("someone/community-lists");
    }

    [Fact]
    public async Task ARepositoryThatFailsOrReturnsJunkOrIsNotHttpsIsSkipped()
    {
        await using var rig = new Rig(temporaryFileManager);

        await rig.Client.Get(cancellationToken: TestContext.Current.CancellationToken);

        rig.Handler.Requests.Should().Contain(GalleryFixtureFiles.ServerError, "the failing repository was asked for");
        rig.Handler.Requests.Should().Contain(GalleryFixtureFiles.Junk);
        rig.Handler.Requests.Should().NotContain(u => u.Scheme == "file");
    }

    [Fact]
    public async Task ARepositoryServingAnOversizedDocumentIsSkipped()
    {
        var handler = GalleryHandler.WithFixtures();
        handler.Responses[GalleryFixtureFiles.Community] = (System.Net.HttpStatusCode.OK,
            GalleryFixtureFiles.Read("modlists-community.json") + new string(' ', WabbajackGalleryClient.MaxDocumentBytes));
        await using var rig = new Rig(temporaryFileManager, handler);

        var listing = await rig.Client.Get(cancellationToken: TestContext.Current.CancellationToken);

        listing.Entries.Should().NotBeEmpty();
        listing.Entries.Should().NotContain(e => e.Repository == "someone/community-lists");
    }

    [Fact]
    public async Task ADocumentStartingWithAByteOrderMarkIsRead()
    {
        var handler = GalleryHandler.WithFixtures();
        handler.Responses[GalleryFixtureFiles.Community] = (System.Net.HttpStatusCode.OK, "\uFEFF" + GalleryFixtureFiles.Read("modlists-community.json"));
        await using var rig = new Rig(temporaryFileManager, handler);

        var listing = await rig.Client.Get(cancellationToken: TestContext.Current.CancellationToken);

        listing.Entries.Should().Contain(e => e.Repository == "someone/community-lists");
    }

    [Fact]
    public async Task RepositoriesAreFetchedInParallelWithALimit()
    {
        var handler = GalleryHandler.WithFixtures();
        handler.Delay = TimeSpan.FromMilliseconds(60);
        var repositories = Enumerable.Range(0, 12).ToDictionary(i => $"r/{i}", i => $"https://example.invalid/{i}/modlists.json");
        handler.Responses[GalleryFixtureFiles.Repositories] = (System.Net.HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(repositories));
        foreach (var url in repositories.Values) handler.Responses[new Uri(url)] = (System.Net.HttpStatusCode.OK, "[]");
        await using var rig = new Rig(temporaryFileManager, handler);

        await rig.Client.Get(cancellationToken: TestContext.Current.CancellationToken);

        handler.Peak.Should().BeGreaterThan(1, "repositories are not fetched one at a time");
        handler.Peak.Should().BeLessThanOrEqualTo(WabbajackGalleryClient.MaxParallelRepositories);
    }

    [Fact]
    public async Task AFreshCacheIsServedWithoutTheNetwork()
    {
        await using var rig = new Rig(temporaryFileManager);
        await rig.Client.Get(cancellationToken: TestContext.Current.CancellationToken);
        var requests = rig.Handler.Requests.Count;
        rig.Time.Advance(TimeSpan.FromHours(5));

        var listing = await rig.Create().Get(cancellationToken: TestContext.Current.CancellationToken);

        rig.Handler.Requests.Count.Should().Be(requests);
        listing.IsStale.Should().BeFalse();
        listing.Entries.Should().NotBeEmpty();
    }

    [Fact]
    public async Task AnOldCacheIsFetchedAgain()
    {
        await using var rig = new Rig(temporaryFileManager);
        await rig.Client.Get(cancellationToken: TestContext.Current.CancellationToken);
        var requests = rig.Handler.Requests.Count;
        rig.Time.Advance(TimeSpan.FromHours(7));

        var listing = await rig.Create().Get(cancellationToken: TestContext.Current.CancellationToken);

        rig.Handler.Requests.Count.Should().BeGreaterThan(requests);
        listing.FetchedAt.Should().Be(rig.Time.GetUtcNow());
    }

    [Fact]
    public async Task RefreshIgnoresAFreshCache()
    {
        await using var rig = new Rig(temporaryFileManager);
        await rig.Client.Get(cancellationToken: TestContext.Current.CancellationToken);
        var requests = rig.Handler.Requests.Count;

        await rig.Client.Get(refresh: true, cancellationToken: TestContext.Current.CancellationToken);

        rig.Handler.Requests.Count.Should().BeGreaterThan(requests);
    }

    [Fact]
    public async Task OfflineWithAnOldCacheServesTheStaleList()
    {
        await using var rig = new Rig(temporaryFileManager);
        var first = await rig.Client.Get(cancellationToken: TestContext.Current.CancellationToken);
        rig.Time.Advance(TimeSpan.FromDays(3));
        rig.Handler.Offline = true;

        var listing = await rig.Create().Get(cancellationToken: TestContext.Current.CancellationToken);

        listing.IsStale.Should().BeTrue();
        listing.FetchedAt.Should().Be(first.FetchedAt);
        listing.Entries.Select(e => e.MachineUrl).Should().BeEquivalentTo(first.Entries.Select(e => e.MachineUrl));
    }

    [Fact]
    public async Task OfflineWithoutACacheThrows()
    {
        await using var rig = new Rig(temporaryFileManager);
        rig.Handler.Offline = true;

        var act = async () => await rig.Client.Get(cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<GalleryUnavailableException>();
    }

    [Fact]
    public async Task EveryRepositoryFailingKeepsTheCacheInsteadOfReplacingItWithNothing()
    {
        await using var rig = new Rig(temporaryFileManager);
        var first = await rig.Client.Get(cancellationToken: TestContext.Current.CancellationToken);
        rig.Time.Advance(TimeSpan.FromDays(1));
        foreach (var key in rig.Handler.Responses.Keys.Where(k => k != GalleryFixtureFiles.Repositories).ToList())
            rig.Handler.Responses[key] = (System.Net.HttpStatusCode.ServiceUnavailable, "");

        var listing = await rig.Create().Get(cancellationToken: TestContext.Current.CancellationToken);

        listing.IsStale.Should().BeTrue();
        listing.Entries.Should().HaveCount(first.Entries.Count);
    }

    [Fact]
    public async Task ACorruptCacheFileIsTreatedAsMissing()
    {
        await using var rig = new Rig(temporaryFileManager);
        rig.CacheFile.Parent.CreateDirectory();
        await rig.CacheFile.WriteAllTextAsync("{ truncated", TestContext.Current.CancellationToken);

        var listing = await rig.Client.Get(cancellationToken: TestContext.Current.CancellationToken);

        listing.Entries.Should().NotBeEmpty();
    }

    [Fact]
    public async Task JunkInTheRepositoryListIsUnavailableNotACrash()
    {
        var handler = GalleryHandler.WithFixtures();
        handler.Responses[GalleryFixtureFiles.Repositories] = (System.Net.HttpStatusCode.OK, "<html>captive portal</html>");
        await using var rig = new Rig(temporaryFileManager, handler);

        var act = async () => await rig.Client.Get(cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<GalleryUnavailableException>();
    }
}
