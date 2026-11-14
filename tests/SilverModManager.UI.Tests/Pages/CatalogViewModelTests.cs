using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.App.UI.Controls.Cards;
using SilverModManager.App.UI.Pages.Catalog;
using SilverModManager.App.UI.Settings;
using SilverModManager.App.UI.Windows;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;

namespace SilverModManager.UI.Tests.Pages;

public class CatalogViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed record Handle(string Name) : ICatalogHandle
    {
        public string Catalog => "Fake";
    }

    private sealed class Refusal() : UserFacingException("Not for you")
    {
        public override string Title => "Nothing was installed";
    }

    private sealed class FakeCatalog : IModCatalog
    {
        public string DisplayName => "Fake";
        public IReadOnlyList<CatalogSort> Sorts { get; } = [new("Best", "Best"), new("Newest", "Newest")];
        public IReadOnlyList<CatalogCard> Cards { get; set; } = [];
        public CatalogInstallOutcome? Outcome { get; set; }
        public Exception? InstallFailure { get; set; }
        public Exception? SearchFailure { get; set; }
        public bool FromCache { get; set; }
        public int PageSize { get; set; } = 100;
        public List<CatalogQuery> Queries { get; } = [];
        public List<CatalogCard> Installed { get; } = [];
        public string Label { get; set; } = "Install";

        public bool Serves(IGameData game) => true;
        public string InstallLabel(IGameData game) => Label;

        public Task<CatalogPage> Search(IGameData game, CatalogQuery query, CancellationToken cancellationToken)
        {
            Queries.Add(query);
            if (SearchFailure is not null) return Task.FromException<CatalogPage>(SearchFailure);

            var matches = Cards.Where(card => (query.IncludeAdult || !card.IsAdult) && card.Title.Contains(query.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
            var start = query.Cursor is null ? 0 : int.Parse(query.Cursor);
            var page = matches.Skip(start).Take(PageSize).ToArray();
            var next = start + page.Length;
            return Task.FromResult(new CatalogPage(page, next < matches.Length ? next.ToString() : null, matches.Length, FromCache));
        }

        public Task<CatalogCard?> Find(IGameData game, string id, CancellationToken cancellationToken)
            => Task.FromResult(Cards.FirstOrDefault(card => card.Id == id));

        public Task<CatalogInstallOutcome> Install(CatalogCard card, LoadoutId loadout, CancellationToken cancellationToken)
        {
            Installed.Add(card);
            if (InstallFailure is not null) return Task.FromException<CatalogInstallOutcome>(InstallFailure);
            return Task.FromResult(Outcome ?? new CatalogInstallOutcome.Installed([]));
        }
    }

    private sealed record Opened(CatalogViewModel Vm, FakeCatalog Catalog, IErrorPresenter Errors, IWindowNotificationService Toasts, IOSInterop Os, GameId Game);

    private static CatalogCard Card(string title, bool adult = false, double? rating = 5)
        => new(title, title, "someone", $"About {title}", null, new Uri($"https://example.test/{title}"), "1.0", 1200, rating, adult, null, new Handle(title));

    private async Task<Opened> Open(FakeCatalog catalog, IStoreSortMemory? sortMemory = null, IAdultContentSetting? adult = null)
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Catalog loadout");
        var errors = Substitute.For<IErrorPresenter>();
        var toasts = Substitute.For<IWindowNotificationService>();
        var os = Substitute.For<IOSInterop>();
        var vm = new CatalogViewModel(
            Provider.GetRequiredService<IWindowManager>(),
            new ModCatalogs([catalog]),
            Connection,
            Substitute.For<IThumbnailLoader>(),
            sortMemory ?? new InMemoryStoreSortMemory(),
            adult ?? new FakeAdultContentSetting(),
            errors,
            toasts,
            os,
            NullLogger<CatalogViewModel>.Instance);
        vm.Initialize(catalog.DisplayName, loadout.LoadoutId);
        await vm.SearchAsync(CancellationToken.None);
        loadout.TryGetGame(out var game).Should().BeTrue();
        return new Opened(vm, catalog, errors, toasts, os, game.GameId);
    }

    private static string[] Titles(CatalogViewModel vm) => vm.Rows.Select(row => row.Title).ToArray();

    [Fact]
    public async Task TheFirstPageLoadsWithTheRememberedSort()
    {
        var memory = new InMemoryStoreSortMemory();
        var first = await Open(new FakeCatalog { Cards = [Card("A")] }, memory);
        first.Vm.SortKey.Should().Be("Best", "the first of the catalog's sorts is the default");
        first.Vm.SortKey = "Newest";

        var second = await Open(new FakeCatalog { Cards = [Card("A")] }, memory);

        second.Vm.SortKey.Should().Be("Newest");
        second.Catalog.Queries.Should().ContainSingle().Which.Sort.Key.Should().Be("Newest");
    }

    [Fact]
    public async Task ASavedSortTheCatalogNoLongerHasFallsBackToTheFirst()
    {
        var memory = new InMemoryStoreSortMemory();
        var seeded = await Open(new FakeCatalog(), memory);
        memory.Remember("Fake", seeded.Game.ToString(), "Retired");

        var reopened = await Open(new FakeCatalog(), memory);

        reopened.Vm.SortKey.Should().Be("Best");
    }

    [Fact]
    public async Task TheSortIsRememberedPerGame()
    {
        var memory = new InMemoryStoreSortMemory();
        memory.Remember("Fake", "some-other-game", "Newest");

        var opened = await Open(new FakeCatalog(), memory);

        opened.Vm.SortKey.Should().Be("Best");
    }

    [Fact]
    public async Task SearchTextNarrowsTheListThroughTheCatalog()
    {
        var opened = await Open(new FakeCatalog { Cards = [Card("Alpha"), Card("Bravo")] });

        opened.Vm.SearchText = " bravo ";
        await opened.Vm.SearchAsync(CancellationToken.None);

        Titles(opened.Vm).Should().Equal("Bravo");
        opened.Catalog.Queries.Last().Text.Should().Be("bravo");
    }

    [Fact]
    public async Task AdultCardsAreHiddenUnlessTheSettingShowsThem()
    {
        var cards = new[] { Card("Safe"), Card("Spicy", adult: true) };
        var hidden = await Open(new FakeCatalog { Cards = cards });
        var shown = await Open(new FakeCatalog { Cards = cards }, adult: new FakeAdultContentSetting(true));

        Titles(hidden.Vm).Should().Equal("Safe");
        Titles(shown.Vm).Should().Equal("Safe", "Spicy");
    }

    [Fact]
    public async Task FlippingTheAdultSettingListsTheOpenPageAgain()
    {
        var setting = new FakeAdultContentSetting();
        var opened = await Open(new FakeCatalog { Cards = [Card("Safe"), Card("Spicy", adult: true)] }, adult: setting);
        using var active = await OnUi(() => Task.FromResult(opened.Vm.Activator.Activate()));

        setting.ShowAdultContent = true;
        await EventuallyOnUi(() => Titles(opened.Vm).Should().Equal("Safe", "Spicy"));

        setting.ShowAdultContent = false;
        await EventuallyOnUi(() => Titles(opened.Vm).Should().Equal("Safe"));
    }

    [Fact]
    public async Task LoadMoreAppendsTheNextPageUntilTheCatalogIsExhausted()
    {
        var opened = await Open(new FakeCatalog { Cards = Enumerable.Range(0, 5).Select(i => Card($"Mod{i}")).ToArray(), PageSize = 2 });
        opened.Vm.HasMore.Should().BeTrue();

        await opened.Vm.LoadMoreAsync(CancellationToken.None);
        await opened.Vm.LoadMoreAsync(CancellationToken.None);

        Titles(opened.Vm).Should().Equal("Mod0", "Mod1", "Mod2", "Mod3", "Mod4");
        opened.Vm.HasMore.Should().BeFalse();
        opened.Catalog.Queries.Select(q => q.Cursor).Should().Equal(null, "2", "4");
    }

    [Fact]
    public async Task ACardShowsTheRatingOnlyWhenTheSourceHasOne()
    {
        var opened = await Open(new FakeCatalog { Cards = [Card("Rated", rating: 112), Card("Unrated", rating: null)], Label = "Subscribe" });

        opened.Vm.Rows[0].Details.Should().Be("1.0  ·  rating 112  ·  1.2K downloads");
        opened.Vm.Rows[1].Details.Should().Be("1.0  ·  1.2K downloads");
        opened.Vm.Rows[0].OwnerLine.Should().Be("by someone");
        opened.Vm.Rows[0].InstallLabel.Should().Be("Subscribe");
    }

    [Fact]
    public async Task AFailedSearchBecomesTheErrorTextAndAnEmptyList()
    {
        var opened = await Open(new FakeCatalog { SearchFailure = new HttpRequestException("offline") });

        opened.Vm.ErrorText.Should().Contain("Fake").And.Contain("offline");
        opened.Vm.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ASavedCopyOfTheListIsSaidSo()
    {
        var opened = await Open(new FakeCatalog { Cards = [Card("A")], FromCache = true });

        opened.Vm.StatusText.Should().Contain("saved");
        opened.Vm.Rows.Should().HaveCount(1);
    }

    [Fact]
    public async Task ARefusedInstallGoesToTheErrorPresenterNotAThrow()
    {
        var refusal = new Refusal();
        var opened = await Open(new FakeCatalog { Cards = [Card("A")], Outcome = new CatalogInstallOutcome.Refused(refusal) });

        await opened.Vm.InstallCommand.Execute(opened.Vm.Rows[0]);

        await opened.Errors.Received(1).Report(refusal, Arg.Any<string>());
        opened.Toasts.DidNotReceiveWithAnyArgs().ShowToast(default!);
    }

    [Fact]
    public async Task AnUnexpectedInstallFailureIsReportedToo()
    {
        var failure = new InvalidOperationException("boom");
        var opened = await Open(new FakeCatalog { Cards = [Card("A")], InstallFailure = failure });

        await opened.Vm.InstallCommand.Execute(opened.Vm.Rows[0]);

        await opened.Errors.Received(1).Report(failure, Arg.Any<string>());
    }

    [Fact]
    public async Task ASubscribedOutcomeShowsItsNote()
    {
        var opened = await Open(new FakeCatalog { Cards = [Card("A")], Outcome = new CatalogInstallOutcome.Subscribed("Start the game to download it.") });

        await opened.Vm.InstallCommand.Execute(opened.Vm.Rows[0]);

        opened.Toasts.Received(1).ShowToast("Start the game to download it.", ToastNotificationVariant.Success);
    }

    [Fact]
    public async Task AnInstalledOutcomeToastsThatTheModWasAdded()
    {
        var opened = await Open(new FakeCatalog { Cards = [Card("A")] });

        await opened.Vm.InstallCommand.Execute(opened.Vm.Rows[0]);

        opened.Toasts.Received(1).ShowToast(Arg.Is<string>(text => text.Contains("A") && text.Contains("added")), ToastNotificationVariant.Success);
    }

    [Fact]
    public async Task AnOutcomeThatSendsTheUserToTheWebsiteOpensThePage()
    {
        var page = new Uri("https://example.test/files");
        var opened = await Open(new FakeCatalog { Cards = [Card("A")], Outcome = new CatalogInstallOutcome.OpenedOnWebsite(page) });

        await opened.Vm.InstallCommand.Execute(opened.Vm.Rows[0]);

        opened.Os.Received(1).OpenUri(page);
    }

    [Fact]
    public async Task OpenOpensTheCardsPage()
    {
        var opened = await Open(new FakeCatalog { Cards = [Card("A")] });

        await opened.Vm.OpenCommand.Execute(opened.Vm.Rows[0]);

        opened.Os.Received(1).OpenUri(new Uri("https://example.test/A"));
    }
}
