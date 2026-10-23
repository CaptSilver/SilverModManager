using System.Net;
using NexusMods.Games.Bethesda.Loot.Masterlists;

namespace NexusMods.Games.Bethesda.Loot.Tests.Masterlists;

public class MasterlistStoreTests : IDisposable
{
    private const string ValidMasterlist = "plugins:\n  - name: cached.esp\n";
    private const string ValidPrelude = "globals:\n  - type: note\n    content: Prelude.\n";
    private const LootGame Game = LootGame.Skyrim;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "loot-store-" + Guid.NewGuid().ToString("N"));
    private readonly BundledMasterlistStore _bundled = new();

    public MasterlistStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private CachingMasterlistStore Store() => new(NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath(_dir), _bundled);

    private string MasterlistCache => Path.Combine(_dir, "loot", "skyrim", "masterlist.yaml");
    private string PreludeCache => Path.Combine(_dir, "loot", "prelude.yaml");

    private void WriteCache(string path, string text, DateTime lastWriteUtc, string? etag = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text);
        System.IO.File.SetLastWriteTimeUtc(path, lastWriteUtc);
        if (etag is not null) System.IO.File.WriteAllText(path + ".etag", etag);
    }

    private DateTime AfterLock => _bundled.RetrievedOn("skyrim").UtcDateTime.AddDays(3);

    [Fact]
    public void BundledStoreServesTheEmbeddedFiles()
    {
        var content = _bundled.Current(Game);

        content.Masterlist.Should().Contain("plugins:");
        content.Prelude.Should().Be(_bundled.PreludeText());
    }

    [Fact]
    public void LockDatesAreMidnightUtc()
    {
        var retrieved = _bundled.RetrievedOn("skyrim");

        retrieved.Offset.Should().Be(TimeSpan.Zero);
        retrieved.TimeOfDay.Should().Be(TimeSpan.Zero);
        _bundled.RetrievedOn("prelude").Should().Be(retrieved);
    }

    [Fact]
    public void TheBundledRevisionNamesTheCommitFromTheLock()
    {
        var lockText = new StreamReader(typeof(BundledMasterlistStore).Assembly.GetManifestResourceStream("loot/masterlists.lock.json")!).ReadToEnd();
        using var lockFile = System.Text.Json.JsonDocument.Parse(lockText);
        var commit = lockFile.RootElement.GetProperty("files").EnumerateArray()
            .Single(file => file.GetProperty("repo").GetString() == "skyrim").GetProperty("commit").GetString()!;

        var revision = _bundled.Current(Game).Revision;

        revision.Should().StartWith("skyrim ").And.Contain(commit[..10]).And.Contain("bundled");
    }

    [Fact]
    public void ARevisionFromTheCacheSaysItWasDownloadedAndWhen()
    {
        WriteCache(MasterlistCache, ValidMasterlist, new DateTime(2099, 3, 4, 0, 0, 0, DateTimeKind.Utc));

        Store().Current(Game).Revision.Should().Be("skyrim (downloaded 2099-03-04)");
    }

    [Fact]
    public void WithoutACacheTheRevisionIsTheBundledOne()
    {
        Store().Current(Game).Revision.Should().Be(_bundled.Current(Game).Revision);
    }

    [Fact]
    public void WithoutACacheTheBundledCopyIsUsed()
    {
        Store().Current(Game).Masterlist.Should().Be(_bundled.Current(Game).Masterlist);
    }

    [Fact]
    public void ACacheNewerThanTheLockDateWins()
    {
        WriteCache(MasterlistCache, ValidMasterlist, AfterLock);

        Store().Current(Game).Masterlist.Should().Be(ValidMasterlist);
    }

    [Fact]
    public void ACacheOlderThanTheLockDateLoses()
    {
        WriteCache(MasterlistCache, ValidMasterlist, _bundled.RetrievedOn("skyrim").UtcDateTime.AddDays(-30));

        Store().Current(Game).Masterlist.Should().Be(_bundled.Current(Game).Masterlist);
    }

    [Fact]
    public void ACachedPreludeIsUsedWhenNewer()
    {
        WriteCache(PreludeCache, ValidPrelude, AfterLock);

        Store().Current(Game).Prelude.Should().Be(ValidPrelude);
    }

    [Fact]
    public void ACachedPreludeOlderThanItsLockDateLoses()
    {
        WriteCache(PreludeCache, ValidPrelude, _bundled.RetrievedOn("prelude").UtcDateTime.AddDays(-30));

        Store().Current(Game).Prelude.Should().Be(_bundled.PreludeText());
    }

    [Fact]
    public void ACacheWrittenOnTheLockDayWins()
    {
        // The lock only records a day, so a copy saved at the start of that day may be newer than the bundle.
        WriteCache(MasterlistCache, ValidMasterlist, _bundled.RetrievedOn("skyrim").UtcDateTime);

        Store().Current(Game).Masterlist.Should().Be(ValidMasterlist);
    }

    [Fact]
    public async Task NotModifiedKeepsTheCacheAndSendsTheStoredETag()
    {
        WriteCache(MasterlistCache, ValidMasterlist, AfterLock, "\"abc\"");
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));

        var result = await Store().RefreshAsync(Game, new HttpClient(handler), CancellationToken.None);

        result.Should().Be(MasterlistRefreshResult.NotModified);
        System.IO.File.ReadAllText(MasterlistCache).Should().Be(ValidMasterlist);
        handler.Requests.Should().Contain(r => r.RequestUri!.AbsolutePath.EndsWith("/loot/skyrim/v0.29/masterlist.yaml") && r.Headers.IfNoneMatch.Single().Tag == "\"abc\"");
    }

    [Fact]
    public async Task OkReplacesTheCacheAndStoresTheETag()
    {
        var handler = new FakeHandler(r => Ok(r.RequestUri!.AbsolutePath.Contains("prelude") ? ValidPrelude : ValidMasterlist, "\"new\""));

        var result = await Store().RefreshAsync(Game, new HttpClient(handler), CancellationToken.None);

        result.Should().Be(MasterlistRefreshResult.Updated);
        System.IO.File.ReadAllText(MasterlistCache).Should().Be(ValidMasterlist);
        System.IO.File.ReadAllText(MasterlistCache + ".etag").Should().Be("\"new\"");
        System.IO.File.ReadAllText(PreludeCache).Should().Be(ValidPrelude);
        Store().Current(Game).Masterlist.Should().Be(ValidMasterlist);
    }

    [Fact]
    public async Task ServerErrorKeepsThePreviousCopy()
    {
        WriteCache(MasterlistCache, ValidMasterlist, AfterLock, "\"old\"");
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await Store().RefreshAsync(Game, new HttpClient(handler), CancellationToken.None);

        result.Should().Be(MasterlistRefreshResult.Failed);
        System.IO.File.ReadAllText(MasterlistCache).Should().Be(ValidMasterlist);
        System.IO.File.ReadAllText(MasterlistCache + ".etag").Should().Be("\"old\"");
    }

    [Fact]
    public async Task UnparsableBodyKeepsThePreviousCopy()
    {
        WriteCache(MasterlistCache, ValidMasterlist, AfterLock, "\"old\"");
        var handler = new FakeHandler(r => Ok(r.RequestUri!.AbsolutePath.Contains("prelude") ? ValidPrelude : "plugins: [unterminated\n  - : :", "\"bad\""));

        var result = await Store().RefreshAsync(Game, new HttpClient(handler), CancellationToken.None);

        result.Should().Be(MasterlistRefreshResult.Failed);
        System.IO.File.ReadAllText(MasterlistCache).Should().Be(ValidMasterlist);
        System.IO.File.ReadAllText(MasterlistCache + ".etag").Should().Be("\"old\"");
    }

    [Fact]
    public async Task UnparsableBodyWithNoCacheLeavesNothingBehind()
    {
        var handler = new FakeHandler(r => Ok(r.RequestUri!.AbsolutePath.Contains("prelude") ? ValidPrelude : "plugins: [unterminated", "\"bad\""));

        var result = await Store().RefreshAsync(Game, new HttpClient(handler), CancellationToken.None);

        result.Should().Be(MasterlistRefreshResult.Failed);
        System.IO.File.Exists(MasterlistCache).Should().BeFalse();
        System.IO.File.Exists(PreludeCache).Should().BeFalse();
    }

    [Fact]
    public async Task NetworkExceptionKeepsThePreviousCopy()
    {
        WriteCache(MasterlistCache, ValidMasterlist, AfterLock);
        var handler = new FakeHandler(_ => throw new HttpRequestException("offline"));

        var result = await Store().RefreshAsync(Game, new HttpClient(handler), CancellationToken.None);

        result.Should().Be(MasterlistRefreshResult.Failed);
        System.IO.File.ReadAllText(MasterlistCache).Should().Be(ValidMasterlist);
    }

    [Fact]
    public async Task AStaleETagIsNotSentWhenTheCacheFileIsGone()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MasterlistCache)!);
        System.IO.File.WriteAllText(MasterlistCache + ".etag", "\"orphan\"");
        var handler = new FakeHandler(r => Ok(ValidMasterlist, "\"n\""));

        await Store().RefreshAsync(Game, new HttpClient(handler), CancellationToken.None);

        handler.Requests.Should().OnlyContain(r => r.Headers.IfNoneMatch.Count == 0);
    }

    private const string OldMasterlist = "plugins:\n  - name: old.esp\n";
    private const string OldPrelude = "globals:\n  - type: note\n    content: Old prelude.\n";

    private void WriteOldPair()
    {
        WriteCache(MasterlistCache, OldMasterlist, AfterLock, "\"old-m\"");
        WriteCache(PreludeCache, OldPrelude, AfterLock, "\"old-p\"");
    }

    private async Task<MasterlistRefreshResult> RefreshWithNewPair() =>
        await Store().RefreshAsync(Game, new HttpClient(new FakeHandler(r =>
            Ok(r.RequestUri!.AbsolutePath.Contains("prelude") ? ValidPrelude : ValidMasterlist, "\"new\""))), CancellationToken.None);

    [Fact]
    public async Task AFailureWritingTheMasterlistLeavesTheOldPair()
    {
        WriteOldPair();
        // A directory where the masterlist's temp file belongs makes the write throw.
        Directory.CreateDirectory(MasterlistCache + ".tmp");

        (await RefreshWithNewPair()).Should().Be(MasterlistRefreshResult.Failed);

        var current = Store().Current(Game);
        current.Masterlist.Should().Be(OldMasterlist);
        current.Prelude.Should().Be(OldPrelude);
        System.IO.File.ReadAllText(PreludeCache + ".etag").Should().Be("\"old-p\"");
    }

    [Fact]
    public async Task AFailureAfterThePreludeIsReplacedRestoresThePreviousPair()
    {
        WriteOldPair();
        // The masterlist's ETag cannot be replaced, so the failure comes after the prelude has moved.
        System.IO.File.Delete(MasterlistCache + ".etag");
        Directory.CreateDirectory(Path.Combine(MasterlistCache + ".etag", "blocker"));

        (await RefreshWithNewPair()).Should().Be(MasterlistRefreshResult.Failed);

        var current = Store().Current(Game);
        current.Masterlist.Should().Be(OldMasterlist);
        current.Prelude.Should().Be(OldPrelude);
        System.IO.File.ReadAllText(PreludeCache + ".etag").Should().Be("\"old-p\"");
        Directory.GetFiles(Path.GetDirectoryName(PreludeCache)!, "*", SearchOption.AllDirectories)
            .Should().NotContain(f => f.EndsWith(".tmp") || f.EndsWith(".bak"));
    }

    [Fact]
    public async Task ABackupLeftByAnEarlierRefreshIsNotRestoredWhereNoFileWas()
    {
        WriteCache(PreludeCache, OldPrelude, AfterLock, "\"old-p\"");
        WriteCache(MasterlistCache + ".bak", OldMasterlist, AfterLock);
        Directory.CreateDirectory(Path.Combine(MasterlistCache + ".etag", "blocker"));

        (await RefreshWithNewPair()).Should().Be(MasterlistRefreshResult.Failed);

        System.IO.File.Exists(MasterlistCache).Should().BeFalse();
        var current = Store().Current(Game);
        current.Masterlist.Should().Be(_bundled.MasterlistText("skyrim"));
        current.Prelude.Should().Be(OldPrelude);
    }

    private static HttpResponseMessage Ok(string body, string etag)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return response;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
}
