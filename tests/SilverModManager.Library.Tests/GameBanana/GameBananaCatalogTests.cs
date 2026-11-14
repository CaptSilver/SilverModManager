using SilverModManager.Backend;
using SilverModManager.Abstractions.Loadouts;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.Library.GameBanana;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.Library.Tests.GitHub;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.GameBanana;
using SilverModManager.Networking.GameBanana.DTOs;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.Library.Tests.GameBanana;

public class GameBananaCatalogTests(IServiceProvider serviceProvider, IConnection connection)
{
    // Each test gets its own bytes: the library keeps one file per set of bytes and the tests share a database.
    private readonly byte[] _payload = Guid.NewGuid().ToByteArray().Concat(Enumerable.Range(0, 3000).Select(i => (byte)(i * 7 + 3))).ToArray();
    private readonly long _modId = Random.Shared.NextInt64(1_000_000, 900_000_000);
    private long FileId(int n) => _modId * 10 + n;

    private CatalogQuery Query(GameBananaCatalog catalog, string text = "", string? sort = null, string? cursor = null, bool adult = false)
        => new(text, sort is null ? catalog.Sorts[0] : catalog.Sorts.Single(s => s.Key == sort), cursor, adult);

    private GameBananaCatalog CatalogOf(GameBananaRig rig, out ILoadoutInstallCore core)
    {
        core = Substitute.For<ILoadoutInstallCore>();
        var installCore = core;
        installCore.Run(Arg.Any<LoadoutId>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<ResolvedInstall>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var outcomes = new List<InstallOutcome>();
                foreach (var item in call.Arg<IReadOnlyList<ResolvedInstall>>())
                {
                    var library = item.InLibrary ?? await item.Download(call.Arg<CancellationToken>());
                    outcomes.Add(new InstallOutcome(item.Name, library, Downloaded: item.InLibrary is null, LoadoutPackageOutcome.Installed, item.Dependency));
                }

                return (IReadOnlyList<InstallOutcome>)outcomes;
            });
        var services = new ServiceCollection().AddSingleton(installCore).BuildServiceProvider();
        return new GameBananaCatalog(rig.Api, rig.Connection, [rig.Game], new CompositeServiceProvider(services, serviceProvider), () => rig.Client, NullLogger<GameBananaCatalog>.Instance);
    }

    private GameBananaProfile ProfileWith(string name, params GameBananaFile[] files)
        => TestMods.Profile(_modId, name, updated: 1000, modified: 1000, files);

    private GameBananaFile PayloadFile(int n, bool clean = true, IReadOnlyDictionary<string, IReadOnlyList<string>>? warnings = null)
        => TestMods.File(FileId(n), $"{_modId}-{n}.zip", Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(_payload)), clean, added: 100 + n, warnings);

    [Fact]
    public async Task TheDefaultListingIsTheSubfeedAndItsCardsCarryTheRecord()
    {
        var rig = new GameBananaRig(connection);
        rig.Api.Listings["subfeed 297 1"] = new GameBananaListing([TestMods.Record(11, "Head HUD"), TestMods.Record(12, "A question", model: "Question")], 41, false);
        var catalog = CatalogOf(rig, out _);

        var page = await catalog.Search(rig.Game, Query(catalog), CancellationToken.None);

        rig.Api.Requests.Should().Equal("subfeed 297 1");
        var card = page.Cards.Should().ContainSingle("only the sections the game lists get a card").Subject;
        card.Id.Should().Be("11");
        card.Title.Should().Be("Head HUD");
        card.Author.Should().Be("Author");
        card.Summary.Should().Be("Maps");
        card.Version.Should().Be("1.0");
        card.Page.Should().Be(new Uri("https://gamebanana.com/mods/11"));
        card.Icon.Should().Be(new Uri("https://images.gamebanana.com/img/ss/mods/220-90_11.jpg"));
        card.Updated.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1000));
        card.Handle.Should().Be(new GameBananaHandle(11, null));
        page.Total.Should().Be(41);
        page.NextCursor.Should().Be("2");
    }

    [Fact]
    public async Task TheLastPageHasNoCursor()
    {
        var rig = new GameBananaRig(connection);
        rig.Api.Listings["subfeed 297 2"] = new GameBananaListing([TestMods.Record(11, "Head HUD")], GameBananaCatalog.PageSize + 1, true);
        var catalog = CatalogOf(rig, out _);

        var page = await catalog.Search(rig.Game, Query(catalog, cursor: "2"), CancellationToken.None);

        page.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task ASortedListingUsesTheIndexOfTheGamesSection()
    {
        var rig = new GameBananaRig(connection);
        rig.Api.Listings["index 297 Mod Generic_MostDownloaded 1"] = new GameBananaListing([TestMods.Record(21, "Popular")], 1, true);
        var catalog = CatalogOf(rig, out _);

        var page = await catalog.Search(rig.Game, Query(catalog, sort: "Generic_MostDownloaded"), CancellationToken.None);

        rig.Api.Requests.Should().Equal("index 297 Mod Generic_MostDownloaded 1");
        page.Cards.Select(card => card.Title).Should().Equal("Popular");
    }

    [Fact]
    public async Task TextGoesToTheSiteSearchWhateverTheSort()
    {
        var rig = new GameBananaRig(connection);
        rig.Api.Listings["search 297 head hud 1"] = new GameBananaListing([TestMods.Record(31, "Head HUD")], 1, true);
        var catalog = CatalogOf(rig, out _);

        var page = await catalog.Search(rig.Game, Query(catalog, text: " head hud ", sort: "Generic_Newest"), CancellationToken.None);

        rig.Api.Requests.Should().Equal("search 297 head hud 1");
        page.Cards.Should().ContainSingle();
    }

    [Fact]
    public async Task AdultModsAreLeftOutUnlessAsked()
    {
        var rig = new GameBananaRig(connection);
        rig.Api.Listings["subfeed 297 1"] = new GameBananaListing([TestMods.Record(41, "Tame"), TestMods.Record(42, "Spicy", adult: true)], 2, true);
        var catalog = CatalogOf(rig, out _);

        var hidden = await catalog.Search(rig.Game, Query(catalog), CancellationToken.None);
        var shown = await catalog.Search(rig.Game, Query(catalog, adult: true), CancellationToken.None);

        hidden.Cards.Select(card => card.Title).Should().Equal("Tame");
        shown.Cards.Select(card => (card.Title, card.IsAdult)).Should().Equal(("Tame", false), ("Spicy", true));
    }

    [Fact]
    public async Task OnlyAGameGameBananaListsIsServed()
    {
        var rig = new GameBananaRig(connection);
        var catalog = CatalogOf(rig, out _);

        catalog.Serves(rig.Game).Should().BeTrue();
        catalog.Serves(new FakeGame(GameId.From("Tests.NotOnGameBanana"))).Should().BeFalse();
        catalog.DisplayName.Should().Be("GameBanana");
        catalog.InstallLabel(rig.Game).Should().Be("Install");
    }

    [Fact]
    public async Task FindReadsTheModPageAndIgnoresAModOfAnotherGame()
    {
        var rig = new GameBananaRig(connection);
        rig.Api.Profiles[_modId] = ProfileWith("Found", PayloadFile(1));
        rig.Api.Profiles[_modId + 1] = TestMods.Profile(_modId + 1, "Elsewhere", 1, 1, [], gameId: 1234);
        var catalog = CatalogOf(rig, out _);

        var card = await catalog.Find(rig.Game, _modId.ToString(), CancellationToken.None);
        var other = await catalog.Find(rig.Game, (_modId + 1).ToString(), CancellationToken.None);
        var missing = await catalog.Find(rig.Game, "nine", CancellationToken.None);
        var gone = await catalog.Find(rig.Game, (_modId + 2).ToString(), CancellationToken.None);

        card!.Title.Should().Be("Found");
        card.Handle.Should().Be(new GameBananaHandle(_modId, null));
        other.Should().BeNull();
        missing.Should().BeNull();
        gone.Should().BeNull("a private or deleted mod has no card");
    }

    [Fact]
    public async Task ACleanSingleFileInstallsAndRecordsWhereItCameFrom()
    {
        var rig = new GameBananaRig(connection);
        rig.Api.Profiles[_modId] = ProfileWith("Installed", PayloadFile(1));
        rig.Bytes.Serve($"https://gamebanana.com/dl/{FileId(1)}", _payload);
        var catalog = CatalogOf(rig, out var core);
        var loadout = await rig.NewLoadout();
        var card = (await catalog.Find(rig.Game, _modId.ToString(), CancellationToken.None))!;

        var outcome = await catalog.Install(card, loadout, CancellationToken.None);

        var installed = outcome.Should().BeOfType<CatalogInstallOutcome.Installed>().Subject;
        var file = GameBananaLibraryFile.Load(connection.Db, installed.PerPackage.Single().Item.Id);
        (file.ModId, file.FileId, file.GameId, file.DateUpdated).Should().Be((_modId, FileId(1), 297, 1000L));
        await core.Received(1).Run(loadout, Arg.Any<string>(), Arg.Any<IReadOnlyList<ResolvedInstall>>(), Arg.Any<CancellationToken>());
        rig.Bytes.Fetched.Distinct().Should().Equal($"https://gamebanana.com/dl/{FileId(1)}");
    }

    [Fact]
    public async Task AFileTheLibraryAlreadyHoldsIsNotDownloadedAgain()
    {
        var rig = new GameBananaRig(connection);
        rig.Api.Profiles[_modId] = ProfileWith("Again", PayloadFile(1));
        rig.Bytes.Serve($"https://gamebanana.com/dl/{FileId(1)}", _payload);
        var catalog = CatalogOf(rig, out _);
        var loadout = await rig.NewLoadout();
        var card = (await catalog.Find(rig.Game, _modId.ToString(), CancellationToken.None))!;

        await catalog.Install(card, loadout, CancellationToken.None);
        var afterFirst = rig.Bytes.Fetched.Count;
        await catalog.Install(card, loadout, CancellationToken.None);

        afterFirst.Should().BeGreaterThan(0);
        rig.Bytes.Fetched.Should().HaveCount(afterFirst, "the second install found the file in the library");
    }

    [Fact]
    public async Task AnUncleanScanResultIsRefusedWithThePageLink()
    {
        var rig = new GameBananaRig(connection);
        rig.Api.Profiles[_modId] = ProfileWith("Flagged", PayloadFile(1, clean: false));
        var catalog = CatalogOf(rig, out var core);

        var outcome = await catalog.Install(new GameBananaCatalogCard(_modId), await rig.NewLoadout(), CancellationToken.None);

        var refusal = outcome.Should().BeOfType<CatalogInstallOutcome.Refused>().Subject.Why;
        refusal.Should().BeOfType<GameBananaRefusedException>();
        refusal.Message.Should().Contain($"https://gamebanana.com/mods/{_modId}");
        rig.Bytes.Fetched.Should().BeEmpty();
        await core.DidNotReceiveWithAnyArgs().Run(default, default!, default!, default);
    }

    [Fact]
    public async Task ALicenceThatForbidsInstallIsRefused()
    {
        var rig = new GameBananaRig(connection);
        rig.Api.Profiles[_modId] = TestMods.Profile(_modId, "Forbidden", 1, 1, [PayloadFile(1)], license: new GameBananaLicenseChecklist([], [], ["Download and install this Mod"]));
        var catalog = CatalogOf(rig, out _);

        var outcome = await catalog.Install(new GameBananaCatalogCard(_modId), await rig.NewLoadout(), CancellationToken.None);

        outcome.Should().BeOfType<CatalogInstallOutcome.Refused>().Which.Why.Should().BeOfType<GameBananaRefusedException>();
        rig.Bytes.Fetched.Should().BeEmpty();
    }

    [Fact]
    public async Task MirrorsAreListedAsLinksNotDownloaded()
    {
        var rig = new GameBananaRig(connection);
        rig.Api.Profiles[_modId] = TestMods.Profile(_modId, "Mirrored", 1, 1, [PayloadFile(1, clean: false)],
            mirrors: [new GameBananaAlternateSource("https://drive.example.invalid/folder", "Drive")]);
        var catalog = CatalogOf(rig, out _);

        var outcome = await catalog.Install(new GameBananaCatalogCard(_modId), await rig.NewLoadout(), CancellationToken.None);

        outcome.Should().BeOfType<CatalogInstallOutcome.Refused>().Which.Why.Message.Should().Contain("https://drive.example.invalid/folder");
        rig.Bytes.Fetched.Should().BeEmpty("a mirror is the author's link to open, never a download of ours");
    }

    [Theory]
    [InlineData("https://drive.example.invalid/a) [GameBanana](https://evil.example", "](https://evil")]
    [InlineData("javascript:alert(1)", "](javascript")]
    public async Task AMirrorCannotPlantALinkOfItsOwn(string url, string planted)
    {
        // The author writes the mirror links, so one could pose as a link to a site it does not go to.
        var rig = new GameBananaRig(connection);
        rig.Api.Profiles[_modId] = TestMods.Profile(_modId, "Mirrored", 1, 1, [PayloadFile(1, clean: false)],
            mirrors: [new GameBananaAlternateSource(url, "Drive")]);
        var catalog = CatalogOf(rig, out _);

        var outcome = await catalog.Install(new GameBananaCatalogCard(_modId), await rig.NewLoadout(), CancellationToken.None);

        outcome.Should().BeOfType<CatalogInstallOutcome.Refused>().Which.Why.Message.Should().NotContain(planted);
    }

    [Fact]
    public async Task SeveralFilesAskForAChoiceAndAFileIdPicksOne()
    {
        var rig = new GameBananaRig(connection);
        rig.Api.Profiles[_modId] = ProfileWith("Two files", PayloadFile(1), PayloadFile(2));
        rig.Bytes.Serve($"https://gamebanana.com/dl/{FileId(2)}", _payload);
        var catalog = CatalogOf(rig, out _);
        var loadout = await rig.NewLoadout();

        var asked = await catalog.Install(new GameBananaCatalogCard(_modId), loadout, CancellationToken.None);
        var picked = await catalog.Install(new GameBananaCatalogCard(_modId, FileId(2)), loadout, CancellationToken.None);

        var choose = asked.Should().BeOfType<CatalogInstallOutcome.Refused>().Which.Why.Should().BeOfType<GameBananaChooseFileException>().Subject;
        choose.Files.Select(file => file.Id).Should().BeEquivalentTo([FileId(1), FileId(2)]);
        choose.Message.Should().Contain(FileId(1).ToString()).And.Contain("--file");
        GameBananaLibraryFile.Load(connection.Db, picked.Should().BeOfType<CatalogInstallOutcome.Installed>().Subject.PerPackage.Single().Item.Id).FileId.Should().Be(FileId(2));
    }

    [Fact]
    public async Task AFileIdTheModDoesNotListIsRefused()
    {
        var rig = new GameBananaRig(connection);
        rig.Api.Profiles[_modId] = ProfileWith("One", PayloadFile(1));
        var catalog = CatalogOf(rig, out _);

        var outcome = await catalog.Install(new GameBananaCatalogCard(_modId, 42), await rig.NewLoadout(), CancellationToken.None);

        outcome.Should().BeOfType<CatalogInstallOutcome.Refused>().Which.Why.Message.Should().Contain("42");
    }

    [Fact]
    public async Task AnArchiveTheSiteFlaggedStillInstallsAndTheWarningIsAvailableBeforehand()
    {
        var rig = new GameBananaRig(connection);
        var warnings = new Dictionary<string, IReadOnlyList<string>> { ["contains_exe"] = ["Setup.exe"] };
        rig.Api.Profiles[_modId] = ProfileWith("Has an exe", PayloadFile(1, warnings: warnings));
        rig.Bytes.Serve($"https://gamebanana.com/dl/{FileId(1)}", _payload);
        var catalog = CatalogOf(rig, out _);
        var card = new GameBananaCatalogCard(_modId);

        var lines = await catalog.Warnings(card.Handle, CancellationToken.None);
        var outcome = await catalog.Install(card, await rig.NewLoadout(), CancellationToken.None);

        lines.Should().ContainSingle().Which.Should().Be("contains_exe: Setup.exe");
        outcome.Should().BeOfType<CatalogInstallOutcome.Installed>();
    }

    [Fact]
    public async Task AModThatIsNoLongerThereIsARefusal()
    {
        var rig = new GameBananaRig(connection);
        var catalog = CatalogOf(rig, out _);

        var outcome = await catalog.Install(new GameBananaCatalogCard(_modId), await rig.NewLoadout(), CancellationToken.None);

        outcome.Should().BeOfType<CatalogInstallOutcome.Refused>().Which.Why.Should().BeOfType<GameBananaUnavailableException>();
    }

    [Fact]
    public async Task ARateLimitIsARefusalWhenInstalling()
    {
        var rig = new GameBananaRig(connection);
        rig.Api.Failure = new GameBananaRateLimitedException(TimeSpan.FromMinutes(1));
        var catalog = CatalogOf(rig, out _);

        var outcome = await catalog.Install(new GameBananaCatalogCard(_modId), await rig.NewLoadout(), CancellationToken.None);

        outcome.Should().BeOfType<CatalogInstallOutcome.Refused>().Which.Why.Should().BeOfType<GameBananaRateLimitedException>();
    }

    [Fact]
    public async Task ACardOfAnotherCatalogIsNotInstalled()
    {
        var rig = new GameBananaRig(connection);
        var catalog = CatalogOf(rig, out _);
        var foreign = new CatalogCard("x", "x", "x", "", null, null, "", 0, null, false, null, new ForeignHandle());

        var act = () => catalog.Install(foreign, SilverModManager.Library.Tests.GitHub.ShelfRig.SomeLoadout, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private sealed record ForeignHandle : ICatalogHandle
    {
        public string Catalog => "Thunderstore";
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            DIHelpers.ConfigureServices(services);
            services.AddLibraryModels().AddGameInstallMetadataModel().AddLoadoutAbstractions().AddOSInterop().AddFileExtractors();
        }
    }
}

/// <summary>
/// A card as the catalog page holds it, for a test that installs by handle.
/// </summary>
internal sealed record GameBananaCatalogCard
{
    public GameBananaCatalogCard(long modId, long? fileId = null) => Handle = new GameBananaHandle(modId, fileId);

    public GameBananaHandle Handle { get; }

    public static implicit operator CatalogCard(GameBananaCatalogCard card)
        => new(card.Handle.ModId.ToString(), "Mod", "", "", null, null, "", 0, null, false, null, card.Handle);
}
