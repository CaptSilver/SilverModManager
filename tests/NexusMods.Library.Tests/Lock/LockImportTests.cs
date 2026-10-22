using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Backend;
using NexusMods.Library.Tests.DownloadsService;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Library.Lock;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.NexusModsApi;
using Xunit;

namespace NexusMods.Library.Tests.Lock;

public class LockImportTests(IConnection connection)
{
    private readonly LockFixtures _fixtures = new(connection);

    private static LockEntry Github(string id, string asset, string sha256) => new(id, "v1", null, asset, sha256, "github");
    private static LockEntry Nexus(string id, string asset, string fileId, string sha256) => new(id, "1.0", fileId, asset, sha256, "nexus");

    private (LockImport Import, FakeSources Downloader) Create(Dictionary<string, string>? served = null)
    {
        var sources = new FakeSources(_fixtures, connection, served ?? []);
        return (sources.CreateImport(connection), sources);
    }

    private static LockFile Lock(params LockEntry[] entries) => new(entries);

    [Fact]
    public async Task AFileAlreadyInTheLibraryWithTheLockedDigestIsSkippedWithoutADownload()
    {
        var content = "already here " + Guid.NewGuid();
        await _fixtures.AddGitHubFile("tool.zip", content, LockFixtures.DigestOf(content), "v1");
        var (import, downloader) = Create();

        var plan = await import.Plan(Lock(Github("tool", "tool.zip", LockFixtures.DigestOf(content).ToString())), game: null, TestContext.Current.CancellationToken);
        var summary = await import.Apply(plan, TestContext.Current.CancellationToken);

        summary.Skipped.Should().ContainSingle().Which.Entry.Id.Should().Be("tool");
        summary.Verified.Should().BeEmpty();
        summary.Failed.Should().BeEmpty();
        downloader.Downloads.Should().BeEmpty();
    }

    [Fact]
    public async Task AFileInTheLibraryWithAnotherDigestIsDownloadedAgain()
    {
        var oldContent = "old bytes " + Guid.NewGuid();
        var newContent = "new bytes " + Guid.NewGuid();
        await _fixtures.AddGitHubFile("tool.zip", oldContent, LockFixtures.DigestOf(oldContent), "v1");
        var (import, downloader) = Create(new() { ["tool.zip"] = newContent });

        var plan = await import.Plan(Lock(Github("tool", "tool.zip", LockFixtures.DigestOf(newContent).ToString())), game: null, TestContext.Current.CancellationToken);
        var summary = await import.Apply(plan, TestContext.Current.CancellationToken);

        downloader.Downloads.Should().Equal(["tool.zip"], string.Join("; ", plan.Items.Select(i => i.Message)));
        summary.Verified.Should().ContainSingle().Which.File.Should().NotBeNull();
        summary.Skipped.Should().BeEmpty();
    }

    [Fact]
    public async Task AnEmptyLockDigestFailsTheEntryEvenWhenTheFileIsPresent()
    {
        var content = "present " + Guid.NewGuid();
        await _fixtures.AddGitHubFile("tool.zip", content, LockFixtures.DigestOf(content), "v1");
        var (import, downloader) = Create(new() { ["tool.zip"] = content });

        var plan = await import.Plan(Lock(Github("tool", "tool.zip", "")), game: null, TestContext.Current.CancellationToken);
        var summary = await import.Apply(plan, TestContext.Current.CancellationToken);

        summary.Failed.Should().ContainSingle().Which.Message.Should().Contain("sha256");
        summary.Skipped.Should().BeEmpty();
        downloader.Downloads.Should().BeEmpty("nothing can be verified against an empty digest");
    }

    [Fact]
    public async Task ADownloadWhoseBytesDoNotMatchIsDiscardedAndTheOtherEntriesStillRun()
    {
        var good = "good " + Guid.NewGuid();
        var tampered = "tampered " + Guid.NewGuid();
        // Older copies of both assets tell the importer which repository they come from.
        await _fixtures.AddGitHubFile("bad.zip", "older bad", LockFixtures.DigestOf("older bad"), "v0");
        await _fixtures.AddGitHubFile("good.zip", "older good", LockFixtures.DigestOf("older good"), "v0");
        var (import, downloader) = Create(new() { ["bad.zip"] = tampered, ["good.zip"] = good });

        var plan = await import.Plan(
            Lock(
                Github("bad", "bad.zip", LockFixtures.DigestOf("what the lock pinned").ToString()),
                Github("good", "good.zip", LockFixtures.DigestOf(good).ToString())
            ),
            game: null, TestContext.Current.CancellationToken);
        var summary = await import.Apply(plan, TestContext.Current.CancellationToken);

        summary.Failed.Should().ContainSingle().Which.Entry.Id.Should().Be("bad");
        summary.Failed.Single().Message.Should().Contain("bad.zip").And.Contain(LockFixtures.DigestOf("what the lock pinned").ToString());
        downloader.Discarded.Should().Equal("bad.zip");
        summary.Verified.Should().ContainSingle().Which.Entry.Id.Should().Be("good");
    }

    [Fact]
    public async Task AGitHubEntryWithNoKnownRepositoryFailsAndNamesTheEntry()
    {
        var (import, downloader) = Create(new() { ["unknown.zip"] = "x" });

        var plan = await import.Plan(Lock(Github("mystery", "unknown.zip", LockFixtures.DigestOf("x").ToString())), game: null, TestContext.Current.CancellationToken);
        var summary = await import.Apply(plan, TestContext.Current.CancellationToken);

        summary.Failed.Should().ContainSingle().Which.Message.Should().Contain("mystery").And.Contain("repository");
        downloader.Downloads.Should().BeEmpty();
    }

    [Fact]
    public async Task AGitHubEntryTakesItsRepositoryFromALibraryFileWithTheSameAssetName()
    {
        var oldContent = "v1 bytes " + Guid.NewGuid();
        var newContent = "v2 bytes " + Guid.NewGuid();
        await _fixtures.AddGitHubFile("host.tar.gz", oldContent, LockFixtures.DigestOf(oldContent), "v1", organization: "garden-of-eyes", repository: "me3");
        var (import, downloader) = Create(new() { ["host.tar.gz"] = newContent });

        var plan = await import.Plan(Lock(Github("host", "host.tar.gz", LockFixtures.DigestOf(newContent).ToString())), game: null, TestContext.Current.CancellationToken);
        await import.Apply(plan, TestContext.Current.CancellationToken);

        downloader.Repositories.Should().ContainSingle().Which.Should().Be(GitHubRepositoryRef.FromName("garden-of-eyes", "me3"));
    }

    [Fact]
    public async Task ANexusEntryDownloadsByFileIdAndIsVerified()
    {
        var content = "nexus bytes " + Guid.NewGuid();
        var (import, downloader) = Create(new() { ["5087:fov.zip"] = content });

        var plan = await import.Plan(Lock(Nexus("fov", "fov.zip", "5087", LockFixtures.DigestOf(content).ToString())), LockFixtures.GameId, TestContext.Current.CancellationToken);
        var summary = await import.Apply(plan, TestContext.Current.CancellationToken);

        summary.Verified.Should().ContainSingle();
        downloader.Downloads.Should().Equal("5087:fov.zip");
    }

    [Fact]
    public async Task ANexusEntryWithoutAGameOrAUsableFileIdFails()
    {
        var (import, downloader) = Create();
        var digest = LockFixtures.DigestOf("x").ToString();

        var plan = await import.Plan(
            Lock(Nexus("no-game", "a.zip", "1", digest), Nexus("bad-id", "b.zip", "abc", digest), new LockEntry("no-id", "1", null, "c.zip", digest, "nexus")),
            game: null, TestContext.Current.CancellationToken);
        var plannedWithGame = await import.Plan(
            Lock(Nexus("bad-id", "b.zip", "abc", digest), new LockEntry("no-id", "1", null, "c.zip", digest, "nexus")),
            LockFixtures.GameId, TestContext.Current.CancellationToken);

        plan.Items.Should().OnlyContain(item => item.Action == LockAction.Fail);
        plannedWithGame.Items.Should().OnlyContain(item => item.Action == LockAction.Fail);
        downloader.Downloads.Should().BeEmpty();
    }

    [Fact]
    public async Task ADownloaderThatThrowsFailsOnlyThatEntry()
    {
        var content = "fine " + Guid.NewGuid();
        var (import, downloader) = Create(new() { ["5:down.zip"] = "x", ["6:fine.zip"] = content });
        downloader.Throwing.Add("5:down.zip");

        var plan = await import.Plan(
            Lock(Nexus("down", "down.zip", "5", LockFixtures.DigestOf("x").ToString()), Nexus("fine", "fine.zip", "6", LockFixtures.DigestOf(content).ToString())),
            LockFixtures.GameId, TestContext.Current.CancellationToken);
        var summary = await import.Apply(plan, TestContext.Current.CancellationToken);

        summary.Failed.Should().ContainSingle().Which.Message.Should().Contain("network down");
        summary.Verified.Should().ContainSingle().Which.Entry.Id.Should().Be("fine");
    }

    [Fact]
    public async Task ADownloadInAPlanForAnUnregisteredSourceFailsThatEntryWithoutDownloading()
    {
        var content = "fine " + Guid.NewGuid();
        var (import, downloader) = Create(new() { ["6:fine.zip"] = content });
        var fine = await import.Plan(Lock(Nexus("fine", "fine.zip", "6", LockFixtures.DigestOf(content).ToString())), LockFixtures.GameId, TestContext.Current.CancellationToken);

        // A plan built by hand rather than by Plan, for a source nobody registered.
        var orphan = new LockPlanItem(new LockEntry("orphan", "1", null, "orphan.zip", LockFixtures.DigestOf("y").ToString(), "ghost"), LockAction.Download, "download");
        var plan = new LockImportPlan([orphan, ..fine.Items], LockFixtures.GameId);
        var summary = await import.Apply(plan, TestContext.Current.CancellationToken);

        summary.Failed.Should().ContainSingle().Which.Message.Should().Contain("orphan").And.Contain("unknown source");
        summary.Verified.Should().ContainSingle().Which.Entry.Id.Should().Be("fine");
        downloader.Downloads.Should().Equal("6:fine.zip");
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            DIHelpers.ConfigureServices(services);
            services
                .AddLibraryModels()
                .AddOSInterop()
                .AddFileExtractors();
        }
    }
}
