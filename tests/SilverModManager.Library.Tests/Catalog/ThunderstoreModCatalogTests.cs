using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.Thunderstore;
using SilverModManager.Networking.Thunderstore.DTOs;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Thunderstore;
using Xunit;

namespace SilverModManager.Library.Tests.Catalog;

public class ThunderstoreModCatalogTests
{
    private static readonly DateTimeOffset Jan = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PackageListing Package(string team, string name, double rating = 1, bool deprecated = false, bool nsfw = false, string version = "1.0.0",
        long downloads = 10, string? icon = null, bool pinned = false, DateTimeOffset? created = null, DateTimeOffset? updated = null)
        => new()
        {
            Name = name,
            FullName = $"{team}-{name}",
            Owner = team,
            PackageUrl = $"https://thunderstore.io/c/repo/p/{team}/{name}/",
            RatingScore = rating,
            IsDeprecated = deprecated,
            HasNsfwContent = nsfw,
            IsPinned = pinned,
            DateCreated = created,
            DateUpdated = updated,
            Versions =
            [
                new PackageVersion
                {
                    Name = name,
                    FullName = $"{team}-{name}-{version}",
                    VersionNumber = version,
                    Description = $"About {name}",
                    Icon = icon,
                    DownloadUrl = "https://thunderstore.io/package/download/",
                    Downloads = downloads,
                },
            ],
        };

    private static (ThunderstoreModCatalog Catalog, IThunderstoreCatalogSource Source, IThunderstoreInstaller Installer, IGameData Game) Create(params PackageListing[] packages)
    {
        var source = Substitute.For<IThunderstoreCatalogSource>();
        source.Load("repo", Arg.Any<CancellationToken>()).Returns(new ThunderstoreCatalog("repo", packages));
        var installer = Substitute.For<IThunderstoreInstaller>();
        var game = Substitute.For<IGameData, IThunderstoreGame>();
        ((IThunderstoreGame)game).ThunderstoreCommunity.Returns("repo");
        return (new ThunderstoreModCatalog(source, installer, NullLogger<ThunderstoreModCatalog>.Instance), source, installer, game);
    }

    private static CatalogQuery Query(ThunderstoreModCatalog catalog, string text = "", string? cursor = null, bool adult = false, string? sort = null)
        => new(text, sort is null ? catalog.Sorts[0] : catalog.Sorts.Single(s => s.Key == sort), cursor, adult);

    [Fact]
    public async Task SearchFiltersByNameOrTeamAndPagesFiftyAtATime()
    {
        var packages = Enumerable.Range(0, 60).Select(i => Package("Bulk", $"Mod{i:00}", rating: i)).Append(Package("Zehs", "Other")).ToArray();
        var (catalog, _, _, game) = Create(packages);

        var first = await catalog.Search(game, Query(catalog, "mod"), CancellationToken.None);
        first.Cards.Should().HaveCount(50);
        first.NextCursor.Should().Be("50");
        first.Total.Should().Be(60);

        var second = await catalog.Search(game, Query(catalog, "mod", cursor: first.NextCursor), CancellationToken.None);
        second.Cards.Should().HaveCount(10);
        second.NextCursor.Should().BeNull();
        first.Cards.Select(c => c.Id).Concat(second.Cards.Select(c => c.Id)).Should().OnlyHaveUniqueItems();

        var byTeam = await catalog.Search(game, Query(catalog, "zehs"), CancellationToken.None);
        byTeam.Cards.Should().ContainSingle().Which.Id.Should().Be("Zehs-Other");
    }

    [Fact]
    public async Task ACardCarriesTheListingsFieldsAndTheVersionToInstall()
    {
        var (catalog, _, _, game) = Create(Package("Zehs", "REPOLib", rating: 900, version: "4.2.0"));

        var card = (await catalog.Search(game, Query(catalog), CancellationToken.None)).Cards.Single();

        card.Title.Should().Be("REPOLib");
        card.Author.Should().Be("Zehs");
        card.Summary.Should().Be("About REPOLib");
        card.Version.Should().Be("4.2.0");
        card.Rating.Should().Be(900);
        card.Downloads.Should().Be(10);
        card.Page.Should().Be(new Uri("https://thunderstore.io/c/repo/p/Zehs/REPOLib/"));
        card.Handle.Catalog.Should().Be("Thunderstore");
    }

    [Fact]
    public async Task AdultAndDeprecatedPackagesAndOtherModManagersAreLeftOutUnlessAskedFor()
    {
        var (catalog, _, _, game) = Create(
            Package("A", "Fine"),
            Package("B", "Spicy", nsfw: true),
            Package("C", "Old", deprecated: true),
            Package("ebkr", "r2modman"));

        var hidden = await catalog.Search(game, Query(catalog), CancellationToken.None);
        var shown = await catalog.Search(game, Query(catalog, adult: true), CancellationToken.None);

        hidden.Cards.Select(c => c.Id).Should().Equal("A-Fine");
        shown.Cards.Select(c => c.Id).Should().BeEquivalentTo("A-Fine", "B-Spicy");
    }

    [Fact]
    public async Task TheSortKeyOrdersTheCards()
    {
        var (catalog, _, _, game) = Create(Package("T", "Beta", rating: 5), Package("T", "Alpha", rating: 1), Package("T", "Gamma", rating: 9));

        var byRating = await catalog.Search(game, Query(catalog, sort: "TopRated"), CancellationToken.None);
        var byName = await catalog.Search(game, Query(catalog, sort: "Name"), CancellationToken.None);

        byRating.Cards.Select(c => c.Title).Should().Equal("Gamma", "Beta", "Alpha");
        byName.Cards.Select(c => c.Title).Should().Equal("Alpha", "Beta", "Gamma");
    }

    private static PackageListing[] Sortable() =>
    [
        Package("a", "Alpha", rating: 10, downloads: 500, created: Jan.AddDays(30), updated: Jan.AddDays(5)),
        Package("b", "Bravo", rating: 90, downloads: 20, created: Jan.AddDays(10), updated: Jan.AddDays(40)),
        Package("c", "Charlie", rating: 50, downloads: 3000, created: Jan.AddDays(20), updated: Jan.AddDays(20)),
        Package("d", "Pinned_Delta", rating: 1, downloads: 1, created: Jan, updated: Jan, pinned: true),
    ];

    private async Task<string[]> Ids(ThunderstoreModCatalog catalog, IGameData game, string text = "", string? sort = null)
        => (await catalog.Search(game, Query(catalog, text, sort: sort), CancellationToken.None)).Cards.Select(c => c.Id).ToArray();

    [Fact]
    public async Task TopRatedIsTheDefaultAndListsPinnedPackagesFirst()
    {
        var (catalog, _, _, game) = Create(Sortable());

        catalog.Sorts[0].Key.Should().Be("TopRated");
        (await Ids(catalog, game)).Should().Equal("d-Pinned_Delta", "b-Bravo", "c-Charlie", "a-Alpha");
    }

    [Theory]
    [InlineData("Downloads", new[] { "c-Charlie", "a-Alpha", "b-Bravo", "d-Pinned_Delta" })]
    [InlineData("Updated", new[] { "b-Bravo", "c-Charlie", "a-Alpha", "d-Pinned_Delta" })]
    [InlineData("Newest", new[] { "a-Alpha", "c-Charlie", "b-Bravo", "d-Pinned_Delta" })]
    [InlineData("Name", new[] { "a-Alpha", "b-Bravo", "c-Charlie", "d-Pinned_Delta" })]
    public async Task EachSortOrdersThePackagesAndLeavesPinnedPackagesWhereTheirValueFalls(string sort, string[] expected)
    {
        var (catalog, _, _, game) = Create(Sortable());

        (await Ids(catalog, game, sort: sort)).Should().Equal(expected);
    }

    [Fact]
    public async Task NameSortIgnoresCaseAndBreaksTiesByFullName()
    {
        var (catalog, _, _, game) = Create(Package("z", "same"), Package("y", "Same"), Package("x", "apple"), Package("w", "Banana"));

        (await Ids(catalog, game, sort: "Name")).Should().Equal("x-apple", "w-Banana", "y-Same", "z-same");
    }

    [Fact]
    public async Task SortingKeepsTheSearchFilter()
    {
        var (catalog, _, _, game) = Create(Sortable());

        (await Ids(catalog, game, "a", "Downloads")).Should().Equal("c-Charlie", "a-Alpha", "b-Bravo", "d-Pinned_Delta");
        (await Ids(catalog, game, "bravo", "Downloads")).Should().Equal("b-Bravo");
    }

    [Fact]
    public async Task APackageWithoutDatesSortsLastByDate()
    {
        var (catalog, _, _, game) = Create(Package("a", "Undated"), Package("b", "Dated", updated: Jan));

        (await Ids(catalog, game, sort: "Updated")).Should().Equal("b-Dated", "a-Undated");
    }

    [Fact]
    public async Task SearchStillMatchesTheOwnerAndTheRawFullName()
    {
        var (catalog, _, _, game) = Create(Package("glarmer", "PEAK_Unlimited"), Package("Zehs", "REPOLib", rating: 2));

        (await Ids(catalog, game, "glarmer")).Should().Equal("glarmer-PEAK_Unlimited");
        (await Ids(catalog, game, "PEAK_Unlimited")).Should().Equal("glarmer-PEAK_Unlimited");
    }

    [Fact]
    public async Task ACardCarriesTheReadableNameTheIconAndTheDownloadsOfEveryVersion()
    {
        var package = Package("glarmer", "PEAK_Unlimited", icon: "https://gcdn.thunderstore.io/new.png") with
        {
            Versions =
            [
                new PackageVersion { Name = "PEAK_Unlimited", FullName = "glarmer-PEAK_Unlimited-2.0.0", VersionNumber = "2.0.0", DownloadUrl = "https://example.com/2", Icon = "https://gcdn.thunderstore.io/new.png", Downloads = 40 },
                new PackageVersion { Name = "PEAK_Unlimited", FullName = "glarmer-PEAK_Unlimited-1.0.0", VersionNumber = "1.0.0", DownloadUrl = "https://example.com/1", Icon = "https://gcdn.thunderstore.io/old.png", Downloads = 2 },
            ],
        };
        var (catalog, _, _, game) = Create(package);

        var card = (await catalog.Search(game, Query(catalog), CancellationToken.None)).Cards.Single();

        card.Title.Should().Be("PEAK Unlimited");
        card.Id.Should().Be("glarmer-PEAK_Unlimited");
        card.Downloads.Should().Be(42);
        card.Icon.Should().Be(new Uri("https://gcdn.thunderstore.io/new.png"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    public async Task ACardWithoutAUsableIconHasNone(string? icon)
    {
        var (catalog, _, _, game) = Create(Package("Zehs", "REPOLib", icon: icon));

        (await catalog.Search(game, Query(catalog), CancellationToken.None)).Cards.Single().Icon.Should().BeNull();
    }

    [Fact]
    public async Task APackageTheCatalogGivesNoAddressForLinksToItsPageInTheCommunity()
    {
        var (catalog, _, _, game) = Create(Package("BepInEx", "BepInExPack") with { PackageUrl = null });

        (await catalog.Search(game, Query(catalog), CancellationToken.None)).Cards.Single().Page
            .Should().Be(new Uri("https://thunderstore.io/c/repo/p/BepInEx/BepInExPack/"));
    }

    [Fact]
    public void TheSortChoicesUseTheSharedWording()
    {
        var (catalog, _, _, _) = Create();

        catalog.Sorts.Select(c => c.Label).Should().Equal("Top rated", "Most downloaded", "Recently updated", "Newest", "Name");
        catalog.Sorts.Select(c => c.Key).Should().Equal("TopRated", "Downloads", "Updated", "Newest", "Name");
    }

    [Fact]
    public async Task AStaleIndexIsRefreshedAndAFailedRefreshFallsBackToTheCachedCopy()
    {
        var (catalog, source, _, game) = Create(Package("A", "Fine"));
        source.IsStale("repo").Returns(true);
        source.Refresh("repo", Arg.Any<CancellationToken>()).Returns(Task.FromException(new HttpRequestException("offline")));

        var page = await catalog.Search(game, Query(catalog), CancellationToken.None);

        page.Cards.Should().ContainSingle();
        page.FromCache.Should().BeTrue();
    }

    [Fact]
    public async Task AFreshIndexIsNotRefreshedAndAStaleOneIsRefreshedBeforeTheSearch()
    {
        var (catalog, source, _, game) = Create(Package("A", "Fine"));

        source.IsStale("repo").Returns(false);
        await catalog.Search(game, Query(catalog), CancellationToken.None);
        await source.DidNotReceiveWithAnyArgs().Refresh(default!, default);

        source.IsStale("repo").Returns(true);
        var page = await catalog.Search(game, Query(catalog), CancellationToken.None);
        await source.Received(1).Refresh("repo", Arg.Any<CancellationToken>());
        page.FromCache.Should().BeFalse();
    }

    [Fact]
    public async Task OtherModManagersStayHiddenWhateverTheirCaseOrTheSearchText()
    {
        var (catalog, _, _, game) = Create(
            Package("EBKR", "R2ModMan"),
            Package("kesomannen", "galemodmanager"),
            Package("someone", "r2modman_Profile_Sync"));

        (await catalog.Search(game, Query(catalog, "r2modman"), CancellationToken.None)).Cards.Select(c => c.Id).Should().Equal("someone-r2modman_Profile_Sync");
        (await catalog.Search(game, Query(catalog, "gale"), CancellationToken.None)).Cards.Should().BeEmpty();
        (await catalog.Find(game, "EBKR-R2ModMan", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public void ItServesOnlyThunderstoreGames()
    {
        var (catalog, _, _, game) = Create();

        catalog.Serves(game).Should().BeTrue();
        catalog.Serves(Substitute.For<IGameData>()).Should().BeFalse();
        catalog.InstallLabel(game).Should().Be("Install");
    }

    [Fact]
    public async Task InstallMapsEveryDependencyOutcomeOntoPerPackage()
    {
        var (catalog, _, installer, game) = Create(Package("Zehs", "REPOLib", version: "4.2.0"));
        var loadout = LoadoutId.From(EntityId.From(0x1234));
        IReadOnlyList<InstallOutcome> outcomes =
        [
            new("Zehs-REPOLib-4.2.0", default, true, LoadoutPackageOutcome.Installed, false),
            new("BepInEx-BepInExPack-5.4.2305", default, true, LoadoutPackageOutcome.Installed, true),
        ];
        installer.InstallAsync(Arg.Any<ThunderstoreVersionRef>(), loadout, Arg.Any<CancellationToken>()).Returns(outcomes);
        var card = (await catalog.Search(game, Query(catalog), CancellationToken.None)).Cards.Single();

        var result = await catalog.Install(card, loadout, CancellationToken.None);

        result.Should().BeOfType<CatalogInstallOutcome.Installed>().Which.PerPackage.Should().HaveCount(2);
        await installer.Received(1).InstallAsync(Arg.Is<ThunderstoreVersionRef>(v => v.ToString() == "Zehs-REPOLib-4.2.0"), loadout, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindReturnsThePackageNamedByItsNamespaceAndName()
    {
        var (catalog, _, _, game) = Create(Package("Zehs", "REPOLib", version: "4.2.0"), Package("ebkr", "r2modman"));

        var card = await catalog.Find(game, "Zehs-REPOLib", CancellationToken.None);

        card.Should().NotBeNull();
        card!.Title.Should().Be("REPOLib");
        card.Version.Should().Be("4.2.0");
        (await catalog.Find(game, "Zehs-Missing", CancellationToken.None)).Should().BeNull();
        (await catalog.Find(game, "ebkr-r2modman", CancellationToken.None)).Should().BeNull("another mod manager is never listed");
    }

    [Fact]
    public async Task FindSettlesATeamNameWithADashByLookingEachSplitUp()
    {
        var (catalog, _, _, game) = Create(Package("Some-Team", "Pkg"));

        (await catalog.Find(game, "Some-Team-Pkg", CancellationToken.None))!.Id.Should().Be("Some-Team-Pkg");
    }

    [Fact]
    public async Task APackageWithoutANameableVersionIsRefusedNotInstalled()
    {
        var (catalog, _, installer, game) = Create(Package("Zehs", "Broken", version: "not-a-version"));
        var card = (await catalog.Search(game, Query(catalog), CancellationToken.None)).Cards.Single();

        var result = await catalog.Install(card, LoadoutId.From(EntityId.From(0x1234)), CancellationToken.None);

        result.Should().BeOfType<CatalogInstallOutcome.Refused>();
        await installer.DidNotReceiveWithAnyArgs().InstallAsync(default, default, CancellationToken.None);
    }

    [Fact]
    public async Task ADisabledGroupRefusalIsAnOutcomeNotAnException()
    {
        var (catalog, _, installer, game) = Create(Package("Zehs", "REPOLib"));
        var loadout = LoadoutId.From(EntityId.From(0x1234));
        installer.InstallAsync(Arg.Any<ThunderstoreVersionRef>(), loadout, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<InstallOutcome>>(_ => throw new LibraryItemInDisabledGroupException("Zehs-REPOLib", "Group"));
        var card = (await catalog.Search(game, Query(catalog), CancellationToken.None)).Cards.Single();

        var result = await catalog.Install(card, loadout, CancellationToken.None);

        result.Should().BeOfType<CatalogInstallOutcome.Refused>().Which.Why.Should().BeOfType<LibraryItemInDisabledGroupException>();
    }
}
