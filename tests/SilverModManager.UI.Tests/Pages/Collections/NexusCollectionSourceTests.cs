using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Threading;
using DynamicData;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Sdk.Games;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Collections;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.UI.Sdk.Icons;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages.Collections;

/// <summary>
/// The Nexus source is the collection page's old Nexus branch moved behind <see cref="CollectionSource"/>; these facts
/// pin what the page and the menu showed before the move.
/// </summary>
public class NexusCollectionSourceTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed record Fixture(
        Loadout.ReadOnly Loadout,
        CollectionRevisionMetadata.ReadOnly Revision,
        NexusCollectionLoadoutGroup.ReadOnly Group,
        NexusCollectionSource Installed);

    private async Task<Fixture> Open(
        string name = "Quality of Life",
        Action<CollectionMetadata.New>? configureCollection = null,
        Action<CollectionRevisionMetadata.New>? configureRevision = null,
        ulong revisionNumber = 1,
        IServiceProvider? services = null)
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Nexus source {Guid.NewGuid()}");
        var revision = await CollectionSeed.AddRevision(Connection, loadout.InstallationInstance.Game.NexusModsGameId.Value, name,
            revisionNumber, configureCollection: configureCollection, configureRevision: configureRevision);
        var group = await CollectionSeed.AddInstalledGroup(Connection, loadout.LoadoutId, $"{name} (group)", revision);
        var source = NexusCollectionSource.Installed(services ?? Provider, loadout.LoadoutId, group);
        return new Fixture(loadout, revision, group, source);
    }

    private static ICollectionPageHost Host()
    {
        var host = Substitute.For<ICollectionPageHost>();
        host.WindowManager.Returns(Substitute.For<IWindowManager>());
        host.WorkspaceController.Returns(Substitute.For<IWorkspaceController>());
        return host;
    }

    private static ContextMenuItem ViewDownloads() => new() { Header = "View downloads" };

    [Fact]
    public async Task TheHeaderFieldsComeFromTheRevisionAndItsCollection()
    {
        var f = await Open(
            configureCollection: c => { c.Endorsements = 1234UL; c.TotalDownloads = 99_000UL; },
            configureRevision: r => { r.TotalSize = Size.From(5_000_000UL); r.OverallRating = 0.75f; },
            revisionNumber: 193);

        f.Installed.AuthorName.Should().Be("An author");
        f.Installed.VersionText.Should().Be("Revision 193");
        f.Installed.Stats.Should().Be(new CollectionStats(Percent.CreateClamped(0.75), 1234UL, 99_000UL, Size.From(5_000_000UL), null));
        f.Installed.Badge.Should().Be(new CollectionBadge(IconValues.NexusColor, Language.CollectionLoadoutView_NexusMods));
        f.Installed.IsReadOnly.Should().BeTrue();
        f.Installed.Link!.Text.Should().Be(Language.CollectionLoadoutView_OpenOnNexusMods);
        (await f.Installed.Link.IsVisible.FirstAsync()).Should().BeTrue();
        (await f.Installed.Description.FirstAsync()).Should().BeEmpty("the Nexus header has no description block");
        (await f.Installed.StateLine.FirstAsync()).Should().Be(Language.CollectionLoadoutView_CollectionInstalled);
    }

    [Fact]
    public async Task ANumberNexusDidNotSendReadsAsZeroSoTheHeaderRendersAsBefore()
    {
        var f = await Open();

        f.Installed.Stats.Should().Be(new CollectionStats(Percent.Zero, 0UL, 0UL, Size.Zero, null));
    }

    [Fact]
    public async Task TheInstalledSourceIsNamedAfterTheGroupAndThePendingOneAfterTheCollection()
    {
        var f = await Open("Cool Collection");
        var pending = NexusCollectionSource.Pending(Provider, f.Loadout.LoadoutId, f.Revision);

        f.Installed.InitialName.Should().Be("Cool Collection (group)");
        (await f.Installed.Name.FirstAsync()).Should().Be("Cool Collection (group)");
        pending.InitialName.Should().Be("Cool Collection");
        (await pending.Name.FirstAsync()).Should().Be("Cool Collection");
    }

    [Fact]
    public async Task TheInstalledSourceHasTheGroupAndIsInstalledAndMayBeToggled()
    {
        var f = await Open();

        (await f.Installed.Group.FirstAsync()).Should().Be(Optional<CollectionGroupId>.Create(f.Group.AsCollectionGroup().CollectionGroupId));
        (await f.Installed.IsInstalled.FirstAsync()).Should().BeTrue();
        (await f.Installed.CanToggle.FirstAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task ThePendingSourceHasNoGroupToToggle()
    {
        var f = await Open();
        var pending = NexusCollectionSource.Pending(Provider, f.Loadout.LoadoutId, f.Revision);

        (await pending.Group.FirstAsync()).HasValue.Should().BeFalse();
        (await pending.CanToggle.FirstAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task TheGroupIsStillThereAfterACollectionUpdateSwitchesItToAnotherRevision()
    {
        var f = await Open(revisionNumber: 1);
        var groupId = f.Group.AsCollectionGroup().CollectionGroupId;
        var latest = new List<Optional<CollectionGroupId>>();
        using var _ = f.Installed.Group.Subscribe(latest.Add);

        var next = await CollectionSeed.AddRevision(Connection, f.Revision.Collection.GameId, "ignored", revisionNumber: 2, existing: f.Revision.Collection);
        await CollectionSeed.SwitchRevision(Connection, f.Group, next);

        latest.Should().NotBeEmpty();
        latest.Last().Should().Be(Optional<CollectionGroupId>.Create(groupId), "an update moves the group to the new revision in place, it does not remove it");
    }

    [Fact]
    public async Task TheInstalledPageIsTheOneTheMenuAndTheOpenCollectionHandlerBuildWithNoRevisionId()
    {
        var f = await Open();
        var groupId = f.Group.AsCollectionGroup().CollectionGroupId;

        f.Installed.InstalledPage(groupId).Should().Be(new PageData
        {
            FactoryId = CollectionLoadoutPageFactory.StaticId,
            Context = new CollectionLoadoutPageContext { LoadoutId = f.Loadout.LoadoutId, GroupId = groupId },
        });
    }

    [Fact]
    public async Task ThePendingPageIsTheStandaloneDownloadPage()
    {
        var f = await Open();
        var pending = NexusCollectionSource.Pending(Provider, f.Loadout.LoadoutId, f.Revision);

        pending.PendingPage.Should().Be(new PageData
        {
            FactoryId = CollectionDownloadPageFactory.StaticId,
            Context = new CollectionDownloadPageContext
            {
                TargetLoadout = f.Loadout.LoadoutId,
                CollectionRevisionMetadataId = f.Revision.CollectionRevisionMetadataId,
            },
        });
    }

    [Fact]
    public async Task TheMoreMenuListsMakeEditableCopyViewDownloadsAndReinstallInThatOrder()
    {
        var f = await Open();

        var items = f.Installed.MenuItems(Host(), ViewDownloads());

        items.Select(item => item.Header).Should().Equal(
            Language.CollectionLoadoutView_MakeEditableLocalCopy, "View downloads", Language.CollectionLoadoutView_ReinstallCollection);
    }

    [Fact]
    public async Task WithoutADownloadsTabTheMoreMenuLeavesViewDownloadsOut()
    {
        var f = await Open();

        var items = f.Installed.MenuItems(Host(), viewDownloads: null);

        items.Select(item => item.Header).Should().Equal(
            Language.CollectionLoadoutView_MakeEditableLocalCopy, Language.CollectionLoadoutView_ReinstallCollection);
    }

    [Fact]
    public async Task ReinstallCarriesItsIconAndTooltip()
    {
        var f = await Open();

        var reinstall = f.Installed.MenuItems(Host(), null).Single(item => item.Header == Language.CollectionLoadoutView_ReinstallCollection);

        reinstall.Icon.Should().Be(IconValues.Refresh);
        reinstall.ToolTip.Should().Be(Language.CollectionLoadoutView_ReinstallCollection_ToolTip);
    }

    [Fact]
    public async Task ReinstallCannotRunWhileThisCollectionsInstallIsActiveAndCanOnceItEnds()
    {
        var jobs = new FakeInstallJobs();
        var f = await Open(services: new ServicesWith(Provider, jobs.Monitor));
        var reinstall = f.Installed.MenuItems(Host(), null).Single(item => item.Header == Language.CollectionLoadoutView_ReinstallCollection);

        await Settled();
        (await reinstall.Command.CanExecute.FirstAsync()).Should().BeTrue("no install is running");

        var status = jobs.Add(f.Revision, f.Loadout.LoadoutId, JobStatus.Running);
        await Settled();
        (await reinstall.Command.CanExecute.FirstAsync()).Should().BeFalse("reinstalling would remove the group under the running install");

        status.OnNext(JobStatus.Completed);
        await Settled();
        (await reinstall.Command.CanExecute.FirstAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task AnInstallOfAnotherCollectionDoesNotBlockReinstall()
    {
        var jobs = new FakeInstallJobs();
        var f = await Open(services: new ServicesWith(Provider, jobs.Monitor));
        var other = await CollectionSeed.AddRevision(Connection, f.Revision.Collection.GameId, "Other");
        var reinstall = f.Installed.MenuItems(Host(), null).Single(item => item.Header == Language.CollectionLoadoutView_ReinstallCollection);

        _ = jobs.Add(other, f.Loadout.LoadoutId, JobStatus.Running);
        await Settled();

        (await reinstall.Command.CanExecute.FirstAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task UninstallingIsAPlainAlwaysAllowedItemWithATooltip()
    {
        var f = await Open();
        var removal = f.Installed.InstalledRemoval(f.Group.AsCollectionGroup().CollectionGroupId);

        removal.Header.Should().Be(Language.CollectionLoadoutView_UninstallCollection);
        removal.Icon.Should().Be(IconValues.PlaylistRemove);
        removal.Styling.Should().Be(ContextMenuItemStyling.Default, "it can be undone");
        removal.ToolTip.Should().Be(Language.CollectionLoadoutView_UninstallCollection_ToolTip);
        (await removal.CanExecute.FirstAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task DeletingARevisionIsCritical()
    {
        var f = await Open();
        var pending = NexusCollectionSource.Pending(Provider, f.Loadout.LoadoutId, f.Revision);

        var removal = pending.PendingRemoval.Should().NotBeNull().And.Subject.As<CollectionRemoval>();

        removal.Header.Should().Be(Language.CollectionDownloadView_Menu_DeleteCollectionRevision);
        removal.Icon.Should().Be(IconValues.DeleteOutline);
        removal.Styling.Should().Be(ContextMenuItemStyling.Critical);
        removal.ToolTip.Should().BeNull("the menu entry's item never had one");
        (await removal.CanExecute.FirstAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task DeletingARevisionRemovesItFromTheLibrary()
    {
        var f = await Open();
        var pending = NexusCollectionSource.Pending(Provider, f.Loadout.LoadoutId, f.Revision);

        await pending.PendingRemoval!.Remove(Substitute.For<IWorkspaceController>());

        CollectionRevisionMetadata.Load(Connection.Db, f.Revision.Id).IsValid().Should().BeFalse();
    }

    [Fact]
    public async Task DownloadsAreNullWhenTheCollectionsArchiveLeftTheLibrary()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"No archive {Guid.NewGuid()}");
        var revision = await CollectionSeed.AddRevision(Connection, loadout.InstallationInstance.Game.NexusModsGameId.Value, "Archive gone", withArchive: false);
        var pending = NexusCollectionSource.Pending(Provider, loadout.LoadoutId, revision);

        pending.CreateDownloads().Should().BeNull();
    }

    [Fact]
    public async Task DownloadsAreTheEmbeddedDownloadPageWhenTheArchiveIsInTheLibrary()
    {
        var f = await Open();

        var downloads = await Dispatcher.UIThread.InvokeAsync(() => f.Installed.CreateDownloads());

        downloads.Should().BeOfType<CollectionDownloadViewModel>();
    }

    [Fact]
    public async Task InstructionsAreEmptyUntilActivatedAndThenParsedFromTheCollectionJson()
    {
        var root = new CollectionRoot
        {
            Info = new CollectionInfo
            {
                Name = "Instructed",
                DomainName = GameDomain.From("cyberpunk2077"),
                InstallInstructions = "Read this first.",
            },
        };
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Instructions {Guid.NewGuid()}");
        var revision = await CollectionSeed.AddRevision(Connection, loadout.InstallationInstance.Game.NexusModsGameId.Value, "Instructed");
        var group = await CollectionSeed.AddInstalledGroup(Connection, loadout.LoadoutId, "Instructed", revision);
        var parsed = 0;
        var source = NexusCollectionSource.Installed(Provider, loadout.LoadoutId, group, (_, _) =>
        {
            parsed++;
            return ValueTask.FromResult(root);
        });
        var seen = new List<CollectionInstructions>();
        using var _ = source.Instructions.Subscribe(seen.Add);

        seen.Should().ContainSingle().Which.HasAny.Should().BeFalse();
        parsed.Should().Be(0, "nothing is read before the page activates");

        using var activation = source.Activate();
        await WaitFor(() => seen.Any(instructions => instructions.HasAny));

        parsed.Should().Be(1);
        seen.Last().Collection.Should().NotBeNull();
    }

    [Fact]
    public async Task ThereAreNoInstructionsToParseWithoutTheArchive()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"No archive instructions {Guid.NewGuid()}");
        var revision = await CollectionSeed.AddRevision(Connection, loadout.InstallationInstance.Game.NexusModsGameId.Value, "No archive", withArchive: false);
        var source = NexusCollectionSource.Pending(Provider, loadout.LoadoutId, revision);

        using var activation = source.Activate();

        (await source.Instructions.FirstAsync()).HasAny.Should().BeFalse();
    }

    private static async Task Settled()
    {
        // The can-execute observable is observed on the UI thread; two round trips drain what it queued.
        await Dispatcher.UIThread.InvokeAsync(() => { });
        await Dispatcher.UIThread.InvokeAsync(() => { });
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition never became true");
            await Task.Delay(20);
        }
    }

    private sealed class ServicesWith(IServiceProvider inner, IJobMonitor monitor) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IJobMonitor) ? monitor : inner.GetService(serviceType);
    }

    /// <summary>A job monitor that only knows collection installs, which is all the Reinstall item asks it about.</summary>
    private sealed class FakeInstallJobs
    {
        private readonly SourceCache<IJob, JobId> _jobs = new(job => job.Id);
        public IJobMonitor Monitor { get; } = Substitute.For<IJobMonitor>();

        public FakeInstallJobs()
        {
            Monitor.GetObservableChangeSet<InstallCollectionJob>().Returns(_jobs.Connect());

            // The busy predicate also watches single-mod installs; none run in these tests.
            Monitor.GetObservableChangeSet<InstallCollectionDownloadJob>().Returns(new SourceCache<IJob, JobId>(job => job.Id).Connect());
        }

        public BehaviorSubject<JobStatus> Add(CollectionRevisionMetadata.ReadOnly revision, LoadoutId target, JobStatus initial)
        {
            var status = new BehaviorSubject<JobStatus>(initial);
            var job = Substitute.For<IJob>();
            job.Id.Returns(JobId.From(Guid.NewGuid()));
            job.ObservableStatus.Returns(status);
            job.Definition.Returns(new InstallCollectionJob
            {
                RevisionMetadata = revision,
                TargetLoadout = target,
                SourceCollection = default,
                Items = [],
                Group = Optional<NexusCollectionLoadoutGroup.ReadOnly>.None,
                ServiceProvider = null!,
                FileStore = null!,
                LibraryService = null!,
                LoadoutManager = null!,
                Connection = null!,
                NexusModsLibrary = null!,
                Logger = null!,
            });
            _jobs.AddOrUpdate(job);
            return status;
        }
    }
}
