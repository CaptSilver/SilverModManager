using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.Backend;
using SilverModManager.Sdk;
using SilverModManager.Library.GitHub;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.GitHub;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using Xunit;

namespace SilverModManager.Library.Tests.GitHub;

public class GitHubShelfCatalogTests(IServiceProvider serviceProvider, IConnection connection)
{
    private static readonly GitHubShelfEntryDefinition Skse = new("ianpatt", "skse64", ".7z", "skse64_", "Script extender");

    // Each test gets its own bytes: the library keeps one file per set of bytes and the tests share a database.
    private readonly byte[] _payload = Guid.NewGuid().ToByteArray().Concat(Enumerable.Range(0, 3000).Select(i => (byte)(i * 7 + 3))).ToArray();

    private static CatalogQuery Everything(GitHubShelfCatalog catalog) => new("", catalog.Sorts[0], null, false);

    private GitHubShelfCatalog CatalogOf(ShelfRig rig, out ILoadoutInstallCore core)
    {
        core = Substitute.For<ILoadoutInstallCore>();
        var installCore = core;
        installCore.Run(Arg.Any<SilverModManager.Sdk.Loadouts.LoadoutId>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<ResolvedInstall>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var outcomes = new List<InstallOutcome>();
                foreach (var item in call.Arg<IReadOnlyList<ResolvedInstall>>())
                {
                    var library = await item.Download(call.Arg<CancellationToken>());
                    outcomes.Add(new InstallOutcome(item.Name, library, Downloaded: true, LoadoutPackageOutcome.Installed, item.Dependency));
                }

                return (IReadOnlyList<InstallOutcome>)outcomes;
            });
        var services = new ServiceCollection().AddSingleton(installCore).BuildServiceProvider();
        return new GitHubShelfCatalog(rig.Api, rig.Releases, rig.Entries, new CompositeProvider(serviceProvider, services), () => rig.Client, NullLogger<GitHubShelfCatalog>.Instance);
    }

    // The job needs the test host's job monitor and temporary files; the install core is the test's own.
    private sealed class CompositeProvider(IServiceProvider host, IServiceProvider overrides) : IServiceProvider
    {
        public object? GetService(Type serviceType) => overrides.GetService(serviceType) ?? host.GetService(serviceType);
    }

    [Fact]
    public async Task TheShelfListsTheGamesEntriesAndTheUsersAddedRepositories()
    {
        var rig = new ShelfRig(connection, Skse);
        rig.Api.Marked["ianpatt/skse64"] = TestReleases.Make("v2.2.6", "ianpatt", "skse64", [TestReleases.AssetOf("skse64_2_02_06.7z")], body: "\n## Script extender\nSecond line");
        rig.Api.Marked["someone/loader"] = TestReleases.Make("v1.0", "someone", "loader", [TestReleases.AssetOf("loader.zip")]);
        await rig.Entries.Add(rig.GameId, "https://github.com/someone/loader", ".zip", null, false);
        var catalog = CatalogOf(rig, out _);

        var page = await catalog.Search(rig.Game, Everything(catalog), CancellationToken.None);

        page.Cards.Select(card => card.Id).Should().Equal("someone/loader", "ianpatt/skse64");
        var skse = page.Cards[1];
        skse.Title.Should().Be("skse64");
        skse.Author.Should().Be("ianpatt");
        skse.Version.Should().Be("v2.2.6");
        skse.Summary.Should().Be("Script extender", "the first line of the release notes that has words in it");
        skse.Page.Should().Be(new Uri("https://github.com/ianpatt/skse64/releases/tag/v2.2.6"));
        page.Cards[0].Summary.Should().Be("Added by you", "a release without notes falls back to what the entry is");
    }

    [Fact]
    public async Task ASearchTextNarrowsTheShelfByRepositoryOrOwner()
    {
        var rig = new ShelfRig(connection, Skse);
        await rig.Entries.Add(rig.GameId, "someone/loader", ".zip", null, false);
        var catalog = CatalogOf(rig, out _);

        var page = await catalog.Search(rig.Game, Everything(catalog) with { Text = "LOAD" }, CancellationToken.None);

        page.Cards.Should().ContainSingle().Which.Id.Should().Be("someone/loader");
    }

    [Fact]
    public async Task OnlyAGameWithAShelfIsServed()
    {
        var rig = new ShelfRig(connection);
        var catalog = CatalogOf(rig, out _);
        catalog.Serves(rig.Game).Should().BeFalse("nothing is declared and nothing was added");

        await rig.Entries.Add(rig.GameId, "o/r", ".zip", null, false);

        catalog.Serves(rig.Game).Should().BeTrue();
        catalog.Serves(new FakeGame(GameId.From("Tests.Unrelated." + Guid.NewGuid()))).Should().BeFalse();
    }

    [Fact]
    public async Task FindTakesTheOwnerAndNameTheCardShowsAndIgnoresCase()
    {
        var rig = new ShelfRig(connection, Skse);
        var catalog = CatalogOf(rig, out _);

        (await catalog.Find(rig.Game, "IANPATT/skse64", CancellationToken.None))!.Id.Should().Be("ianpatt/skse64");
        (await catalog.Find(rig.Game, "nobody/nothing", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task TheFirstFetchRecordsTheRepositoryId()
    {
        var rig = new ShelfRig(connection, Skse);
        rig.Api.Marked["ianpatt/skse64"] = TestReleases.Make("v2.2.6", "ianpatt", "skse64", [TestReleases.AssetOf("skse64_2_02_06.7z")]);
        rig.Api.Ids["ianpatt/skse64"] = 12345UL;
        var catalog = CatalogOf(rig, out _);

        await catalog.Search(rig.Game, Everything(catalog), CancellationToken.None);

        rig.Entries.For(rig.Game).Single().RepositoryId.Should().Be(12345UL);
        rig.Api.Requests.Count(request => request.StartsWith("id ")).Should().Be(1);
    }

    [Fact]
    public async Task AKnownRepositoryIdIsNotAskedForAgainAndTheReleaseIsFetchedByIt()
    {
        var rig = new ShelfRig(connection, Skse with { RepositoryId = 12345UL });
        rig.Api.ById[12345UL] = TestReleases.Make("v2.2.6", "ianpatt", "skse64", [TestReleases.AssetOf("skse64_2_02_06.7z")]);
        var catalog = CatalogOf(rig, out _);

        await catalog.Search(rig.Game, Everything(catalog), CancellationToken.None);

        rig.Api.Requests.Should().Equal("latest #12345");
    }

    [Fact]
    public async Task ARefreshWithinTheHourReusesTheRelease()
    {
        var rig = new ShelfRig(connection, Skse with { RepositoryId = 1UL });
        rig.Api.ById[1UL] = TestReleases.Make("v1", "ianpatt", "skse64", [TestReleases.AssetOf("skse64_1.7z")]);
        var catalog = CatalogOf(rig, out _);

        await catalog.Search(rig.Game, Everything(catalog), CancellationToken.None);
        rig.Time.Advance(TimeSpan.FromMinutes(59));
        await catalog.Search(rig.Game, Everything(catalog), CancellationToken.None);
        rig.Api.Requests.Should().HaveCount(1, "the listing is reused inside the hour");

        rig.Time.Advance(TimeSpan.FromMinutes(2));
        await catalog.Search(rig.Game, Everything(catalog), CancellationToken.None);
        rig.Api.Requests.Should().HaveCount(2, "an hour on, the page asks again");
    }

    [Fact]
    public async Task AUsedUpRateLimitSurfacesWhenBrowsing()
    {
        var rig = new ShelfRig(connection, Skse);
        rig.Api.Failure = new GitHubRateLimitedException(rig.Time.GetUtcNow().AddMinutes(20));
        var catalog = CatalogOf(rig, out _);

        var act = async () => await catalog.Search(rig.Game, Everything(catalog), CancellationToken.None);

        await act.Should().ThrowAsync<GitHubRateLimitedException>();
    }

    [Fact]
    public async Task AUsedUpRateLimitIsARefusalWhenInstalling()
    {
        var rig = new ShelfRig(connection, Skse);
        var catalog = CatalogOf(rig, out _);
        rig.Api.Marked["ianpatt/skse64"] = TestReleases.Make("v2.2.6", "ianpatt", "skse64", [TestReleases.AssetOf("skse64_2_02_06.7z")]);
        var card = (await catalog.Find(rig.Game, "ianpatt/skse64", CancellationToken.None))!;
        rig.Time.Advance(TimeSpan.FromHours(2));
        rig.Api.Failure = new GitHubRateLimitedException(rig.Time.GetUtcNow().AddMinutes(20));

        var outcome = await catalog.Install(card, ShelfRig.SomeLoadout, CancellationToken.None);

        outcome.Should().BeOfType<CatalogInstallOutcome.Refused>().Which.Why.Should().BeOfType<GitHubRateLimitedException>();
    }

    [Fact]
    public async Task AnAssetWithoutADigestInstallsUnderTrustOnFirstUse()
    {
        var rig = new ShelfRig(connection, new GitHubShelfEntryDefinition("o", "tool", ".7z", "tool_", "Tool"));
        rig.Bytes.Serve("https://example.invalid/tool_1.7z", _payload);
        rig.Api.Marked["o/tool"] = TestReleases.Make("v1", "o", "tool", [TestReleases.AssetOf("tool_1.7z", digest: null)]);
        var catalog = CatalogOf(rig, out var core);
        var card = (await catalog.Find(rig.Game, "o/tool", CancellationToken.None))!;

        var outcome = await catalog.Install(card, ShelfRig.SomeLoadout, CancellationToken.None);

        var installed = outcome.Should().BeOfType<CatalogInstallOutcome.Installed>().Subject;
        var file = GitHubReleaseLibraryFile.Load(connection.Db, installed.PerPackage.Single().Item.Id);
        file.PinnedSha256.Should().Be(TestReleases.Sha(_payload), "the first download decides the pin");
        file.DigestAdvertised.Value.Should().BeFalse();
        file.Tag.Should().Be("v1");
        await core.Received(1).Run(ShelfRig.SomeLoadout, Arg.Any<string>(), Arg.Any<IReadOnlyList<ResolvedInstall>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheDownloadIsRecordedForTheGameTheCardWasBrowsedFor()
    {
        var rig = new ShelfRig(connection, new GitHubShelfEntryDefinition("o", "tool", ".7z", null, "Tool"));
        rig.Bytes.Serve("https://example.invalid/tool_1.7z", _payload);
        rig.Api.Marked["o/tool"] = TestReleases.Make("v1", "o", "tool", [TestReleases.AssetOf("tool_1.7z")]);
        var catalog = CatalogOf(rig, out _);
        var card = (await catalog.Find(rig.Game, "o/tool", CancellationToken.None))!;

        var outcome = await catalog.Install(card, ShelfRig.SomeLoadout, CancellationToken.None);

        var item = outcome.Should().BeOfType<CatalogInstallOutcome.Installed>().Subject.PerPackage.Single().Item;
        LibraryDestinations.GamesOf(item).Should().Contain(rig.GameId);
    }

    [Fact]
    public async Task ASidecarMismatchAddsNothing()
    {
        var rig = new ShelfRig(connection, new GitHubShelfEntryDefinition("o", "tool", ".7z", "tool_", "Tool"));
        var wrong = TestReleases.Sha("not the payload"u8.ToArray());
        rig.Bytes.Serve("https://example.invalid/tool_1.7z", _payload).Serve("https://example.invalid/SHA256SUMS", $"{wrong}  tool_1.7z\n");
        rig.Api.Marked["o/tool"] = TestReleases.Make("v1", "o", "tool", [TestReleases.AssetOf("tool_1.7z"), TestReleases.AssetOf("SHA256SUMS")]);
        var catalog = CatalogOf(rig, out _);
        var card = (await catalog.Find(rig.Game, "o/tool", CancellationToken.None))!;
        var copiesBefore = LibraryFile.All(connection.Db).Count(file => file.FileName == "tool_1.7z");

        var outcome = await catalog.Install(card, ShelfRig.SomeLoadout, CancellationToken.None);

        var refusal = outcome.Should().BeOfType<CatalogInstallOutcome.Refused>().Subject.Why;
        refusal.Should().BeOfType<ReleaseAssetIntegrityException>().Which.Message.Should().Contain(wrong.ToString());
        LibraryFile.All(connection.Db).Count(file => file.FileName == "tool_1.7z").Should().Be(copiesBefore);
    }

    [Fact]
    public async Task AMatchingSidecarInstallsAndTheRowStillSaysFirstUse()
    {
        var rig = new ShelfRig(connection, new GitHubShelfEntryDefinition("o", "tool", ".7z", "tool_", "Tool"));
        rig.Bytes.Serve("https://example.invalid/tool_1.7z", _payload).Serve("https://example.invalid/SHA256SUMS", $"{TestReleases.Sha(_payload)}  tool_1.7z\n");
        rig.Api.Marked["o/tool"] = TestReleases.Make("v1", "o", "tool", [TestReleases.AssetOf("tool_1.7z"), TestReleases.AssetOf("SHA256SUMS")]);
        var catalog = CatalogOf(rig, out _);
        var card = (await catalog.Find(rig.Game, "o/tool", CancellationToken.None))!;

        var outcome = await catalog.Install(card, ShelfRig.SomeLoadout, CancellationToken.None);

        var item = outcome.Should().BeOfType<CatalogInstallOutcome.Installed>().Subject.PerPackage.Single().Item;
        GitHubReleaseLibraryFile.Load(connection.Db, item.Id).DigestAdvertised.Value.Should().BeFalse();
    }

    [Fact]
    public async Task ARepositoryWithoutAReleaseHasACardThatCannotBeInstalled()
    {
        var rig = new ShelfRig(connection, new GitHubShelfEntryDefinition("o", "empty", ".7z", null, "Tool"));
        var catalog = CatalogOf(rig, out _);
        var card = (await catalog.Find(rig.Game, "o/empty", CancellationToken.None))!;

        card.Version.Should().BeEmpty();
        var outcome = await catalog.Install(card, ShelfRig.SomeLoadout, CancellationToken.None);

        outcome.Should().BeOfType<CatalogInstallOutcome.Refused>().Which.Why.Should().BeOfType<CardNotInstallableException>();
    }

    [Fact]
    public async Task AReleaseWithoutTheAssetIsARefusalNamingTheSuffix()
    {
        var rig = new ShelfRig(connection, new GitHubShelfEntryDefinition("o", "tool", ".7z", null, "Tool"));
        rig.Api.Marked["o/tool"] = TestReleases.Make("v1", "o", "tool", [TestReleases.AssetOf("tool.zip")]);
        var catalog = CatalogOf(rig, out _);
        var card = (await catalog.Find(rig.Game, "o/tool", CancellationToken.None))!;

        var outcome = await catalog.Install(card, ShelfRig.SomeLoadout, CancellationToken.None);

        outcome.Should().BeOfType<CatalogInstallOutcome.Refused>().Which.Why.Message.Should().Contain(".7z");
    }

    [Fact]
    public void TheShelfOffersOnlyTheInstallVerbAndANameSort()
    {
        var catalog = CatalogOf(new ShelfRig(connection), out _);

        catalog.DisplayName.Should().Be("GitHub");
        catalog.InstallLabel(new FakeGame(GameId.From("Tests.Unrelated"))).Should().Be("Install");
        catalog.Sorts.Select(sort => sort.Key).Should().Equal("name");
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            DIHelpers.ConfigureServices(services);
            services.AddLibraryModels().AddOSInterop().AddFileExtractors();
        }
    }
}
