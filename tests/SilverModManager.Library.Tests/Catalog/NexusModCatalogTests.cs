using DynamicData.Kernel;
using FluentAssertions;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Library;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using Xunit;

namespace SilverModManager.Library.Tests.Catalog;

public class NexusModCatalogTests
{
    private static readonly NexusModsGameId EldenRing = NexusModsGameId.From(4333);
    private static readonly LoadoutId Loadout = LoadoutId.From(EntityId.From(0x1234));

    private sealed record Fakes(
        NexusModCatalog Catalog,
        INexusModsBrowser Browser,
        INexusModsInstaller Installer,
        ILoadoutInstallCore Core,
        ILoginManager Login,
        IGameData Game);

    private static Fakes Create()
    {
        var browser = Substitute.For<INexusModsBrowser>();
        var installer = Substitute.For<INexusModsInstaller>();
        var core = Substitute.For<ILoadoutInstallCore>();
        var login = Substitute.For<ILoginManager>();
        var domains = Substitute.For<IGameDomainToGameIdMappingCache>();
        domains.GetDomain(EldenRing).Returns(GameDomain.From("eldenring"));
        var game = Substitute.For<IGameData>();
        game.NexusModsGameId.Returns(Optional.Some(EldenRing));
        return new Fakes(new NexusModCatalog(browser, installer, core, login, domains), browser, installer, core, login, game);
    }

    private static NexusModsSearchHit Hit(uint id, string name, bool adult = false)
        => new(ModId.From(id), $"uid{id}", name, $"About {name}", "an author", "1.2", 7, 900, new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), null, null, adult);

    private static CatalogQuery Query(NexusModCatalog catalog, string text = "", string? cursor = null, bool adult = false)
        => new(text, catalog.Sorts[0], cursor, adult);

    [Fact]
    public async Task SearchReturnsCardsWithTheModPageAsPage()
    {
        var f = Create();
        f.Browser.Search(Arg.Any<NexusModsSearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(new NexusModsSearchPage([Hit(10, "Better Combat")], 0, 120));

        var page = await f.Catalog.Search(f.Game, Query(f.Catalog, "combat"), CancellationToken.None);

        var card = page.Cards.Should().ContainSingle().Subject;
        card.Id.Should().Be("10");
        card.Title.Should().Be("Better Combat");
        card.Author.Should().Be("an author");
        card.Downloads.Should().Be(900);
        card.Page.Should().Be(NexusModsUrlBuilder.GetModUri(GameDomain.From("eldenring"), ModId.From(10)));
        page.Total.Should().Be(120);
        page.NextCursor.Should().Be("1", "the next page starts after the hits already listed");
        await f.Browser.Received(1).Search(Arg.Is<NexusModsSearchQuery>(q => q.SearchText == "combat" && q.Game == EldenRing && q.Offset == 0 && !q.IncludeAdult), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheCursorIsTheOffsetOfTheNextPage()
    {
        var f = Create();
        f.Browser.Search(Arg.Any<NexusModsSearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(new NexusModsSearchPage([Hit(11, "Late")], 40, 41));

        var page = await f.Catalog.Search(f.Game, Query(f.Catalog, cursor: "40"), CancellationToken.None);

        page.NextCursor.Should().BeNull();
        await f.Browser.Received(1).Search(Arg.Is<NexusModsSearchQuery>(q => q.Offset == 40), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ItServesGamesThatAreOnNexusMods()
    {
        var f = Create();
        var offNexus = Substitute.For<IGameData>();
        offNexus.NexusModsGameId.Returns(Optional<NexusModsGameId>.None);

        f.Catalog.Serves(f.Game).Should().BeTrue();
        f.Catalog.Serves(offNexus).Should().BeFalse();
        f.Catalog.DisplayName.Should().Be("Nexus Mods");
    }

    private static void GivenOneMainFile(Fakes f, bool inLibrary)
    {
        f.Installer.Files(EldenRing, ModId.From(10), Arg.Any<CancellationToken>())
            .Returns([new NexusModsFileRow(FileId.From(77), "main.zip", "1.0", NexusModsFileCategory.Main, 10, DateTimeOffset.UnixEpoch)]);
        f.Installer.DownloadPage(EldenRing, ModId.From(10), FileId.From(77)).Returns(new Uri("nxm://eldenring/mods/10/files/77"));
        f.Installer.Resolve(EldenRing, ModId.From(10), FileId.From(77), "main.zip", false, Loadout)
            .Returns(new ResolvedInstall("main.zip", false, inLibrary ? default(LibraryItem.ReadOnly) : null, _ => throw new InvalidOperationException()));
    }

    private static ValueTask<UserInfo?> User(UserRole role)
        => new(new UserInfo { UserId = UserId.From(1), Name = "tester", UserRole = role, AvatarUrl = null });

    private static CatalogCard Card(NexusModCatalog catalog)
        => new("10", "Better Combat", "an author", "", null, null, "1.0", 0, null, false, null, new NexusCardHandle(EldenRing, ModId.From(10)));

    [Fact]
    public async Task FindBuildsTheCardOfANumericModId()
    {
        var f = Create();

        var card = await f.Catalog.Find(f.Game, "10", CancellationToken.None);

        card.Should().NotBeNull();
        card!.Handle.Should().Be(new NexusCardHandle(EldenRing, ModId.From(10)));
        card.Page.Should().Be(NexusModsUrlBuilder.GetModUri(GameDomain.From("eldenring"), ModId.From(10)));
        (await f.Catalog.Find(f.Game, "not-a-number", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task AFreeAccountIsSentToTheSiteWhenTheFileIsNotInTheLibrary()
    {
        var f = Create();
        GivenOneMainFile(f, inLibrary: false);
        f.Login.GetUserInfoAsync(Arg.Any<CancellationToken>()).Returns(User(UserRole.Free));

        var outcome = await f.Catalog.Install(Card(f.Catalog), Loadout, CancellationToken.None);

        outcome.Should().BeOfType<CatalogInstallOutcome.OpenedOnWebsite>().Which.Page.Should().Be(new Uri("nxm://eldenring/mods/10/files/77"));
        await f.Core.DidNotReceiveWithAnyArgs().Run(default, default!, default!, default);
    }

    [Fact]
    public async Task APremiumAccountInstallsTheMainFileThroughTheSharedCore()
    {
        var f = Create();
        GivenOneMainFile(f, inLibrary: false);
        f.Login.GetUserInfoAsync(Arg.Any<CancellationToken>()).Returns(User(UserRole.Premium));
        IReadOnlyList<InstallOutcome> done = [new("Better Combat", default, true, LoadoutPackageOutcome.Installed, false)];
        f.Core.Run(Loadout, "nexus:10", Arg.Any<IReadOnlyList<ResolvedInstall>>(), Arg.Any<CancellationToken>()).Returns(done);

        var outcome = await f.Catalog.Install(Card(f.Catalog), Loadout, CancellationToken.None);

        outcome.Should().BeOfType<CatalogInstallOutcome.Installed>().Which.PerPackage.Should().HaveCount(1);
    }

    [Fact]
    public async Task AFileTheLibraryAlreadyHoldsInstallsForAFreeAccountToo()
    {
        var f = Create();
        GivenOneMainFile(f, inLibrary: true);
        f.Login.GetUserInfoAsync(Arg.Any<CancellationToken>()).Returns(User(UserRole.Free));
        f.Core.Run(Loadout, "nexus:10", Arg.Any<IReadOnlyList<ResolvedInstall>>(), Arg.Any<CancellationToken>()).Returns([]);

        var outcome = await f.Catalog.Install(Card(f.Catalog), Loadout, CancellationToken.None);

        outcome.Should().BeOfType<CatalogInstallOutcome.Installed>();
    }

    [Fact]
    public async Task ACardFoundByIdInstallsUnderTheMainFilesName()
    {
        var f = Create();
        GivenOneMainFile(f, inLibrary: true);
        f.Login.GetUserInfoAsync(Arg.Any<CancellationToken>()).Returns(User(UserRole.Free));
        f.Core.Run(Loadout, "nexus:10", Arg.Any<IReadOnlyList<ResolvedInstall>>(), Arg.Any<CancellationToken>()).Returns([]);
        var card = await f.Catalog.Find(f.Game, "10", CancellationToken.None);

        await f.Catalog.Install(card!, Loadout, CancellationToken.None);

        // The card of an id has no real title, so the outcome names the file the way nexus install does.
        await f.Core.Received(1).Run(Loadout, "nexus:10", Arg.Is<IReadOnlyList<ResolvedInstall>>(items => items.Single().Name == "main.zip"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AModWithoutOneMainFileSendsTheUserToItsFilesTab()
    {
        var f = Create();
        f.Installer.Files(EldenRing, ModId.From(10), Arg.Any<CancellationToken>()).Returns([]);

        var outcome = await f.Catalog.Install(Card(f.Catalog), Loadout, CancellationToken.None);

        outcome.Should().BeOfType<CatalogInstallOutcome.OpenedOnWebsite>().Which.Page.Query.Should().Be("?tab=files");
    }
}
