using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DynamicData;
using SilverModManager.App.UI.Controls;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.App.UI.Pages.Collections.Update;
using SilverModManager.App.UI.Pages.LibraryPage.Collections;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.App.UI.Pages.WabbajackGallery;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using ReactiveUI;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Tests.Pages;
using SilverModManager.UI.Tests.Pages.Collections;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Gallery;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Models;
using SilverModManager.Wabbajack.Update;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests;

/// <summary>
/// Updating an installed Wabbajack list from the UI: the offer, the review and apply behind the shared flow, the
/// superseded old version, the menu items that come and go with the restore point, and the gallery's Update button.
/// </summary>
public class WabbajackUpdateUiTests(IServiceProvider provider) : AUiTest(provider)
{
    private const string Repository = "wabbajack-tools/mod-lists";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition never became true");
            await Task.Delay(20);
        }
    }

    private Task<WabbajackModlistLibraryFile.ReadOnly> AddList(
        string version, string? machineUrl = null, DateTimeOffset? dateUpdated = null, string? name = null, string gameType = "SkyrimSpecialEdition",
        Hash? xxHash64 = null, string? galleryUrl = null, string author = "Someone")
        => WabbajackGroupSeed.AddList(Connection, version, machineUrl, Repository, dateUpdated, name, gameType, xxHash64, galleryUrl, author);

    private async Task<WabbajackModlistLoadoutGroup.ReadOnly> Install(LoadoutId loadout, WabbajackModlistLibraryFile.ReadOnly file, bool finished = true)
    {
        var group = await WabbajackGroupSeed.AddGroup(Connection, file.Id, loadout, file.Name);
        if (finished) await WabbajackGroupSeed.Finish(Connection, group);
        return WabbajackModlistLoadoutGroup.Load(Connection.Db, group);
    }

    private Task Switch(WabbajackModlistLoadoutGroup.ReadOnly group, WabbajackModlistLibraryFile.ReadOnly from, WabbajackModlistLibraryFile.ReadOnly to, bool finished = false)
        => WabbajackGroupSeed.Switch(Connection, group.Id, from.Id, to.Id, finished);

    private async Task RecordRestorePoint(EntityId group)
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(group, WabbajackModlistLoadoutGroup.LastUpdateRestorePoint, EntityId.From(Connection.Db.BasisTxId.Value));
        await tx.Commit();
    }

    private static GalleryEntry Entry(string version, string machineUrl, string title = "Update list", string author = "Someone", string repository = Repository,
        DateTimeOffset? dateUpdated = null, string? download = null, string hash = "AQIDBAUGBwg=") => new()
    {
        Title = title, Author = author, GalleryGame = "skyrimspecialedition", MachineUrl = machineUrl, Repository = repository, Version = version,
        DateUpdated = dateUpdated,
        Download = new Uri(download ?? $"https://authored-files.wabbajack.org/{machineUrl}.{version}.wabbajack"),
        DownloadHash = WabbajackHash.Parse(hash),
        DownloadSize = 10UL,
    };

    private static GalleryListing Listing(params GalleryEntry[] entries) => new(entries, DateTimeOffset.UnixEpoch, IsStale: false);

    private sealed class FakeClient(GalleryListing? cached) : IWabbajackGalleryClient
    {
        public Subject<GalleryListing> Refresh { get; } = new();
        public GalleryListing? Cached { get; set; } = cached;

        public Task<GalleryListing> Get(bool refresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult(Cached ?? throw new InvalidOperationException("There is no cached listing"));

        public GalleryListing? GetCached() => Cached;
        public IObservable<GalleryListing> Refreshed => Refresh;
    }

    /// <summary>Stands in for the updater's work on the library and the install job, and records what it was asked.</summary>
    private sealed class FakeUpdater(IServiceProvider services) : ModlistUpdater(services)
    {
        public ModlistUpdatePreviewResult Preview { get; set; } = new([], Size.Zero, Size.Zero, [], [], [], [], [], [], [], [], [], [], []);
        public List<EntityId> Reviewed { get; } = [];
        public List<(EntityId File, bool SkipMissing, IReadOnlySet<RelativePath> Keep)> Applied { get; } = [];
        public Exception? ApplyThrows { get; set; }
        public TxId? RestorePoint { get; set; }
        public bool Downloaded { get; set; }

        public override Task<ModlistUpdatePreviewResult> Review(WabbajackModlistLoadoutGroup.ReadOnly group, WabbajackModlistLibraryFile.ReadOnly newFile, CancellationToken ct)
        {
            Reviewed.Add(newFile.Id);
            return Task.FromResult(Preview);
        }

        public override Task<bool> IsDownloaded(WabbajackModlistLibraryFile.ReadOnly newFile, LoadoutId loadout, CancellationToken ct) => Task.FromResult(Downloaded);

        public override Task Download(WabbajackModlistLibraryFile.ReadOnly newFile, LoadoutId loadout, CancellationToken ct) => Task.CompletedTask;

        public override Task<WabbajackModlistLoadoutGroup.ReadOnly> Apply(
            WabbajackModlistLoadoutGroup.ReadOnly group, WabbajackModlistLibraryFile.ReadOnly newFile, bool skipMissing, IReadOnlySet<RelativePath> gameFilesToKeep, CancellationToken ct)
        {
            Applied.Add((newFile.Id, skipMissing, gameFilesToKeep));
            return ApplyThrows is null ? Task.FromResult(group) : Task.FromException<WabbajackModlistLoadoutGroup.ReadOnly>(ApplyThrows);
        }

        public override TxId? RestorePointOf(WabbajackModlistLoadoutGroup.ReadOnly group) => RestorePoint;
    }

    private sealed class Lines : IProgress<string>
    {
        public List<string> Reported { get; } = [];
        public void Report(string value) => Reported.Add(value);
    }

    private IServiceProvider With(params (Type Service, object Instance)[] overrides)
        => overrides.Aggregate(Provider, (inner, next) => new OverridingServices(inner, next.Service, next.Instance));

    private IObservable<ICollectionUpdate?> Offer(FakeClient client, WabbajackModlistLoadoutGroup.ReadOnly group)
        => new WabbajackModlistProvider(With((typeof(IWabbajackGalleryClient), client))).ObserveUpdate(CollectionGroup.Load(Connection.Db, group.Id));

    // ---- the offer ----

    [Fact]
    public async Task AnInstalledListWithANewerCachedEntryOffersAnUpdate()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Offer {Guid.NewGuid()}");
        var file = await AddList("1.0", machineUrl: "offer-list");
        var group = await Install(loadout.LoadoutId, file);

        var update = await Offer(new FakeClient(Listing(Entry("2.0", "offer-list"))), group).FirstAsync();

        update.Should().NotBeNull();
        update!.Name.Should().Be(file.Name);
        update.CurrentVersionText.Should().Be("Version 1.0");
        update.NewVersionText.Should().Be("Version 2.0");
        update.Note.Should().BeNull();
    }

    [Fact]
    public async Task TheOfferFollowsTheGalleryRefreshAndGoesWhenTheGroupMovesToTheNewFile()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Follow {Guid.NewGuid()}");
        var one = await AddList("1.0", machineUrl: "follow-list");
        var two = await AddList("2.0", machineUrl: "follow-list");
        var group = await Install(loadout.LoadoutId, one);
        var client = new FakeClient(null);
        var offers = new List<ICollectionUpdate?>();
        using var subscription = Offer(client, group).Subscribe(offers.Add);

        offers.Should().ContainSingle().Which.Should().BeNull("nothing is cached yet");

        client.Refresh.OnNext(Listing(Entry("2.0", "follow-list")));
        await WaitFor(() => offers.LastOrDefault() is not null);

        await Switch(group, one, two);
        await WaitFor(() => offers.Last() is null);
    }

    [Fact]
    public async Task AListWithoutIdentityOffersNoUpdate()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"No identity {Guid.NewGuid()}");
        var file = await AddList("1.0");
        var group = await Install(loadout.LoadoutId, file);

        var update = await Offer(new FakeClient(Listing(Entry("2.0", "any-list"))), group).FirstAsync();

        update.Should().BeNull();
    }

    [Fact]
    public async Task AReuploadUnderTheSameDateCarriesTheAuthorNote()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Reupload {Guid.NewGuid()}");
        var date = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var file = await AddList("1.0", machineUrl: "reupload-list", dateUpdated: date);
        var group = await Install(loadout.LoadoutId, file);

        var update = await Offer(new FakeClient(Listing(Entry("1.1", "reupload-list", dateUpdated: date))), group).FirstAsync();

        update!.Note.Should().Be("re-uploaded by the author");
    }

    [Fact]
    public async Task PickingTheGalleryEntryOfAListMakesItsUpdateAppearWithoutAGalleryRefresh()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Picked offer {Guid.NewGuid()}");
        var file = await AddList("1.0");
        var group = await Install(loadout.LoadoutId, file);
        var client = new FakeClient(Listing(Entry("2.0", "picked-offer-list")));
        var offers = new List<ICollectionUpdate?>();
        using var subscription = Offer(client, group).Subscribe(offers.Add);
        offers.Last().Should().BeNull("the list has no gallery identity yet");

        await Provider.GetRequiredService<ModlistIdentityBackfill>().Assign(WabbajackModlistLibraryFile.Load(Connection.Db, file.Id), Entry("1.0", "picked-offer-list"));

        await WaitFor(() => offers.Last() is not null);
        offers.Last()!.NewVersionText.Should().Be("Version 2.0");
    }

    [Fact]
    public async Task AGroupSwitchedToAFileWhoseInstallNeverFinishedIsOfferedNoUpdate()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Mid update {Guid.NewGuid()}");
        var one = await AddList("1.0", machineUrl: "mid-update-list");
        var two = await AddList("2.0", machineUrl: "mid-update-list");
        var group = await Install(loadout.LoadoutId, one);
        await Switch(group, one, two);
        var client = new FakeClient(Listing(Entry("3.0", "mid-update-list")));

        var update = await Offer(client, group).FirstAsync();

        update.Should().BeNull("Apply refuses an update on top of one that is not finished, and it does so only after the download");
    }

    [Fact]
    public async Task OnceTheSwitchedGroupFinishesItsInstallTheNewerReleaseIsOfferedAgain()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Mid update done {Guid.NewGuid()}");
        var one = await AddList("1.0", machineUrl: "mid-done-list");
        var two = await AddList("2.0", machineUrl: "mid-done-list");
        var group = await Install(loadout.LoadoutId, one);
        await Switch(group, one, two);
        var offers = new List<ICollectionUpdate?>();
        using var subscription = Offer(new FakeClient(Listing(Entry("3.0", "mid-done-list"))), group).Subscribe(offers.Add);
        offers.Last().Should().BeNull();

        using (var tx = Connection.BeginTransaction())
        {
            tx.Retract(group.Id, WabbajackModlistLoadoutGroup.WasDisabledByUser, false);
            tx.Add(group.Id, WabbajackModlistLoadoutGroup.InstalledAt, DateTimeOffset.UtcNow);
            await tx.Commit();
        }

        await WaitFor(() => offers.Last() is not null);
    }

    // ---- the adapter ----

    private async Task<(WabbajackModlistLoadoutGroup.ReadOnly Group, WabbajackModlistLibraryFile.ReadOnly New, GalleryEntry Entry)> InstalledAndNewer()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Adapter {Guid.NewGuid()}");
        var machineUrl = $"adapter-{Guid.NewGuid():N}";
        var one = await AddList("1.0", machineUrl);
        var two = await AddList("2.0", machineUrl);
        var group = await Install(loadout.LoadoutId, one);
        return (group, two, Entry("2.0", machineUrl));
    }

    [Fact]
    public async Task ReviewDownloadsTheListFileWhenItIsNotInTheLibraryYet()
    {
        var (group, two, entry) = await InstalledAndNewer();
        var gallery = new WabbajackGalleryViewModelTests.FakeGallery { DownloadedAs = LibraryFileId.From(two.Id) };
        var updater = new FakeUpdater(Provider);
        var update = new WabbajackCollectionUpdate(With((typeof(IWabbajackGalleryService), gallery), (typeof(ModlistUpdater), updater)), group, entry);
        var progress = new Lines();

        await update.Review(progress, CancellationToken.None);

        gallery.Downloads.Should().ContainSingle().Which.Should().Be(entry);
        progress.Reported.Should().Equal("Downloading the list…");
        updater.Reviewed.Should().Equal(two.Id);
    }

    [Fact]
    public async Task ReviewUsesTheLibrarysCopyOfTheNewListWithoutDownloading()
    {
        var (group, two, entry) = await InstalledAndNewer();
        var gallery = new WabbajackGalleryViewModelTests.FakeGallery();
        gallery.ByHash[entry.MachineUrl] = LibraryFileId.From(two.Id);
        var updater = new FakeUpdater(Provider);
        var update = new WabbajackCollectionUpdate(With((typeof(IWabbajackGalleryService), gallery), (typeof(ModlistUpdater), updater)), group, entry);

        await update.Review(new Lines(), CancellationToken.None);

        gallery.Downloads.Should().BeEmpty();
        updater.Reviewed.Should().Equal(two.Id);
    }

    [Fact]
    public async Task TheReviewIsThePreviewInTheNeutralForm()
    {
        var (group, two, entry) = await InstalledAndNewer();
        var gallery = new WabbajackGalleryViewModelTests.FakeGallery();
        gallery.ByHash[entry.MachineUrl] = LibraryFileId.From(two.Id);
        static ArchivePlanRow Archive(ArchiveState state, string name)
            => new(new ArchiveEntry(FileIdentity.XxHash64(Hash.From((ulong)name.Length)), 4_000, name, "", new ManualState(new Uri("https://example.test/a"), "get it")), state, null, null, null);
        var updater = new FakeUpdater(Provider)
        {
            Preview = new ModlistUpdatePreviewResult(
                [Archive(ArchiveState.Manual, "by-hand.7z"), Archive(ArchiveState.Automatic, "fetched.7z")], Size.From(8_000), Size.From(20_000),
                Added: ["New mod"], Changed: ["Edited mod"], Renamed: [("Old", "New")], Removed: ["Gone mod"], RetriedTextures: ["textures/a.dds"],
                GameFilesKept: [RelativePath.FromUnsanitizedInput("Data/Keep.esp")], UserTweaksKept: [], Warnings: ["careful"],
                ModOrder: ["Moved mod"], Plugins: ["Plugin.esp"], Conflicts: ["Contested mod"]),
        };
        var update = new WabbajackCollectionUpdate(With((typeof(IWabbajackGalleryService), gallery), (typeof(ModlistUpdater), updater)), group, entry);

        var review = await update.Review(null, CancellationToken.None);

        review.Downloads.Select(download => (download.Name, download.NeedsBrowser)).Should().Equal(("by-hand.7z", true), ("fetched.7z", false));
        review.DownloadSize.Should().Be(Size.From(8_000));
        review.NewBytesUpperBound.Should().Be(Size.From(20_000));
        review.DiskNote.Should().StartWith("Plus room for the largest ").And.Contain("until Finish update");
        review.Added.Should().Equal("New mod");
        review.Changed.Should().Equal("Edited mod", "textures/a.dds (tried again)");
        review.Renamed.Should().Equal(new ReviewRename("Old", "New"));
        review.Dropped.Should().Equal("Gone mod");
        review.ModOrderChanges.Should().Equal("Moved mod");
        review.UserEditsKept.Should().Equal("Contested mod", "Plugin.esp");
        review.GameFilesKept.Should().Equal("Data/Keep.esp");
        review.Warnings.Should().Equal("careful");
        review.WritesRestorePoint.Should().BeTrue();
        review.DroppedAreRemovedNotAsked.Should().BeTrue();
    }

    [Fact]
    public async Task ApplyHandsTheReviewedGameFilesAndTheChoiceToSkipToTheUpdater()
    {
        var (group, two, entry) = await InstalledAndNewer();
        var gallery = new WabbajackGalleryViewModelTests.FakeGallery();
        gallery.ByHash[entry.MachineUrl] = LibraryFileId.From(two.Id);
        var updater = new FakeUpdater(Provider)
        {
            Preview = new ModlistUpdatePreviewResult([], Size.Zero, Size.Zero, [], [], [], [], [], [RelativePath.FromUnsanitizedInput("Data/Keep.esp")], [], [], [], [], []),
            RestorePoint = TxId.From(0x1234),
        };
        var update = new WabbajackCollectionUpdate(With((typeof(IWabbajackGalleryService), gallery), (typeof(ModlistUpdater), updater)), group, entry);
        await update.Review(null, CancellationToken.None);

        var outcome = await update.Apply(skipMissing: true, CancellationToken.None);

        var applied = updater.Applied.Should().ContainSingle().Subject;
        applied.File.Should().Be(two.Id);
        applied.SkipMissing.Should().BeTrue();
        applied.Keep.Should().BeEquivalentTo([RelativePath.FromUnsanitizedInput("Data/Keep.esp")]);
        outcome.Updated.Should().BeTrue();
        outcome.Error.Should().BeNull();
        outcome.RestorePoint.Should().Be(TxId.From(0x1234));
        outcome.Undo.Should().NotBeNull("the update wrote a restore point");
        outcome.ToastText.Should().Be($"{update.Name} updated to Version 2.0.");
    }

    [Fact]
    public async Task ARefusedApplyIsAnOutcomeWithTheReasonAndNothingToUndo()
    {
        var (group, two, entry) = await InstalledAndNewer();
        var gallery = new WabbajackGalleryViewModelTests.FakeGallery();
        gallery.ByHash[entry.MachineUrl] = LibraryFileId.From(two.Id);
        var updater = new FakeUpdater(Provider)
        {
            ApplyThrows = new ModlistUpdateRefusedException("2 archives are still missing: a.7z; nothing was changed"),
            RestorePoint = TxId.From(0x1234),
        };
        var update = new WabbajackCollectionUpdate(With((typeof(IWabbajackGalleryService), gallery), (typeof(ModlistUpdater), updater)), group, entry);

        var outcome = await update.Apply(skipMissing: false, CancellationToken.None);

        outcome.Updated.Should().BeFalse();
        outcome.Error.Should().BeOfType<ModlistUpdateRefusedException>();
        outcome.Undo.Should().BeNull("a refusal is made before anything is written, even if an earlier update left a restore point");
        outcome.ToastText.Should().Contain("2 archives are still missing");
    }

    [Fact]
    public async Task AnApplyThatStoppedAfterTheSwitchOffersUndo()
    {
        var (group, two, entry) = await InstalledAndNewer();
        var gallery = new WabbajackGalleryViewModelTests.FakeGallery();
        gallery.ByHash[entry.MachineUrl] = LibraryFileId.From(two.Id);
        var updater = new FakeUpdater(Provider) { ApplyThrows = new IOException("disk full"), RestorePoint = TxId.From(0x1234) };
        var update = new WabbajackCollectionUpdate(With((typeof(IWabbajackGalleryService), gallery), (typeof(ModlistUpdater), updater)), group, entry);

        var outcome = await update.Apply(skipMissing: false, CancellationToken.None);

        outcome.Error.Should().BeOfType<IOException>();
        outcome.Undo.Should().NotBeNull("the group was switched and the restore point is the way back");
    }

    [Fact]
    public async Task TheToastEscapesTheListNameAndTheErrorMessageForMarkdown()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Escapes {Guid.NewGuid()}");
        var machineUrl = $"escape-{Guid.NewGuid():N}";
        var one = await AddList("1.0", machineUrl, name: "My_List [beta]*");
        var two = await AddList("2.0", machineUrl, name: "My_List [beta]*");
        var group = await Install(loadout.LoadoutId, one);
        var gallery = new WabbajackGalleryViewModelTests.FakeGallery();
        var entry = Entry("2.0", machineUrl);
        gallery.ByHash[entry.MachineUrl] = LibraryFileId.From(two.Id);
        var updater = new FakeUpdater(Provider) { ApplyThrows = new IOException("disk _full_ [now]"), RestorePoint = TxId.From(0x1234) };
        var update = new WabbajackCollectionUpdate(With((typeof(IWabbajackGalleryService), gallery), (typeof(ModlistUpdater), updater)), group, entry);

        var failed = await update.Apply(skipMissing: false, CancellationToken.None);
        updater.ApplyThrows = null;
        var applied = await update.Apply(skipMissing: false, CancellationToken.None);

        failed.ToastText.Should().Contain(MarkdownText.InlineMidLine("My_List [beta]*")).And.Contain(MarkdownText.Inline("disk _full_ [now]"))
            .And.NotContain("My_List [beta]*").And.NotContain("_full_");
        applied.ToastText.Should().StartWith(MarkdownText.Inline("My_List [beta]*")).And.NotContain("My_List [beta]*");
    }

    [Fact]
    public async Task ADiskAddedFileTakenAsTheNewReleaseByItsHashGetsTheGalleryEntrysIdentityAndSoFollowsAnUndo()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Backfill {Guid.NewGuid()}");
        var machineUrl = $"backfill-{Guid.NewGuid():N}";
        var one = await AddList("1.0", machineUrl);
        WabbajackHash.Parse("AQIDBAUGBwg=").TryXxHash64(out var xx).Should().BeTrue();
        var byHand = await AddList("2.0", xxHash64: xx);
        var group = await Install(loadout.LoadoutId, one);
        var gallery = new WabbajackGalleryViewModelTests.FakeGallery();
        var entry = Entry("2.0", machineUrl);
        gallery.ByHash[entry.MachineUrl] = LibraryFileId.From(byHand.Id);
        var updater = new FakeUpdater(Provider);
        var update = new WabbajackCollectionUpdate(With((typeof(IWabbajackGalleryService), gallery), (typeof(ModlistUpdater), updater)), group, entry);

        await update.Review(null, CancellationToken.None);

        var taken = WabbajackModlistLibraryFile.Load(Connection.Db, byHand.Id);
        WabbajackModlistLibraryFile.MachineUrl.TryGetValue(taken, out var recordedUrl).Should().BeTrue("the update records which list the file is a release of");
        recordedUrl.Should().Be(machineUrl);
        WabbajackModlistLibraryFile.Repository.TryGetValue(taken, out var recordedRepository).Should().BeTrue();
        recordedRepository.Should().Be(Repository);

        await Switch(group, one, byHand);
        await WabbajackGroupSeed.SwitchBack(Connection, group.Id, one.Id, byHand.Id);
        WabbajackModlistInstallState.SuccessorOf(Connection.Db, LibraryFileId.From(byHand.Id), loadout.LoadoutId)
            .Should().Be(LibraryFileId.From(one.Id), "after the undo the tab of the new file goes back to the page that holds the install");
    }

    [Fact]
    public void TheFinishedAndUndoneToastsEscapeTheNameWhereItSitsInTheSentence()
    {
        WabbajackUpdateMenuItems.FinishedText("- My_List [beta]*").Should().StartWith("\u200B-", "a name that opens the sentence is escaped as a line start, so a list marker is defused")
            .And.Contain(MarkdownText.Inline("- My_List [beta]*")).And.NotContain("My_List [beta]*");
        WabbajackUpdateMenuItems.UndoneText("My_List [beta]*", "Version 1_0 [x]").Should()
            .Contain(MarkdownText.InlineMidLine("My_List [beta]*")).And.Contain(MarkdownText.InlineMidLine("Version 1_0 [x]"))
            .And.NotContain("My_List [beta]*").And.NotContain("1_0 [x]");
    }

    // ---- the superseded old version ----

    [Fact]
    public async Task TheOldFileReadsSupersededOnTheCardAndInTheInstallReasonAfterTheSwitch()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Superseded {Guid.NewGuid()}");
        var one = await AddList("1.0", machineUrl: "super-list");
        var two = await AddList("2.0", machineUrl: "super-list");
        var group = await Install(loadout.LoadoutId, one);
        WabbajackModlistInstallState.Read(Connection.Db, LibraryFileId.From(one.Id), loadout.LoadoutId).Should().Be(WabbajackModlistCardState.Installed);

        await Switch(group, one, two);

        var old = WabbajackModlistInstallState.ReadInstall(Connection.Db, LibraryFileId.From(one.Id), loadout.LoadoutId);
        old.State.Should().Be(WabbajackModlistCardState.Superseded);
        old.SupersededBy.Should().Be("2.0");
        WabbajackModlistInstallState.Read(Connection.Db, LibraryFileId.From(two.Id), loadout.LoadoutId).Should().Be(WabbajackModlistCardState.Incomplete, "the new file's install is waiting to be resumed");

        var card = new WabbajackModlistCardViewModel(Provider, Substitute.For<IWindowManager>(), WorkspaceId.From(Guid.NewGuid()), one, loadout.LoadoutId);
        card.State.Should().Be(WabbajackModlistCardState.Superseded);
        card.SupersededBy.Should().Be("2.0");

        WabbajackModlistText.InstallBlockedReason(true, false, true, false, new ArchivePlan([]), false, supersededBy: "2.0")
            .Should().Contain("2.0").And.Contain("Update that one");
    }

    [Fact]
    public async Task ADiskAddedFileWithoutIdentityIsNeverSuperseded()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"By hand {Guid.NewGuid()}");
        var byHand = await AddList("1.0");
        var other = await AddList("2.0");
        await Install(loadout.LoadoutId, other);

        var state = WabbajackModlistInstallState.ReadInstall(Connection.Db, LibraryFileId.From(byHand.Id), loadout.LoadoutId);

        state.State.Should().Be(WabbajackModlistCardState.NotInstalled);
        state.SupersededBy.Should().BeNull();
    }

    [Fact]
    public async Task ANewerReleaseOfTheSameGalleryListInstalledInAnotherLoadoutOfTheGameSupersedesAnOlderFile()
    {
        var elsewhere = await SeedLoadoutRow(GameStore.Steam, $"Elsewhere {Guid.NewGuid()}");
        var here = await SeedLoadoutRow(GameStore.Steam, $"Here {Guid.NewGuid()}");
        var older = await AddList("1.0", machineUrl: "across-list", dateUpdated: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        var newer = await AddList("2.0", machineUrl: "across-list", dateUpdated: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        await Install(elsewhere.LoadoutId, newer);

        WabbajackModlistInstallState.ReadInstall(Connection.Db, LibraryFileId.From(older.Id), here.LoadoutId).SupersededBy.Should().Be("2.0");
        WabbajackModlistInstallState.ReadInstall(Connection.Db, LibraryFileId.From(newer.Id), here.LoadoutId).State.Should().Be(WabbajackModlistCardState.NotInstalled);
    }

    // ---- the pending list ----

    [Fact]
    public async Task ThePendingListExcludesTheNewFileBeforeTheSwitchAndTheOldFileAfterItButKeepsAMidUpdateGroupsResumeEntry()
    {
        var game = new TestGame(GameId.From("CreationEngine.SkyrimSE"), SilverModManager.Sdk.NexusModsApi.NexusModsGameId.From(1704));
        var loadout = Loadout.Load(Connection.Db, (await TestGame.AddLoadout(Connection, game)).Value);
        var machineUrl = $"pending-{Guid.NewGuid():N}";
        var one = await AddList("1.0", machineUrl);
        var two = await AddList("2.0", machineUrl);
        var unrelated = await AddList("1.0", machineUrl: $"unrelated-{Guid.NewGuid():N}");
        var group = await Install(loadout.LoadoutId, one);
        Exception? failure = null;
        using var pending = new WabbajackModlistProvider(Provider).ObservePending(loadout).Do(_ => { }, error => failure = error).Catch(System.Reactive.Linq.Observable.Empty<IChangeSet<CollectionSource, EntityId>>()).AsObservableCache();
        await WaitFor(() => failure is not null || pending.Lookup(unrelated.Id).HasValue);
        failure.Should().BeNull();

        pending.Lookup(one.Id).HasValue.Should().BeFalse("it is installed");
        pending.Lookup(two.Id).HasValue.Should().BeFalse("installing it would be an update of the installed list");

        await Switch(group, one, two);

        await WaitFor(() => pending.Lookup(two.Id).HasValue);
        pending.Lookup(two.Id).HasValue.Should().BeTrue("the update stopped halfway, so its entry is where it is resumed");
        pending.Lookup(one.Id).HasValue.Should().BeFalse("the version the group was switched away from is not an install");
        pending.Lookup(unrelated.Id).HasValue.Should().BeTrue();
    }

    // ---- the menu ----

    private static ICollectionPageHost Host(IWindowManager? windows = null)
    {
        var host = Substitute.For<ICollectionPageHost>();
        host.WindowManager.Returns(windows ?? Substitute.For<IWindowManager>());
        return host;
    }

    [Fact]
    public async Task UndoAndFinishAreOfferedWhileARestorePointIsRecordedAndWithdrawnAfterFinish()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Menu {Guid.NewGuid()}");
        var file = await AddList("2.0", machineUrl: "menu-list");
        var group = await Install(loadout.LoadoutId, file);
        var source = new WabbajackModlistSource(Provider, loadout.LoadoutId, file);
        source.CreateDownloads();
        var changes = 0;
        using var subscription = source.MenuChanged.Subscribe(_ => changes++);
        await WaitFor(() => changes == 1);
        string[] Headers() => [.. source.MenuItems(Host(), null).Select(item => item.Header)];

        Headers().Should().NotContain("Undo update").And.NotContain("Finish update");

        await RecordRestorePoint(group.Id);

        await WaitFor(() => changes == 2);
        Headers().Should().Contain("Undo update").And.Contain("Finish update");
        Headers()[^1].Should().Be("Reinstall modlist", "the page's Remove item stays next to Reinstall");

        await new ModlistUpdateFinish(Provider).Finish(WabbajackModlistLoadoutGroup.Load(Connection.Db, group.Id), deleteOldArchives: false, CancellationToken.None);

        await WaitFor(() => changes == 3);
        Headers().Should().NotContain("Undo update").And.NotContain("Finish update");
    }

    /// <summary>A modlist page whose loadout picker the test moves, as the Downloads tab's picker does.</summary>
    private sealed class MovablePicker : WabbajackModlistDesignViewModel, IWabbajackModlistViewModel
    {
        private WabbajackLoadoutChoice? _selected;

        public new WabbajackLoadoutChoice? SelectedLoadout
        {
            get => _selected;
            set => this.RaiseAndSetIfChanged(ref _selected, value);
        }
    }

    [Fact]
    public async Task SwitchingThePickerBetweenTwoLoadoutsThatBothHaveARestorePointRebuildsTheMenuForTheOtherGroup()
    {
        var first = await SeedLoadoutRow(GameStore.Steam, $"Picker one {Guid.NewGuid()}");
        var second = await SeedLoadoutRow(GameStore.Steam, $"Picker two {Guid.NewGuid()}");
        var file = await AddList("2.0", machineUrl: "two-loadouts-list");
        var inFirst = await Install(first.LoadoutId, file);
        var inSecond = await Install(second.LoadoutId, file);
        await RecordRestorePoint(inFirst.Id);
        await RecordRestorePoint(inSecond.Id);
        var model = new MovablePicker();
        var source = new WabbajackModlistSource(With((typeof(IWabbajackModlistViewModel), model)), loadoutId: null, file);
        source.CreateDownloads();
        var changes = 0;
        using var subscription = source.MenuChanged.Subscribe(_ => changes++);
        await WaitFor(() => changes >= 1);
        var before = changes;

        model.SelectedLoadout = new WabbajackLoadoutChoice(first.LoadoutId, "first");
        await WaitFor(() => changes == before + 1);
        model.SelectedLoadout = new WabbajackLoadoutChoice(second.LoadoutId, "second");

        await WaitFor(() => changes == before + 2);
    }

    [Fact]
    public async Task ThePickerIsOfferedForAListWithNoIdentityAndSeveralCandidatesAndWritesTheChoice()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Picker {Guid.NewGuid()}");
        var link = $"https://authored-files.wabbajack.org/picker-{Guid.NewGuid():N}.wabbajack";
        var file = await AddList("1.0", name: $"Picked list {Guid.NewGuid():N}", galleryUrl: link);
        GalleryEntry Candidate(string repository, string machineUrl) => Entry("1.0", machineUrl, title: file.Name, repository: repository, download: link);
        var client = new FakeClient(Listing(Candidate("first/repo", "picked-a"), Candidate("second/repo", "picked-b")));
        var windows = Substitute.For<IWindowManager>();
        windows.ShowDialog(Arg.Any<IDialog>(), DialogWindowType.Modal).Returns(new StandardDialogResult { ButtonId = ButtonDefinitionId.From("pick-1") });
        var source = new WabbajackModlistSource(With((typeof(IWabbajackGalleryClient), client)), loadout.LoadoutId, file);
        source.CreateDownloads();
        var changes = 0;
        using var subscription = source.MenuChanged.Subscribe(_ => changes++);
        await WaitFor(() => changes == 1);

        var picker = source.MenuItems(Host(windows), null).Single(item => item.Header == "Which gallery entry is this list?");
        await picker.Command.Execute();

        var written = WabbajackModlistLibraryFile.Load(Connection.Db, file.Id);
        WabbajackModlistLibraryFile.MachineUrl.TryGetValue(written, out var machineUrl).Should().BeTrue();
        machineUrl.Should().Be("picked-b");
        WabbajackModlistLibraryFile.Repository.TryGetValue(written, out var repository).Should().BeTrue();
        repository.Should().Be("second/repo");
        await WaitFor(() => changes == 2);
        source.MenuItems(Host(), null).Select(item => item.Header).Should().NotContain("Which gallery entry is this list?", "the identity is known now");
    }

    [Fact]
    public async Task ThePickerIsOfferedForAListWithOneCandidateTheBackfillDeclined()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Picker one {Guid.NewGuid()}");
        var link = $"https://authored-files.wabbajack.org/picker-one-{Guid.NewGuid():N}.wabbajack";
        var file = await AddList("1.0", name: $"Single list {Guid.NewGuid():N}", galleryUrl: link);
        var candidate = Entry("1.0", "single-a", title: file.Name, repository: "first/repo", download: link);
        // Another repository lists the same title, so the backfill leaves the file for the user to decide.
        var lookalike = Entry("1.0", "single-b", title: file.Name, repository: "second/repo", download: "https://example.org/other.wabbajack");
        var client = new FakeClient(Listing(candidate, lookalike));
        var shown = new List<Dialog>();
        var windows = Substitute.For<IWindowManager>();
        windows.ShowDialog(Arg.Do<IDialog>(d => shown.Add((Dialog)d)), DialogWindowType.Modal).Returns(new StandardDialogResult { ButtonId = ButtonDefinitionId.From("pick-0") });
        var source = new WabbajackModlistSource(With((typeof(IWabbajackGalleryClient), client)), loadout.LoadoutId, file);
        source.CreateDownloads();
        var changes = 0;
        using var subscription = source.MenuChanged.Subscribe(_ => changes++);
        await WaitFor(() => changes == 1);

        var picker = source.MenuItems(Host(windows), null).Single(item => item.Header == "Which gallery entry is this list?");
        await picker.Command.Execute();

        var dialog = shown.Should().ContainSingle().Subject;
        dialog.ViewModel.ContentViewModel.Should().BeAssignableTo<IDialogStandardContentViewModel>().Subject.Text.Should().Be(Language.CollectionUpdate_Pick_Text_One);
        dialog.ViewModel.ButtonDefinitions.Select(b => b.Id).Should().Equal(ButtonDefinitionId.Cancel, ButtonDefinitionId.From("pick-0"));
        var written = WabbajackModlistLibraryFile.Load(Connection.Db, file.Id);
        written.MachineUrl.Value.Should().Be("single-a");
        written.Repository.Value.Should().Be("first/repo");
        await WaitFor(() => changes == 2);
        source.MenuItems(Host(), null).Select(item => item.Header).Should().NotContain("Which gallery entry is this list?");
    }

    [Fact]
    public async Task AnUpdateAppearsForAListWhoseGalleryLinkMovedToANewRelease()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Renewed {Guid.NewGuid()}");
        var file = await AddList("1.0", name: $"Renewed list {Guid.NewGuid():N}", galleryUrl: "https://authored-files.wabbajack.org/renewed.wabbajack_00000000-0000-0000-0000-000000000001");
        var group = await Install(loadout.LoadoutId, file);
        var listing = Listing(Entry("2.0", "renewed-list", title: file.Name, download: "https://authored-files.wabbajack.org/renewed.wabbajack_39f36b83-0000-0000-0000-000000000002"));
        var offers = new List<ICollectionUpdate?>();
        using var subscription = Offer(new FakeClient(listing), group).Subscribe(offers.Add);
        offers.Last().Should().BeNull("the list has no gallery identity yet");

        await Provider.GetRequiredService<ModlistIdentityBackfill>().Run(listing, CancellationToken.None);

        await WaitFor(() => offers.Last() is not null);
        offers.Last()!.NewVersionText.Should().Be("Version 2.0");
    }

    // ---- following the install to the new file ----

    [Fact]
    public async Task ThePageOfTheOldFileMovesToTheNewFilesPageOnceAnUpdateSwitchedTheGroup()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Moves {Guid.NewGuid()}");
        var one = await AddList("1.0", machineUrl: "moves-list");
        var two = await AddList("2.0", machineUrl: "moves-list");
        var group = await Install(loadout.LoadoutId, one);
        var source = new WabbajackModlistSource(Provider, loadout.LoadoutId, one);
        var moves = new List<PageData>();
        using var subscription = source.MovedTo.Subscribe(moves.Add);
        moves.Should().BeEmpty();

        await Switch(group, one, two);

        await WaitFor(() => moves.Count == 1);
        moves[0].FactoryId.Should().Be(WabbajackModlistPageFactory.StaticId);
        moves[0].Context.Should().Be(new WabbajackModlistPageContext { LibraryFileId = LibraryFileId.From(two.Id), TargetLoadout = loadout.LoadoutId });
    }

    [Fact]
    public async Task AnOpenTabOfTheOldFileReopensOnTheNewFile()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Reopen {Guid.NewGuid()}");
        var one = await AddList("1.0", machineUrl: "reopen-list");
        var two = await AddList("2.0", machineUrl: "reopen-list");
        var group = await Install(loadout.LoadoutId, one);
        await Switch(group, one, two);

        var page = new WabbajackModlistPageFactory(Provider).CreateViewModel(
            new WabbajackModlistPageContext { LibraryFileId = LibraryFileId.From(one.Id), TargetLoadout = loadout.LoadoutId });

        page.Name.Should().Be(two.Name);
    }

    [Fact]
    public async Task UndoNamesTheVersionTheListIsBackOnNotTheOneThatWasUndone()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Undo text {Guid.NewGuid()}");
        var one = await AddList("3.2.1", machineUrl: "undo-text-list");
        var two = await AddList("3.3.0", machineUrl: "undo-text-list");
        var group = await Install(loadout.LoadoutId, one);
        await Switch(group, one, two);

        var restored = await WabbajackCollectionUpdate.Undo(Provider, WabbajackModlistLoadoutGroup.Load(Connection.Db, group.Id), CancellationToken.None);

        restored.Should().Be("Version 3.2.1");
        WabbajackModlistLoadoutGroup.Load(Connection.Db, group.Id).Modlist.Id.Should().Be(one.Id);
    }

    [Fact]
    public async Task TheTabOfTheNewFileGoesBackToTheOldFilesPageOnceTheUpdateWasUndone()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Moves back {Guid.NewGuid()}");
        var one = await AddList("1.0", machineUrl: "back-list");
        var two = await AddList("2.0", machineUrl: "back-list");
        var group = await Install(loadout.LoadoutId, one);
        await Switch(group, one, two);
        var source = new WabbajackModlistSource(Provider, loadout.LoadoutId, two);
        var moves = new List<PageData>();
        using var subscription = source.MovedTo.Subscribe(moves.Add);
        moves.Should().BeEmpty("the new file is where the install is");

        await WabbajackGroupSeed.SwitchBack(Connection, group.Id, one.Id, two.Id);

        WabbajackModlistInstallState.ReadInstall(Connection.Db, LibraryFileId.From(two.Id), loadout.LoadoutId).State
            .Should().Be(WabbajackModlistCardState.NotInstalled, "the page of the new file would offer a second install of the same list");
        await WaitFor(() => moves.Count == 1);
        moves[0].Context.Should().Be(new WabbajackModlistPageContext { LibraryFileId = LibraryFileId.From(one.Id), TargetLoadout = loadout.LoadoutId });
    }

    // ---- a newer file's page while an older version of the list is installed ----

    private async Task<(Loadout.ReadOnly Loadout, WabbajackModlistLibraryFile.ReadOnly One, WabbajackModlistLibraryFile.ReadOnly Two, WabbajackModlistLoadoutGroup.ReadOnly Group)> OlderInstalled(string tag)
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"{tag} {Guid.NewGuid()}");
        var machineUrl = $"{tag}-{Guid.NewGuid():N}";
        var one = await AddList("3.2", machineUrl);
        var two = await AddList("3.3", machineUrl);
        var group = await Install(loadout.LoadoutId, one);
        return (loadout, one, two, group);
    }

    [Fact]
    public async Task OpeningTheNewerFilesPageWhileTheOlderVersionIsInstalledKeepsThatFilesPage()
    {
        var (loadout, _, two, _) = await OlderInstalled("open-newer");

        var page = new WabbajackModlistPageFactory(Provider).CreateViewModel(
            new WabbajackModlistPageContext { LibraryFileId = LibraryFileId.From(two.Id), TargetLoadout = loadout.LoadoutId });

        page.VersionText.Should().Be("Version 3.3", "the user opened this file, and the page must not silently become the installed version's");
    }

    [Fact]
    public async Task TheNewerFilesTabStaysWhereItIsWhileTheOlderVersionIsInstalledAndOtherRevisionsLand()
    {
        var (loadout, _, two, _) = await OlderInstalled("stay-newer");
        var source = new WabbajackModlistSource(Provider, loadout.LoadoutId, two);
        var moves = new List<PageData>();
        using var subscription = source.MovedTo.Subscribe(moves.Add);

        await AddList("9.9", machineUrl: "unrelated-list");
        await Task.Delay(300);

        moves.Should().BeEmpty();
    }

    [Fact]
    public async Task TheNewerFilesTabStaysWhenTheOlderVersionIsInstalledOrReinstalledWhileThePageIsOpen()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"never-held {Guid.NewGuid()}");
        var machineUrl = $"never-held-{Guid.NewGuid():N}";
        var one = await AddList("3.2", machineUrl);
        var two = await AddList("3.3", machineUrl);
        var source = new WabbajackModlistSource(Provider, loadout.LoadoutId, two);
        var moves = new List<PageData>();
        using var subscription = source.MovedTo.Subscribe(moves.Add);

        var group = await Install(loadout.LoadoutId, one);
        await Task.Delay(300);
        await WabbajackGroupSeed.Delete(Connection, group.Id);
        await Install(loadout.LoadoutId, one);
        await Task.Delay(300);

        moves.Should().BeEmpty("the newer file never had a group here, so its page is not the older version's to follow");
    }

    [Fact]
    public async Task AnOlderFileWithoutDatesIsSupersededByTheNewerVersionInstalledHereAndOffersNoUpdateToItself()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"downgrade {Guid.NewGuid()}");
        var machineUrl = $"downgrade-{Guid.NewGuid():N}";
        var one = await AddList("3.2", machineUrl);
        var two = await AddList("3.3", machineUrl);
        await Install(loadout.LoadoutId, two);

        var install = WabbajackModlistInstallState.ReadInstall(Connection.Db, LibraryFileId.From(one.Id), loadout.LoadoutId);
        var offer = await new WabbajackModlistSource(Provider, loadout.LoadoutId, one).UpdateToThisVersion.FirstAsync();

        install.State.Should().Be(WabbajackModlistCardState.Superseded);
        install.SupersededBy.Should().Be("3.3");
        install.OtherVersion.Should().BeNull();
        offer.Should().BeNull("updating to the older release would be a downgrade");
    }

    [Fact]
    public async Task TheLibraryCardOfTheNewerFileHidesInstallAndStatesTheInstalledVersion()
    {
        var (loadout, _, two, _) = await OlderInstalled("card-view");
        var card = new WabbajackModlistCardViewModel(Provider, Substitute.For<IWindowManager>(), WorkspaceId.From(Guid.NewGuid()), two, loadout.LoadoutId);
        var (window, view) = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = new WabbajackModlistCardView { DataContext = card };
            var window = Track(new Window { Width = 600, Height = 400, Content = view });
            ShowRendered(window);
            return (window, view);
        });

        await EventuallyOnUi(() =>
        {
            Render(window);
            view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "StateText").Text.Should().Contain("3.2");
            view.GetVisualDescendants().OfType<StandardButton>().Single(b => b.Name == "InstallButton").IsVisible.Should().BeFalse("a plain Install would put a second copy of the list beside version 3.2");
        });
        await OnUi(window.Close);
    }

    [Fact]
    public async Task TheNewerFilesPageReadsThatTheOlderVersionIsInstalledHereWhileItsStateStaysNotInstalled()
    {
        var (loadout, one, two, group) = await OlderInstalled("reads-other");

        var install = WabbajackModlistInstallState.ReadInstall(Connection.Db, LibraryFileId.From(two.Id), loadout.LoadoutId);

        install.State.Should().Be(WabbajackModlistCardState.NotInstalled);
        install.OtherVersion.Should().Be("3.2");
        install.OtherGroup.Should().Be(group.Id);
        WabbajackModlistInstallState.ReadInstall(Connection.Db, LibraryFileId.From(one.Id), loadout.LoadoutId).OtherVersion.Should().BeNull("the installed file is installed");
        var card = new WabbajackModlistCardViewModel(Provider, Substitute.For<IWindowManager>(), WorkspaceId.From(Guid.NewGuid()), two, loadout.LoadoutId);
        card.OtherVersion.Should().Be("3.2", "the card must not offer a plain Install that would create a second group");
    }

    [Fact]
    public async Task TheNewerFilesPageOffersToUpdateToItselfAndNamesTheInstalledVersion()
    {
        var (loadout, _, two, _) = await OlderInstalled("offers-self");
        var source = new WabbajackModlistSource(Provider, loadout.LoadoutId, two);

        var line = await source.StateLine.FirstAsync();
        var offer = await source.UpdateToThisVersion.FirstAsync();

        line.Should().Contain("3.2");
        offer.Should().NotBeNull();
        offer!.CurrentVersionText.Should().Be("Version 3.2");
        offer.NewVersionText.Should().Be("Version 3.3");
        (await source.Group.FirstAsync()).HasValue.Should().BeFalse("the file itself has no group, so the page does not claim an install");
    }

    [Fact]
    public async Task AnUpdateToAChosenLibraryFileReviewsAndAppliesThatFileWithoutTheGallery()
    {
        var (_, _, two, group) = await OlderInstalled("chosen-file");
        var gallery = Substitute.For<IWabbajackGalleryService>();
        var updater = new FakeUpdater(Provider) { Downloaded = true };
        var update = new WabbajackCollectionUpdate(With((typeof(IWabbajackGalleryService), gallery), (typeof(ModlistUpdater), updater)), group, two);

        await update.Review(null, CancellationToken.None);
        await update.Apply(skipMissing: false, CancellationToken.None);

        updater.Reviewed.Should().OnlyContain(id => id == two.Id);
        updater.Applied.Should().ContainSingle().Which.File.Should().Be(two.Id);
        gallery.ReceivedCalls().Should().BeEmpty();
        update.NewVersionText.Should().Be("Version 3.3");
    }

    // ---- the gallery ----

    private async Task<(WabbajackGalleryViewModel Vm, List<PageData> Pages, List<ICollectionUpdate> Updates)> OpenGallery(LoadoutId loadout, IWabbajackGalleryClient client)
    {
        var pages = new List<PageData>();
        var updates = new List<ICollectionUpdate>();
        var service = new WabbajackGalleryService(client, Provider.GetRequiredService<WabbajackGalleryDownloader>(), Connection);
        var vm = new WabbajackGalleryViewModel(
            Provider.GetRequiredService<IWindowManager>(), Connection, new GalleryServiceOverClient(service, client), Substitute.For<IOSInterop>(),
            Substitute.For<SilverModManager.App.UI.Controls.Cards.IThumbnailLoader>(), new InMemoryStoreSortMemory(), new FakeAdultContentSetting(),
            NullLogger<WabbajackGalleryViewModel>.Instance, Provider)
        {
            LoadoutId = loadout,
            OpenPage = pages.Add,
            RunUpdate = update => { updates.Add(update); return Task.CompletedTask; },
        };
        await vm.LoadAsync(CancellationToken.None);
        return (vm, pages, updates);
    }

    /// <summary>The real service for the library questions, with the listing read from the fake client.</summary>
    private sealed class GalleryServiceOverClient(WabbajackGalleryService inner, IWabbajackGalleryClient client) : IWabbajackGalleryService
    {
        public Task<GalleryListing> GetListing(bool refresh, CancellationToken cancellationToken) => client.Get(refresh, cancellationToken);
        public IReadOnlyDictionary<string, LibraryFileId> ListsInLibrary() => inner.ListsInLibrary();
        public LibraryFileId? FindInLibrary(GalleryEntry entry) => inner.FindInLibrary(entry);
        public InstalledModlist? InstalledOlderThan(GalleryEntry entry, LoadoutId loadout) => inner.InstalledOlderThan(entry, loadout);
        public Task<LibraryFileId> Download(GalleryEntry entry, CancellationToken cancellationToken) => inner.Download(entry, cancellationToken);
    }

    [Fact]
    public async Task TheGalleryCardReadsUpdateForANewerVersionOfAnInstalledList()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Gallery {Guid.NewGuid()}");
        var machineUrl = $"gallery-{Guid.NewGuid():N}";
        var file = await AddList("1.0", machineUrl);
        var group = await Install(loadout.LoadoutId, file);
        var client = new FakeClient(Listing(
            Entry("2.0", machineUrl, title: "Gallery list") with { GalleryGame = "cyberpunk2077" },
            Entry("1.0", "not-installed", title: "Other list") with { GalleryGame = "cyberpunk2077" }));
        var (vm, pages, updates) = await OpenGallery(loadout.LoadoutId, client);

        var rows = vm.Lists.ToDictionary(row => row.Title);
        rows["Gallery list"].InstallButtonText.Should().Be("Update");
        rows["Gallery list"].CanInstall.Should().BeTrue();
        rows["Other list"].InstallButtonText.Should().Be("Install");

        await vm.InstallCommand.Execute(rows["Gallery list"]);

        var update = updates.Should().ContainSingle().Subject;
        update.CurrentVersionText.Should().Be("Version 1.0");
        update.NewVersionText.Should().Be("Version 2.0");
        pages.Should().ContainSingle().Which.Context.Should().Be(new WabbajackModlistPageContext { LibraryFileId = LibraryFileId.From(file.Id), TargetLoadout = loadout.LoadoutId });
    }

    [Fact]
    public async Task TheGalleryOffersUpdateOnlyForAnEntryNewerThanTheInstalledFile()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Gallery same {Guid.NewGuid()}");
        var machineUrl = $"gallery-same-{Guid.NewGuid():N}";
        await Install(loadout.LoadoutId, await AddList("1.0", machineUrl));
        var client = new FakeClient(Listing(Entry("1.0", machineUrl, title: "Same list") with { GalleryGame = "cyberpunk2077" }));
        var (vm, _, _) = await OpenGallery(loadout.LoadoutId, client);

        vm.Lists.Single().InstallButtonText.Should().Be("Install");
    }

    [Fact]
    public async Task TheGalleryOffersNoUpdateForAListWhoseEarlierUpdateNeverFinished()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Gallery mid update {Guid.NewGuid()}");
        var machineUrl = $"gallery-mid-{Guid.NewGuid():N}";
        var one = await AddList("1.0", machineUrl);
        var two = await AddList("2.0", machineUrl);
        var group = await Install(loadout.LoadoutId, one);
        var service = new WabbajackGalleryService(new FakeClient(null), Provider.GetRequiredService<WabbajackGalleryDownloader>(), Connection);
        service.InstalledOlderThan(Entry("2.0", machineUrl), loadout.LoadoutId).Should().NotBeNull("the group is on the older release");

        await Switch(group, one, two);

        service.InstalledOlderThan(Entry("3.0", machineUrl), loadout.LoadoutId).Should().BeNull();
    }

    [Fact]
    public async Task TheLibrarysCopyOfAReleaseIsFoundByItsPublishedHash()
    {
        var published = WabbajackHash.Parse("AQIDBAUGBwg=");
        published.TryXxHash64(out var xx).Should().BeTrue();
        var held = await AddList("2.0", machineUrl: "hash-list", xxHash64: xx);
        var service = new WabbajackGalleryService(new FakeClient(null), Provider.GetRequiredService<WabbajackGalleryDownloader>(), Connection);

        service.FindInLibrary(Entry("2.0", "hash-list")).Should().Be(LibraryFileId.From(held.Id));
        service.FindInLibrary(Entry("2.0", "hash-list", hash: "CQkJCQkJCQk=")).Should().BeNull();
    }
}
