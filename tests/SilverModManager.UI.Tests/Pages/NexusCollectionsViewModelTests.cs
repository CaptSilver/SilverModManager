using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.Pages.NexusCatalog;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.CLI.Types;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

public class NexusCollectionsViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class Fixture
    {
        public NexusCollectionsViewModel Vm = null!;
        public INexusCollectionsBrowser Browser = null!;
        public IIpcProtocolHandler Nxm = null!;
        public IOSInterop OsInterop = null!;
        public NexusModsGameId Game;
        public Exception? SearchFailure;
        public List<NexusCollectionsSearchQuery> Queries { get; } = [];
        public List<PageData> OpenedPages { get; } = [];
        public LoadoutId LoadoutId;
    }

    private static NexusCollectionSearchHit Hit(string slug, string name = "Some collection", int revision = 7, bool adult = false)
        => new(CollectionSlug.From(slug), GameDomain.From("cyberpunk2077"), name, $"{name} summary", "curator", 12, 3400, 45, 123_456_789UL,
            RevisionNumber.From((ulong)revision), new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), new Uri($"https://img.example/{slug}.png"), adult);

    private static NexusCollectionsSearchPage Page(int offset, int total, params string[] slugs)
        => new(slugs.Select(slug => Hit(slug)).ToArray(), offset, total);

    private async Task<Fixture> Open(Func<NexusCollectionsSearchQuery, NexusCollectionsSearchPage>? pages = null)
    {
        var row = await SeedLoadoutRow(GameStore.Steam, "Nexus collections loadout");
        var fixture = new Fixture { LoadoutId = row.LoadoutId };
        fixture.Game = GameInstallMetadata.GameId.Get(Loadout.Load(Connection.Db, row.LoadoutId).Installation);

        fixture.Browser = Substitute.For<INexusCollectionsBrowser>();
        fixture.Browser.Search(default!, default).ReturnsForAnyArgs(call =>
        {
            var query = call.Arg<NexusCollectionsSearchQuery>();
            fixture.Queries.Add(query);
            if (fixture.SearchFailure is not null) return Task.FromException<NexusCollectionsSearchPage>(fixture.SearchFailure);
            return Task.FromResult(pages?.Invoke(query) ?? Page(query.Offset, 1, "abc123"));
        });

        fixture.Nxm = Substitute.For<IIpcProtocolHandler>();
        fixture.Nxm.Protocol.Returns("nxm");
        fixture.OsInterop = Substitute.For<IOSInterop>();

        fixture.Vm = new NexusCollectionsViewModel(
            Connection,
            fixture.Browser,
            fixture.OsInterop,
            Provider.GetServices<IGameData>(),
            [Substitute.For<IIpcProtocolHandler>(), fixture.Nxm],
            Substitute.For<SilverModManager.App.UI.Controls.Cards.IThumbnailLoader>(),
            NullLogger<NexusCollectionsViewModel>.Instance)
        {
            LoadoutId = row.LoadoutId,
            OpenPage = fixture.OpenedPages.Add,
        };
        return fixture;
    }

    // The database outlives a test, so every seeded collection gets an id and a slug no other test uses.
    private static long _nextCollectionId = 9_000_000;

    private async Task<CollectionRevisionMetadataId> SeedLibraryRevision(string slug, ulong revisionNumber)
    {
        var collectionId = (ulong)Interlocked.Increment(ref _nextCollectionId);
        using var tx = Connection.BeginTransaction();
        var author = new SilverModManager.Abstractions.NexusModsLibrary.Models.User.New(tx) { Name = "Author", NexusId = collectionId };
        var collection = new CollectionMetadata.New(tx)
        {
            CollectionId = CollectionId.From(collectionId),
            Slug = CollectionSlug.From(slug),
            Name = "In the library",
            GameId = NexusModsGameId.From(3333),
            AuthorId = author,
        };
        var revision = new CollectionRevisionMetadata.New(tx)
        {
            RevisionId = RevisionId.From(collectionId),
            RevisionNumber = RevisionNumber.From(revisionNumber),
            CollectionId = collection,
        };
        var result = await tx.Commit();
        return result.Remap(revision).Id;
    }

    [Fact]
    public async Task Search_LoadsTheFirstPageOfTheLoadoutsGameMostEndorsedFirst()
    {
        var f = await Open(pages: q => Page(q.Offset, 2, "abc123", "def456"));

        await f.Vm.SearchAsync(CancellationToken.None);

        var query = f.Queries.Should().ContainSingle().Subject;
        query.Game.Should().Be(f.Game);
        query.SearchText.Should().BeEmpty();
        query.Sort.Should().Be(NexusCollectionsSort.Endorsements);
        query.Offset.Should().Be(0);
        query.Count.Should().Be(40);
        query.IncludeAdult.Should().BeFalse();
        f.Vm.Collections.Select(c => c.Hit.Slug.Value).Should().Equal("abc123", "def456");
        f.Vm.ErrorText.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_SendsTheTrimmedTextAndTheChosenSort()
    {
        var f = await Open();
        f.Vm.SearchText = "  vanilla plus ";
        f.Vm.Sort = NexusCollectionsSort.Updated;

        await f.Vm.SearchAsync(CancellationToken.None);

        f.Queries.Should().ContainSingle().Which.Should().Match<NexusCollectionsSearchQuery>(q => q.SearchText == "vanilla plus" && q.Sort == NexusCollectionsSort.Updated);
    }

    [Fact]
    public async Task LoadMore_AppendsTheNextPageFromTheCurrentOffset()
    {
        var f = await Open(pages: q => q.Offset == 0 ? Page(0, 3, "a", "b") : Page(q.Offset, 3, "c"));
        await f.Vm.SearchAsync(CancellationToken.None);
        f.Vm.HasMore.Should().BeTrue();

        await f.Vm.LoadMoreAsync(CancellationToken.None);

        f.Queries.Select(q => q.Offset).Should().Equal(0, 2);
        f.Vm.Collections.Select(c => c.Hit.Slug.Value).Should().Equal("a", "b", "c");
        f.Vm.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task Search_Failure_ShowsTheErrorAndNoCollections()
    {
        var f = await Open();
        f.SearchFailure = new HttpRequestException("offline");

        await f.Vm.SearchAsync(CancellationToken.None);

        f.Vm.Collections.Should().BeEmpty();
        f.Vm.ErrorText.Should().Contain("offline");
    }

    [Fact]
    public async Task Rows_ShowWhatTheCardNeeds()
    {
        var f = await Open(pages: q => new NexusCollectionsSearchPage([Hit("abc123", "Vanilla Plus", adult: true)], 0, 1));

        await f.Vm.SearchAsync(CancellationToken.None);

        var row = f.Vm.Collections.Should().ContainSingle().Subject;
        row.Name.Should().Be("Vanilla Plus");
        row.AuthorLine.Should().Be("by curator");
        row.Summary.Should().Be("Vanilla Plus summary");
        row.ImageUrl.Should().Be(new Uri("https://img.example/abc123.png"));
        row.IsAdult.Should().BeTrue();
        row.Details.Should().Contain("45 mods").And.Contain("118 MB").And.Contain("12 endorsements").And.Contain("3.4K downloads");
        row.PrimaryButtonText.Should().Be("Install");
    }

    [Fact]
    public async Task Install_HandsTheNxmLinkOfTheLatestRevisionToTheNxmHandler()
    {
        var f = await Open();
        await f.Vm.SearchAsync(CancellationToken.None);

        await f.Vm.InstallAsync(f.Vm.Collections[0], CancellationToken.None);

        var call = f.Nxm.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IIpcProtocolHandler.Handle));
        var url = NXMUrl.Parse((string)call.GetArguments()[0]!).Should().BeOfType<NXMCollectionUrl>().Subject;
        url.Game.Should().Be("cyberpunk2077");
        url.Slug.Should().Be(CollectionSlug.From("abc123"));
        url.Revision.Should().Be(RevisionNumber.From(7));
        f.OpenedPages.Should().BeEmpty();
        f.Vm.ErrorText.Should().BeEmpty();
    }

    [Fact]
    public async Task Install_WhenTheHandlerFails_ShowsTheError()
    {
        var f = await Open();
        await f.Vm.SearchAsync(CancellationToken.None);
        f.Nxm.Handle(default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("no luck"));

        await f.Vm.InstallAsync(f.Vm.Collections[0], CancellationToken.None);

        f.Vm.ErrorText.Should().Contain("Some collection").And.Contain("no luck");
    }

    [Fact]
    public async Task Install_OfACollectionWithoutAPublishedRevision_SaysSoInsteadOfDoingNothing()
    {
        var hit = Hit("abc123") with { LatestRevision = null };
        var f = await Open(pages: q => new NexusCollectionsSearchPage([hit], 0, 1));
        await f.Vm.SearchAsync(CancellationToken.None);

        await f.Vm.InstallAsync(f.Vm.Collections[0], CancellationToken.None);

        f.Vm.ErrorText.Should().Contain("no published revision");
        await f.Nxm.DidNotReceiveWithAnyArgs().Handle(default!, default);
    }

    [Fact]
    public async Task ACollectionAlreadyInTheLibrary_ShowsOpen_AndOpensItsCollectionPageInsteadOfInstalling()
    {
        var inLibrary = $"in-library-{Guid.NewGuid():N}";
        var revision = await SeedLibraryRevision(inLibrary, 7);
        var f = await Open(pages: q => Page(q.Offset, 2, inLibrary, "not-in-library"));
        await f.Vm.SearchAsync(CancellationToken.None);

        f.Vm.Collections[0].PrimaryButtonText.Should().Be("Open");
        f.Vm.Collections[1].PrimaryButtonText.Should().Be("Install");

        await f.Vm.InstallAsync(f.Vm.Collections[0], CancellationToken.None);

        await f.Nxm.DidNotReceiveWithAnyArgs().Handle(default!, default);
        var page = f.OpenedPages.Should().ContainSingle().Subject;
        page.FactoryId.Should().Be(CollectionDownloadPageFactory.StaticId);
        page.Context.Should().BeEquivalentTo(new CollectionDownloadPageContext { TargetLoadout = f.LoadoutId, CollectionRevisionMetadataId = revision });
    }

    [Fact]
    public async Task ACollectionThatEntersTheLibraryWhileListed_FlipsToOpen()
    {
        var slug = $"enters-library-{Guid.NewGuid():N}";
        var f = await Open(pages: q => Page(q.Offset, 1, slug));
        using var watching = f.Vm.Start();
        f.Vm.IsVisible = true;
        await f.Vm.SearchAsync(CancellationToken.None);
        f.Vm.Collections[0].PrimaryButtonText.Should().Be("Install");

        await SeedLibraryRevision(slug, 7);

        await Eventually(() => f.Vm.Collections[0].PrimaryButtonText.Should().Be("Open"));
    }

    [Fact]
    public async Task OpenOnNexus_OpensTheCollectionsWebPage()
    {
        var f = await Open();
        await f.Vm.SearchAsync(CancellationToken.None);

        f.Vm.OpenOnNexus(f.Vm.Collections[0]);

        f.OsInterop.Received(1).OpenUri(new Uri("https://www.nexusmods.com/games/cyberpunk2077/collections/abc123"));
    }

    [Fact]
    public async Task NothingIsSearched_UntilTheTabIsShown()
    {
        var f = await Open();
        using var watching = f.Vm.Start();

        await Task.Delay(700);
        f.Queries.Should().BeEmpty();

        f.Vm.IsVisible = true;

        await Eventually(() => f.Queries.Should().ContainSingle());
    }

    [Fact]
    public async Task ReturningToTheTab_DoesNotSearchAgain_ButChangingTheSearchDoes()
    {
        var f = await Open();
        using var watching = f.Vm.Start();
        f.Vm.IsVisible = true;
        await Eventually(() => f.Queries.Should().ContainSingle());

        f.Vm.IsVisible = false;
        f.Vm.IsVisible = true;
        await Task.Delay(700);
        f.Queries.Should().ContainSingle();

        f.Vm.SearchText = "plus";
        await Eventually(() => f.Queries.Should().HaveCount(2));
        f.Queries[1].SearchText.Should().Be("plus");
    }
}
