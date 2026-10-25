using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Library.Jobs;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Pages.NexusCatalog;
using SilverModManager.App.UI.Windows;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

public class NexusModsViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class Fixture
    {
        public NexusModsViewModel Vm = null!;
        public INexusModsBrowser Browser = null!;
        public ILoginManager Login = null!;
        public IOSInterop OsInterop = null!;
        public ILibraryService Library = null!;
        public NexusModsGameId Game;
        public R3.BehaviorSubject<UserInfo?> UserInfo = null!;
        public Exception? SearchFailure;
        public List<NexusModsSearchQuery> Queries { get; } = [];
    }

    private static NexusModsSearchHit Hit(uint modId, string name = "Some mod")
        => new(ModId.From(modId), $"uid-{modId}", name, $"{name} summary", "author", "1.0", 1234, 1_500_000, new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            new Uri($"https://img.example/{modId}-t.png"), new Uri($"https://img.example/{modId}.png"), false);

    private static UserInfo User(UserRole role)
        => new() { UserId = UserId.From(1), Name = "someone", UserRole = role, AvatarUrl = null };

    private static NexusModsSearchPage Page(int offset, int total, params uint[] modIds)
        => new(modIds.Select(id => Hit(id)).ToArray(), offset, total);

    private async Task<Fixture> Open(bool premium, Func<NexusModsSearchQuery, NexusModsSearchPage>? pages = null, bool storedNexusId = true)
    {
        var row = await SeedLoadoutRow(GameStore.Steam, "Nexus Mods loadout");
        var fixture = new Fixture();
        fixture.Game = GameInstallMetadata.GameId.Get(Loadout.Load(Connection.Db, row.LoadoutId).Installation);

        if (!storedNexusId)
        {
            // A row written before the game had a Nexus id.
            using var tx = Connection.BeginTransaction();
            tx.Retract(Loadout.Load(Connection.Db, row.LoadoutId).Installation.Id, GameInstallMetadata.GameId, fixture.Game);
            await tx.Commit();
        }

        fixture.Browser = Substitute.For<INexusModsBrowser>();
        fixture.Browser.Search(default!, default).ReturnsForAnyArgs(call =>
        {
            var query = call.Arg<NexusModsSearchQuery>();
            fixture.Queries.Add(query);
            if (fixture.SearchFailure is not null) return Task.FromException<NexusModsSearchPage>(fixture.SearchFailure);
            return Task.FromResult(pages?.Invoke(query) ?? Page(query.Offset, 1, 7));
        });

        fixture.Login = Substitute.For<ILoginManager>();
        fixture.Login.IsPremium.Returns(premium);
        fixture.Login.IsLoggedIn.Returns(premium);
        fixture.UserInfo = new R3.BehaviorSubject<UserInfo?>(premium ? User(UserRole.Premium) : null);
        fixture.Login.UserInfoObservable.Returns(fixture.UserInfo);

        var domains = Substitute.For<IGameDomainToGameIdMappingCache>();
        domains.GetDomain(default).ReturnsForAnyArgs(GameDomain.From("cyberpunk2077"));

        fixture.OsInterop = Substitute.For<IOSInterop>();
        fixture.Library = Substitute.For<ILibraryService>();
        fixture.Library.AddDownload(default!).ReturnsForAnyArgs(_ =>
        {
            var task = Substitute.For<IJobTask<IAddDownloadJob, LibraryFile.ReadOnly>>();
            task.GetAwaiter().Returns(Task.FromResult(default(LibraryFile.ReadOnly)).GetAwaiter());
            return task;
        });

        fixture.Vm = new NexusModsViewModel(
            Provider.GetRequiredService<IWindowManager>(),
            Connection,
            fixture.Browser,
            fixture.Login,
            fixture.Library,
            Provider.GetRequiredService<TemporaryFileManager>(),
            fixture.OsInterop,
            domains,
            Provider.GetServices<IGameData>(),
            NullLogger<NexusModsViewModel>.Instance)
        {
            LoadoutId = row.LoadoutId,
        };
        return fixture;
    }

    private static NexusModsModRow RowOf(uint modId) => new(Hit(modId));

    [Fact]
    public async Task Search_LoadsTheFirstPageOfTheLoadoutsGameMostEndorsedFirst()
    {
        var f = await Open(premium: false, pages: q => Page(q.Offset, 2, 7, 8));

        await f.Vm.SearchAsync(CancellationToken.None);

        var query = f.Queries.Should().ContainSingle().Subject;
        query.Game.Should().Be(f.Game);
        query.SearchText.Should().BeEmpty();
        query.Sort.Should().Be(NexusModsSort.Endorsements);
        query.Offset.Should().Be(0);
        query.Count.Should().Be(40);
        query.IncludeAdult.Should().BeFalse();
        f.Vm.Mods.Select(m => m.Hit.ModId).Should().Equal(ModId.From(7), ModId.From(8));
        f.Vm.ErrorText.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_RowWithoutAStoredNexusId_UsesTheIdOfTheGameClass()
    {
        var f = await Open(premium: false, storedNexusId: false);

        await f.Vm.SearchAsync(CancellationToken.None);

        f.Vm.ErrorText.Should().BeEmpty();
        f.Queries.Should().ContainSingle().Which.Game.Should().Be(f.Game);
        f.Vm.Mods.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Rows_CarryReadableText()
    {
        var f = await Open(premium: false);
        await f.Vm.SearchAsync(CancellationToken.None);

        var row = f.Vm.Mods.Single();
        row.Name.Should().Be("Some mod");
        row.Summary.Should().Be("Some mod summary");
        row.AuthorLine.Should().Be("by author");
        row.Details.Should().Be("1.0  ·  1.2K endorsements  ·  1.5M downloads");
        row.ThumbnailUrl.Should().Be(new Uri("https://img.example/7-t.png"));
    }

    [Fact]
    public async Task Search_WithATextAndASort_SendsBothAndStartsAtTheTop()
    {
        var f = await Open(premium: false);
        f.Vm.SearchText = "  weapons ";
        f.Vm.Sort = NexusModsSort.Updated;

        await f.Vm.SearchAsync(CancellationToken.None);

        f.Queries.Last().SearchText.Should().Be("weapons");
        f.Queries.Last().Sort.Should().Be(NexusModsSort.Updated);
        f.Queries.Last().Offset.Should().Be(0);
    }

    [Fact]
    public async Task Search_ReplacesTheRowsOfAnEarlierSearch()
    {
        var f = await Open(premium: false, pages: q => q.SearchText == "b" ? Page(0, 1, 2) : Page(0, 1, 1));
        await f.Vm.SearchAsync(CancellationToken.None);
        f.Vm.SearchText = "b";

        await f.Vm.SearchAsync(CancellationToken.None);

        f.Vm.Mods.Select(m => m.Hit.ModId).Should().Equal(ModId.From(2));
    }

    [Fact]
    public async Task LoadMore_AppendsTheNextPage()
    {
        var f = await Open(premium: false, pages: q => q.Offset == 0 ? Page(0, 3, 1, 2) : Page(q.Offset, 3, 3));
        await f.Vm.SearchAsync(CancellationToken.None);
        f.Vm.HasMore.Should().BeTrue();

        await f.Vm.LoadMoreAsync(CancellationToken.None);

        f.Queries.Last().Offset.Should().Be(2, "the next page starts after the rows already shown");
        f.Vm.Mods.Select(m => m.Hit.ModId).Should().Equal(ModId.From(1), ModId.From(2), ModId.From(3));
        f.Vm.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task LoadMore_WhenTheApiFails_KeepsTheRowsAndCanBeRetried()
    {
        var f = await Open(premium: false, pages: q => q.Offset == 0 ? Page(0, 3, 1, 2) : Page(q.Offset, 3, 3));
        await f.Vm.SearchAsync(CancellationToken.None);
        f.SearchFailure = new InvalidOperationException("timed out");

        await f.Vm.LoadMoreAsync(CancellationToken.None);

        f.Vm.ErrorText.Should().Contain("timed out");
        f.Vm.Mods.Should().HaveCount(2);
        f.Vm.HasMore.Should().BeTrue("the page that failed is still there to ask for");

        f.SearchFailure = null;
        await f.Vm.LoadMoreAsync(CancellationToken.None);

        f.Vm.Mods.Select(m => m.Hit.ModId).Should().Equal(ModId.From(1), ModId.From(2), ModId.From(3));
        f.Vm.ErrorText.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadMore_WhenEverythingIsShown_AsksForNothing()
    {
        var f = await Open(premium: false, pages: q => Page(0, 1, 1));
        await f.Vm.SearchAsync(CancellationToken.None);

        await f.Vm.LoadMoreAsync(CancellationToken.None);

        f.Queries.Should().HaveCount(1);
    }

    [Fact]
    public async Task Search_WhenTheApiFails_ShowsTheErrorAndKeepsNoRows()
    {
        var f = await Open(premium: false);
        f.SearchFailure = new InvalidOperationException("Nexus Mods is down");

        await f.Vm.SearchAsync(CancellationToken.None);

        f.Vm.ErrorText.Should().Contain("Nexus Mods is down");
        f.Vm.Mods.Should().BeEmpty();
        f.Vm.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task Search_AfterAFailure_ClearsTheError()
    {
        var f = await Open(premium: false);
        f.SearchFailure = new InvalidOperationException("down");
        await f.Vm.SearchAsync(CancellationToken.None);
        f.SearchFailure = null;

        await f.Vm.SearchAsync(CancellationToken.None);

        f.Vm.ErrorText.Should().BeEmpty();
        f.Vm.Mods.Should().HaveCount(1);
    }

    [Fact]
    public async Task InstallButtonText_SaysInstallForPremiumAndDownloadOnNexusForOthers()
    {
        var premium = await Open(premium: true);
        var free = await Open(premium: false);
        using var activePremium = await OnUi(() => Task.FromResult(premium.Vm.Activator.Activate()));
        using var activeFree = await OnUi(() => Task.FromResult(free.Vm.Activator.Activate()));

        await EventuallyOnUi(() =>
        {
            premium.Vm.InstallButtonText.Should().Be("Install");
            free.Vm.InstallButtonText.Should().Be("Download on Nexus Mods");
        });
    }

    [Fact]
    public async Task InstallButtonText_FollowsALoginOrLogoutWhileThePageIsOpen()
    {
        var f = await Open(premium: false);
        using var active = await OnUi(() => Task.FromResult(f.Vm.Activator.Activate()));

        f.UserInfo.OnNext(User(UserRole.Premium));
        await EventuallyOnUi(() => f.Vm.InstallButtonText.Should().Be("Install"));

        f.UserInfo.OnNext(null);
        await EventuallyOnUi(() => f.Vm.InstallButtonText.Should().Be("Download on Nexus Mods"));
    }

    [Fact]
    public async Task Install_ForAFreeUser_OpensTheFilesTabAndDownloadsNothing()
    {
        var f = await Open(premium: false);

        await f.Vm.InstallAsync(RowOf(7), CancellationToken.None);

        f.OsInterop.Received(1).OpenUri(new Uri("https://www.nexusmods.com/cyberpunk2077/mods/7?tab=files"));
        await f.Browser.DidNotReceiveWithAnyArgs().FindMainFile(default, default, default);
        await f.Browser.DidNotReceiveWithAnyArgs().CreateDownloadJob(default, default, default, default, default);
        _ = f.Library.DidNotReceiveWithAnyArgs().AddDownload(default!);
    }

    [Fact]
    public async Task Install_ForPremium_DownloadsTheMainFileIntoTheLibrary()
    {
        var f = await Open(premium: true);
        var job = Substitute.For<IJobTask<NexusModsDownloadJob, AbsolutePath>>();
        f.Browser.FindMainFile(f.Game, ModId.From(7), Arg.Any<CancellationToken>()).Returns(FileId.From(55));
        f.Browser.CreateDownloadJob(f.Game, ModId.From(7), FileId.From(55), Arg.Any<AbsolutePath>(), Arg.Any<CancellationToken>()).Returns(job);

        await f.Vm.InstallAsync(RowOf(7), CancellationToken.None);

        _ = f.Library.Received(1).AddDownload(job);
        f.OsInterop.DidNotReceiveWithAnyArgs().OpenUri(default!);
        f.Vm.ErrorText.Should().BeEmpty();
    }

    [Fact]
    public async Task Install_ForPremium_WithoutASingleMainFile_OpensTheFilesTab()
    {
        var f = await Open(premium: true);
        f.Browser.FindMainFile(default, default, default).ReturnsForAnyArgs(Task.FromResult<FileId?>(null));

        await f.Vm.InstallAsync(RowOf(7), CancellationToken.None);

        f.OsInterop.Received(1).OpenUri(new Uri("https://www.nexusmods.com/cyberpunk2077/mods/7?tab=files"));
        await f.Browser.DidNotReceiveWithAnyArgs().CreateDownloadJob(default, default, default, default, default);
    }

    [Fact]
    public async Task Install_WhenTheDownloadCannotStart_ShowsTheError()
    {
        var f = await Open(premium: true);
        f.Browser.FindMainFile(default, default, default).ReturnsForAnyArgs(Task.FromResult<FileId?>(FileId.From(55)));
        f.Browser.CreateDownloadJob(default, default, default, default, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("no download links"));

        await f.Vm.InstallAsync(RowOf(7), CancellationToken.None);

        f.Vm.ErrorText.Should().Contain("Some mod").And.Contain("no download links");
    }

    [Fact]
    public async Task Open_AlwaysOpensTheModPage()
    {
        var premium = await Open(premium: true);
        var free = await Open(premium: false);

        premium.Vm.Open(RowOf(7));
        free.Vm.Open(RowOf(7));

        premium.OsInterop.Received(1).OpenUri(new Uri("https://www.nexusmods.com/cyberpunk2077/mods/7"));
        free.OsInterop.Received(1).OpenUri(new Uri("https://www.nexusmods.com/cyberpunk2077/mods/7"));
    }
}
