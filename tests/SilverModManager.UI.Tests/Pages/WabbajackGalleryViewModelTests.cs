using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.App.UI.Pages.WabbajackGallery;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Gallery;

namespace SilverModManager.UI.Tests.Pages;

public class WabbajackGalleryViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    // The seeded loadout is for Cyberpunk 2077, which the gallery knows as cyberpunk2077.
    private const string Game = "cyberpunk2077";

    internal static GalleryEntry Entry(string title, string author = "Someone", string game = Game, string id = "", Action<GalleryEntryBuilder>? configure = null)
    {
        var builder = new GalleryEntryBuilder { Title = title, Author = author, Game = game, Id = id == "" ? title.ToLowerInvariant() : id };
        configure?.Invoke(builder);
        return builder.Build();
    }

    internal sealed class GalleryEntryBuilder
    {
        public string Title = "", Author = "", Game = "", Id = "";
        public string[] Tags = [];
        public bool Official, Nsfw, ForcedDown;
        public ulong? Archives, Installed;
        public DateTimeOffset? Created, Updated;
        public bool Downloadable = true;

        public GalleryEntry Build() => new()
        {
            Title = Title,
            Author = Author,
            GalleryGame = Game,
            MachineUrl = Id,
            Repository = "wabbajack-tools/mod-lists",
            Tags = Tags,
            IsOfficial = Official,
            IsNsfw = Nsfw,
            IsForcedDown = ForcedDown,
            ArchivesSize = Archives,
            InstalledSize = Installed,
            DateCreated = Created,
            DateUpdated = Updated,
            Version = "1.0",
            Download = Downloadable ? new Uri($"https://authored-files.wabbajack.org/{Id}.wabbajack") : null,
            DownloadHash = Downloadable ? WabbajackHash.Parse("AQIDBAUGBwg=") : null,
            DownloadSize = Downloadable ? 10UL : null,
        };
    }

    internal sealed class FakeGallery(params GalleryEntry[] entries) : IWabbajackGalleryService
    {
        public IReadOnlyList<GalleryEntry> Entries { get; set; } = entries;
        public DateTimeOffset FetchedAt { get; set; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public bool IsStale { get; set; }
        public Exception? ListingFailure { get; set; }
        public Exception? DownloadFailure { get; set; }
        public Dictionary<string, LibraryFileId> InLibrary { get; } = new();
        public List<bool> Requests { get; } = [];
        public List<GalleryEntry> Downloads { get; } = [];
        public LibraryFileId DownloadedAs { get; set; } = LibraryFileId.From(EntityId.From(0xD0D0));

        public Task<GalleryListing> GetListing(bool refresh, CancellationToken cancellationToken)
        {
            Requests.Add(refresh);
            return ListingFailure is null ? Task.FromResult(new GalleryListing(Entries, FetchedAt, IsStale)) : Task.FromException<GalleryListing>(ListingFailure);
        }

        public IReadOnlyDictionary<string, LibraryFileId> ListsInLibrary() => InLibrary;

        public Task<LibraryFileId> Download(GalleryEntry entry, CancellationToken cancellationToken)
        {
            Downloads.Add(entry);
            return DownloadFailure is null ? Task.FromResult(DownloadedAs) : Task.FromException<LibraryFileId>(DownloadFailure);
        }
    }

    private async Task<(WabbajackGalleryViewModel Vm, List<PageData> Pages)> Open(
        FakeGallery gallery, FakeAdultContentSetting? adult = null, IOSInterop? os = null, InMemoryStoreSortMemory? sortMemory = null)
    {
        var pages = new List<PageData>();
        var vm = new WabbajackGalleryViewModel(
            Provider.GetRequiredService<IWindowManager>(),
            Connection,
            gallery,
            os ?? Substitute.For<IOSInterop>(),
            Substitute.For<SilverModManager.App.UI.Controls.Cards.IThumbnailLoader>(),
            sortMemory ?? new InMemoryStoreSortMemory(),
            adult ?? new FakeAdultContentSetting(),
            NullLogger<WabbajackGalleryViewModel>.Instance)
        {
            LoadoutId = (await SeedLoadoutRow(GameStore.Steam, "gallery")).LoadoutId,
            OpenPage = pages.Add,
        };
        await vm.LoadAsync(CancellationToken.None);
        return (vm, pages);
    }

    private static string[] Titles(WabbajackGalleryViewModel vm) => vm.Lists.Select(r => r.Title).ToArray();

    [Fact]
    public async Task Open_ListsOnlyTheListsOfTheLoadoutsGameAndHidesForcedDownAndAdultOnes()
    {
        var gallery = new FakeGallery(
            Entry("Shown"),
            Entry("Other game", game: "skyrimspecialedition"),
            Entry("Spicy", configure: b => b.Nsfw = true),
            Entry("Pulled", configure: b => b.ForcedDown = true));

        var (vm, _) = await Open(gallery);

        Titles(vm).Should().Equal("Shown");
    }

    [Fact]
    public async Task FlippingShowAdultContent_ListsTheAdultListsAtOnce()
    {
        var adult = new FakeAdultContentSetting();
        var gallery = new FakeGallery(Entry("Shown"), Entry("Spicy", configure: b => b.Nsfw = true), Entry("Pulled", configure: b => b.ForcedDown = true));
        var (vm, _) = await Open(gallery, adult);
        using var active = await OnUi(() => Task.FromResult(vm.Activator.Activate()));
        Titles(vm).Should().Equal("Shown");

        adult.ShowAdultContent = true;

        await EventuallyOnUi(() => Titles(vm).Should().BeEquivalentTo("Shown", "Spicy"));
    }

    [Fact]
    public async Task ADefaultSort_PutsTheMostRecentlyUpdatedFirstAndFallsBackToTheDateCreated()
    {
        var gallery = new FakeGallery(
            Entry("Old", configure: b => b.Updated = new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            Entry("Fresh", configure: b => b.Updated = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            Entry("Never updated", configure: b => b.Created = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            Entry("No dates"));

        var (vm, _) = await Open(gallery);

        vm.Sort.Should().Be(WabbajackGallerySort.Updated);
        Titles(vm).Should().Equal("Fresh", "Never updated", "Old", "No dates");
    }

    [Fact]
    public async Task EachSort_OrdersTheListsItNames()
    {
        var gallery = new FakeGallery(
            Entry("Beta", configure: b => { b.Created = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero); b.Archives = 50; }),
            Entry("alpha", configure: b => { b.Created = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero); b.Archives = 500; }),
            Entry("Gamma", configure: b => { b.Created = new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.Zero); }));
        var (vm, _) = await Open(gallery);

        vm.Sort = WabbajackGallerySort.Newest;
        Titles(vm).Should().Equal("alpha", "Gamma", "Beta");

        vm.Sort = WabbajackGallerySort.Name;
        Titles(vm).Should().Equal("alpha", "Beta", "Gamma");

        // A list that gives no size has nothing to rank by, so it goes last whichever way the sort runs.
        vm.Sort = WabbajackGallerySort.LargestDownload;
        Titles(vm).Should().Equal("alpha", "Beta", "Gamma");

        vm.Sort = WabbajackGallerySort.SmallestDownload;
        Titles(vm).Should().Equal("Beta", "alpha", "Gamma");
    }

    [Fact]
    public async Task Search_MatchesTitleAuthorAndTagsIgnoringCase()
    {
        var gallery = new FakeGallery(
            Entry("Nightfall", "Mara", configure: b => b.Tags = ["Quests", "Survival"]),
            Entry("Daybreak", "Jonas", configure: b => b.Tags = ["Visuals"]),
            Entry("Plain", "Nobody"));
        var (vm, _) = await Open(gallery);

        vm.SearchText = "night";
        Titles(vm).Should().Equal("Nightfall");

        vm.SearchText = "JONAS";
        Titles(vm).Should().Equal("Daybreak");

        vm.SearchText = "surviv";
        Titles(vm).Should().Equal("Nightfall");

        vm.SearchText = "visuals jonas";
        Titles(vm).Should().Equal("Daybreak");

        vm.SearchText = "";
        Titles(vm).Should().HaveCount(3);
    }

    [Fact]
    public async Task ARow_CarriesTheTextsOfItsCard()
    {
        var gallery = new FakeGallery(Entry("Nightfall", "Mara", configure: b =>
        {
            b.Official = true;
            b.Tags = ["Quests", "Survival", "Gameplay", "Combat", "Magic"];
            b.Archives = (ulong)(85 * 1024L * 1024 * 1024);
            b.Installed = (ulong)(210 * 1024L * 1024 * 1024);
        }));

        var (vm, _) = await Open(gallery);

        var row = vm.Lists.Single();
        row.AuthorLine.Should().Be("by Mara");
        row.IsOfficial.Should().BeTrue();
        row.SizesLine.Should().Be("download 85 GB · installed 210 GB");
        row.ShownTags.Should().Equal("Quests", "Survival", "Gameplay");
    }

    [Fact]
    public async Task ASizelessRow_SaysNothingOfSizes()
    {
        var (vm, _) = await Open(new FakeGallery(Entry("Nightfall")));

        vm.Lists.Single().SizesLine.Should().BeEmpty();
    }

    [Fact]
    public async Task AListThatIsInTheLibrary_ReadsOpenInsteadOfInstall()
    {
        var gallery = new FakeGallery(Entry("Have"), Entry("Want"));
        gallery.InLibrary["https://authored-files.wabbajack.org/have.wabbajack"] = LibraryFileId.From(EntityId.From(0xABC));

        var (vm, _) = await Open(gallery);

        vm.Lists.Single(r => r.Title == "Have").InstallButtonText.Should().Be("Open");
        vm.Lists.Single(r => r.Title == "Want").InstallButtonText.Should().Be("Install");
    }

    [Fact]
    public async Task AListWithNothingToVerifyADownloadWith_OffersNoInstall()
    {
        var (vm, _) = await Open(new FakeGallery(Entry("Unverifiable", configure: b => b.Downloadable = false)));

        vm.Lists.Single().CanInstall.Should().BeFalse();
    }

    [Fact]
    public async Task Install_DownloadsTheListThenOpensItsModlistPage()
    {
        var gallery = new FakeGallery(Entry("Want"));
        var (vm, pages) = await Open(gallery);

        await vm.InstallCommand.Execute(vm.Lists.Single());

        gallery.Downloads.Should().ContainSingle().Which.Title.Should().Be("Want");
        var page = pages.Should().ContainSingle().Subject;
        page.FactoryId.Should().Be(WabbajackModlistPageFactory.StaticId);
        page.Context.Should().Be(new WabbajackModlistPageContext(gallery.DownloadedAs));
        vm.ErrorText.Should().BeEmpty();
    }

    [Fact]
    public async Task Install_OfAListAlreadyInTheLibrary_OpensItWithoutDownloading()
    {
        var gallery = new FakeGallery(Entry("Have"));
        var existing = LibraryFileId.From(EntityId.From(0xABC));
        gallery.InLibrary["https://authored-files.wabbajack.org/have.wabbajack"] = existing;
        var (vm, pages) = await Open(gallery);

        await vm.InstallCommand.Execute(vm.Lists.Single());

        gallery.Downloads.Should().BeEmpty();
        pages.Should().ContainSingle().Which.Context.Should().Be(new WabbajackModlistPageContext(existing));
    }

    [Fact]
    public async Task Install_ThatFails_ReportsItAndOpensNothing()
    {
        var gallery = new FakeGallery(Entry("Want")) { DownloadFailure = new GalleryDownloadException("the hash did not match") };
        var (vm, pages) = await Open(gallery);

        await vm.InstallCommand.Execute(vm.Lists.Single());

        pages.Should().BeEmpty();
        vm.ErrorText.Should().Contain("Want").And.Contain("the hash did not match");
    }

    [Fact]
    public async Task Install_WhoseDownloadIsCancelledElsewhere_SaysSoAndOpensNothing()
    {
        var gallery = new FakeGallery(Entry("Want")) { DownloadFailure = new OperationCanceledException() };
        var (vm, pages) = await Open(gallery);

        await vm.InstallCommand.Execute(vm.Lists.Single());

        pages.Should().BeEmpty();
        vm.ErrorText.Should().Contain("Want").And.Contain("cancelled");
    }

    [Fact]
    public async Task Refresh_ThatTimesOut_ShowsWhyAndKeepsWhatWasListed()
    {
        var gallery = new FakeGallery(Entry("A"));
        var (vm, _) = await Open(gallery);

        gallery.ListingFailure = new TaskCanceledException("timed out");
        await vm.RefreshCommand.Execute();

        Titles(vm).Should().Equal("A");
        vm.ErrorText.Should().Contain("timed out");
    }

    [Fact]
    public async Task Open_ShowsWhenTheGalleryWasFetchedAndFlagsAnOfflineCopy()
    {
        var gallery = new FakeGallery(Entry("A"));
        var (vm, _) = await Open(gallery);
        vm.RefreshedText.Should().StartWith("Last refreshed").And.NotContain("offline");

        gallery.IsStale = true;
        await vm.RefreshCommand.Execute();
        vm.RefreshedText.Should().Contain("offline");
    }

    [Fact]
    public async Task Refresh_AsksTheGalleryToBypassItsCache()
    {
        var gallery = new FakeGallery(Entry("A"));
        var (vm, _) = await Open(gallery);
        gallery.Requests.Should().Equal(false);

        gallery.Entries = [Entry("A"), Entry("B")];
        await vm.RefreshCommand.Execute();

        gallery.Requests.Should().Equal(false, true);
        Titles(vm).Should().BeEquivalentTo("A", "B");
    }

    [Fact]
    public async Task AGalleryThatCannotBeReached_ShowsWhyAndKeepsWhatWasListed()
    {
        var gallery = new FakeGallery(Entry("A"));
        var (vm, _) = await Open(gallery);

        gallery.ListingFailure = new GalleryUnavailableException("No gallery repository could be read");
        await vm.RefreshCommand.Execute();

        vm.ErrorText.Should().Contain("No gallery repository could be read");
        Titles(vm).Should().Equal("A");
    }

    [Fact]
    public async Task TheSortChoice_IsRememberedPerGame()
    {
        var memory = new InMemoryStoreSortMemory();
        var (vm, _) = await Open(new FakeGallery(Entry("A")), sortMemory: memory);
        vm.Sort = WabbajackGallerySort.Name;

        var (reopened, _) = await Open(new FakeGallery(Entry("A")), sortMemory: memory);

        reopened.Sort.Should().Be(WabbajackGallerySort.Name);
    }

    [Fact]
    public async Task OpenLink_HandsTheAddressToTheSystem()
    {
        var os = Substitute.For<IOSInterop>();
        var (vm, _) = await Open(new FakeGallery(Entry("A")), os: os);

        await vm.OpenLinkCommand.Execute(new Uri("https://example.invalid/readme"));

        os.Received(1).OpenUri(new Uri("https://example.invalid/readme"));
    }
}
