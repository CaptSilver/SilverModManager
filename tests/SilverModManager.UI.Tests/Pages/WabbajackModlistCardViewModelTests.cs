using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages;
using SilverModManager.App.UI.Pages.LibraryPage.Collections;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Models;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>A Wabbajack modlist as a card on the Library's Collections tab, and removing it.</summary>
public class WabbajackModlistCardViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    private readonly IWindowManager _windows = Substitute.For<IWindowManager>();
    private readonly IWorkspaceController _workspace = Substitute.For<IWorkspaceController>();
    private readonly WorkspaceId _workspaceId = WorkspaceId.From(Guid.NewGuid());

    private async Task<(EntityId File, Loadout.ReadOnly Loadout)> AddList(string name, int archives = 0, string? galleryUrl = null)
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"{name} loadout");
        using var tx = Connection.BeginTransaction();
        var bytes = System.Text.Encoding.UTF8.GetBytes(name + Guid.NewGuid());
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = $"{name}.wabbajack",
            Hash = bytes.xxHash3(),
            Size = Size.From((ulong)bytes.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = $"{name}.wabbajack" },
        };
        _ = new LocalFile.New(tx, id) { LibraryFile = file, OriginalPath = $"/x/{name}.wabbajack" };
        var list = new WabbajackModlistLibraryFile.New(tx, id)
        {
            LibraryFile = file, Name = name, Author = "Someone", Version = "2.1", GameType = "NoSuchGame",
            ArchiveCount = archives, ArchiveSize = Size.From(5_000), InstallSize = Size.From(9_000), IsNsfw = false,
        };
        if (galleryUrl is not null) tx.Add(list.Id, WabbajackModlistLibraryFile.GalleryDownloadUrl, galleryUrl);
        var result = await tx.Commit();
        return (result.Remap(file).Id, loadout);
    }

    private WabbajackModlistCardViewModel Card(EntityId file, Loadout.ReadOnly loadout)
    {
        _windows.ActiveWorkspaceController.Returns(_workspace);
        return new WabbajackModlistCardViewModel(Provider, _windows, _workspaceId, WabbajackModlistLibraryFile.Load(Connection.Db, file), loadout.LoadoutId);
    }

    /// <summary>An archive row for <paramref name="name"/>, hashed from its name; it is in the library when <paramref name="file"/> is given.</summary>
    private async Task<(EntityId Row, FileIdentity Identity)> AddArchive(EntityId list, string name, EntityId? fetched = null)
    {
        var identity = FileIdentity.XxHash64(Hash.From((ulong)System.IO.Hashing.XxHash64.HashToUInt64(System.Text.Encoding.UTF8.GetBytes(name))));
        using var tx = Connection.BeginTransaction();
        var row = new WabbajackModlistArchive.New(tx) { ModlistId = list, Identity = identity, Size = Size.From(10), Name = name };
        if (fetched is { } file) tx.Add(row.Id, WabbajackModlistArchive.FetchedFiles, file);
        var result = await tx.Commit();
        return (result.Remap(row).Id, identity);
    }

    /// <summary>A library file whose xxHash64 is <paramref name="identity"/>.</summary>
    private async Task<EntityId> AddLibraryFile(string name, FileIdentity identity)
    {
        using var tx = Connection.BeginTransaction();
        var bytes = System.Text.Encoding.UTF8.GetBytes(name + Guid.NewGuid());
        identity.TryXxHash64(out var xx).Should().BeTrue();
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = name,
            Hash = bytes.xxHash3(),
            XxHash64 = xx,
            Size = Size.From(1_000),
            LibraryItem = new LibraryItem.New(tx, id) { Name = name },
        };
        _ = new LocalFile.New(tx, id) { LibraryFile = file, OriginalPath = $"/x/{name}" };
        var result = await tx.Commit();
        return result.Remap(file).Id;
    }

    private async Task Install(Loadout.ReadOnly loadout, EntityId libraryItem)
    {
        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { LoadoutId = loadout.LoadoutId, Name = "installed" },
        };
        _ = new LibraryLinkedLoadoutItem.New(tx, id) { LibraryItemId = libraryItem, LoadoutItemGroup = group };
        await tx.Commit();
    }

    [Fact]
    public async Task TheCardShowsTheListsFields()
    {
        var (file, loadout) = await AddList("Yggdrasil", archives: 1294, galleryUrl: "https://example.test/y.wabbajack");

        var card = Card(file, loadout);

        card.Name.Should().Be("Yggdrasil");
        card.Author.Should().Be("Someone");
        card.Version.Should().Be("2.1");
        card.Kind.Should().Be("Wabbajack modlist");
        card.Source.Should().Be(ModSources.Wabbajack);
        card.GameName.Should().Be("Unknown game");
        card.ArchiveSize.Should().Be(Size.From(5_000));
        card.InstallSize.Should().Be(Size.From(9_000));
        card.ArchivesNeeded.Should().Be(1294);
        card.ArchivesHad.Should().Be(0);
        card.State.Should().Be(WabbajackModlistCardState.NotInstalled);
    }

    [Fact]
    public async Task ArchivesHadCountsTheArchivesTheLibraryHoldsAndGrowsWhenOneArrives()
    {
        var (file, loadout) = await AddList("Counting", archives: 2);
        var (_, heldIdentity) = await AddArchive(file, "held.7z");
        await AddArchive(file, "missing.7z");
        await AddLibraryFile("held.7z", heldIdentity);
        var card = Card(file, loadout);
        card.ArchivesHad.Should().Be(1);
        using var activation = card.Activator.Activate();

        var (_, lateIdentity) = await AddArchive(file, "late.7z");
        await AddLibraryFile("late.7z", lateIdentity);

        await Eventually(() => card.ArchivesHad.Should().Be(2));
    }

    [Fact]
    public async Task TheStateFollowsTheListsGroupInTheCardsLoadout()
    {
        var (file, loadout) = await AddList("Stateful");
        var card = Card(file, loadout);
        using var activation = card.Activator.Activate();
        card.State.Should().Be(WabbajackModlistCardState.NotInstalled);

        EntityId group;
        using (var tx = Connection.BeginTransaction())
        {
            var g = new WabbajackModlistLoadoutGroup.New(tx, out var id)
            {
                ModlistId = file,
                CollectionGroup = new CollectionGroup.New(tx, id)
                {
                    IsReadOnly = false,
                    LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
                    {
                        IsGroup = true,
                        LoadoutItem = new LoadoutItem.New(tx, id) { Name = "Stateful", LoadoutId = loadout.LoadoutId, IsDisabled = true },
                    },
                },
            };
            group = (await tx.Commit()).Remap(g).Id;
        }
        await Eventually(() => card.State.Should().Be(WabbajackModlistCardState.Incomplete));

        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(group, WabbajackModlistLoadoutGroup.InstalledAt, DateTimeOffset.UtcNow);
            tx.Retract(group, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }
        await Eventually(() => card.State.Should().Be(WabbajackModlistCardState.Installed));

        using (var tx = Connection.BeginTransaction())
        {
            _ = new WabbajackSkippedFile.New(tx, out var skippedId)
            {
                GroupId = WabbajackModlistLoadoutGroupId.From(group), To = "x/y.esp", Reason = "missing", Kind = SkipKind.MissingArchive,
                LoadoutItem = new LoadoutItem.New(tx, skippedId) { Name = "skipped", LoadoutId = loadout.LoadoutId },
            };
            await tx.Commit();
        }
        await Eventually(() => card.State.Should().Be(WabbajackModlistCardState.Incomplete));
    }

    private PageData OpenedPage()
    {
        var call = _workspace.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IWorkspaceController.OpenPage));
        return ((DynamicData.Kernel.Optional<PageData>)call.GetArguments()[1]!).Value;
    }

    [Fact]
    public async Task OpenModlistOpensItsPageWithoutPickingALoadout()
    {
        var (file, loadout) = await AddList("Opened");
        var card = Card(file, loadout);

        await card.OpenCommand.Execute(System.Reactive.Unit.Default).FirstAsync();

        var context = OpenedPage().Context.Should().BeOfType<WabbajackModlistPageContext>().Subject;
        context.LibraryFileId.Value.Should().Be(file);
        context.TargetLoadout.Should().BeNull();
    }

    [Fact]
    public async Task InstallOpensTheListsPageOnTheLibrarysLoadout()
    {
        var (file, loadout) = await AddList("Installing");
        var card = Card(file, loadout);

        await card.InstallCommand.Execute(System.Reactive.Unit.Default).FirstAsync();

        var context = OpenedPage().Context.Should().BeOfType<WabbajackModlistPageContext>().Subject;
        context.TargetLoadout.Should().Be(loadout.LoadoutId);
    }

    [Fact]
    public async Task TheCardViewShowsTheFieldsAndItsOpenButtonOpensThePage()
    {
        var (file, loadout) = await AddList("Rendered", archives: 7, galleryUrl: "https://example.test/r.wabbajack");
        var (_, held) = await AddArchive(file, "rendered-held.7z");
        await AddLibraryFile("rendered-held.7z", held);
        var card = Card(file, loadout);

        var view = await OnUi(() =>
        {
            var view = new WabbajackModlistCardView { DataContext = card, ViewModel = card };
            ShowRendered(Track(new Avalonia.Controls.Window { Content = view, Width = 500, Height = 400 }));
            return Task.FromResult(view);
        });

        await EventuallyOnUi(() =>
        {
            view.TitleText.Text.Should().Be("Rendered");
            view.KindText.Text.Should().Be("Wabbajack modlist");
            view.SourceText.Text.Should().Be(ModSources.Wabbajack);
            view.ArchivesText.Text.Should().Contain("1 of 7");
        });

        var open = await OnUi(() => Task.FromResult(view.OpenButton));
        await Click(open);
        await Eventually(() => _workspace.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IWorkspaceController.OpenPage)).Should().BeTrue());
    }

    private WabbajackModlistRemover Remover => new(Provider);

    [Fact]
    public async Task TheRemovalPlanCountsOnlyTheArchivesThisListFetchedAndNothingElseUses()
    {
        var (list, loadout) = await AddList("Removing");
        var (other, _) = await AddList("Another list");
        var (_, ownedId) = await AddArchive(list, "owned.7z");
        var (_, sharedId) = await AddArchive(list, "shared.7z");
        var (_, installedId) = await AddArchive(list, "installed.7z");
        var (_, ownFileId) = await AddArchive(list, "users own.7z");
        var owned = await AddLibraryFile("owned.7z", ownedId);
        var shared = await AddLibraryFile("shared.7z", sharedId);
        var installed = await AddLibraryFile("installed.7z", installedId);
        await AddLibraryFile("users own.7z", ownFileId);
        await MarkFetched(list, ownedId, owned);
        await MarkFetched(list, sharedId, shared);
        await MarkFetched(list, installedId, installed);
        await AddArchive(other, "shared.7z");
        await Install(loadout, installed);

        var plan = Remover.Plan(LibraryFileId.From(list));

        plan.OwnedArchives.Select(id => id.Value).Should().BeEquivalentTo([owned]);
        plan.OwnedSize.Should().Be(Size.From(1_000));
        plan.Kept.Select(k => (k.Name, k.Reason)).Should().BeEquivalentTo([("shared.7z", "used by Another list"), ("installed.7z", "installed in a loadout")]);
    }

    private async Task MarkFetched(EntityId list, FileIdentity identity, EntityId file)
    {
        using var tx = Connection.BeginTransaction();
        foreach (var row in WabbajackModlistArchive.FindByModlist(Connection.Db, list).Where(r => r.Identity.Equals(identity)))
            tx.Add(row.Id, WabbajackModlistArchive.FetchedFiles, file);
        await tx.Commit();
    }

    [Fact]
    public async Task RemovingAListLeavesAnArchiveAnotherListNamesOutOfTheModsTab()
    {
        var (list, _) = await AddList("Removed first");
        var (other, _) = await AddList("Still installed");
        var (_, sharedId) = await AddArchive(list, "shared-m.7z");
        await AddArchive(other, "shared-m.7z");
        var shared = await AddLibraryFile("shared-m.7z", sharedId);
        await MarkFetched(list, sharedId, shared);
        var plan = Remover.Plan(LibraryFileId.From(list));

        await Remover.Remove(plan, deleteArchives: true, _workspace);

        LibraryFile.Load(Connection.Db, shared).IsValid().Should().BeTrue();
        WabbajackModlistArchive.FindByModlist(Connection.Db, other).Single().FetchedFiles.Select(f => f.Id).Should().Equal(shared);
        var shown = new List<bool>();
        using var subscription = LibraryDataProviderHelper.IsShownInMods(Connection, shared).Subscribe(value => { lock (shown) shown.Add(value); });
        await Eventually(() => { lock (shown) shown.Should().NotBeEmpty().And.EndWith(false); });
    }

    [Fact]
    public async Task AFetchedFileThatIsAMemberOfAnotherArchiveIsNeverOfferedForDeletion()
    {
        var (list, _) = await AddList("Linked a member");
        var (_, memberId) = await AddArchive(list, "member.7z");
        memberId.TryXxHash64(out var xx).Should().BeTrue();
        EntityId member;
        using (var tx = Connection.BeginTransaction())
        {
            var parentBytes = System.Text.Encoding.UTF8.GetBytes("parent.zip" + Guid.NewGuid());
            var parent = new LibraryFile.New(tx, out var parentId)
            {
                FileName = "parent.zip",
                Hash = parentBytes.xxHash3(),
                Size = Size.From(1_000),
                LibraryItem = new LibraryItem.New(tx, parentId) { Name = "parent.zip" },
            };
            _ = new LibraryArchive.New(tx, parentId) { IsArchive = true, LibraryFile = parent };
            var memberBytes = System.Text.Encoding.UTF8.GetBytes("member.7z" + Guid.NewGuid());
            var memberFile = new LibraryFile.New(tx, out var memberEntity)
            {
                FileName = "member.7z",
                Hash = memberBytes.xxHash3(),
                XxHash64 = xx,
                Size = Size.From(1_000),
                LibraryItem = new LibraryItem.New(tx, memberEntity) { Name = "member.7z" },
            };
            _ = new LibraryArchiveFileEntry.New(tx, memberEntity) { Path = RelativePath.FromUnsanitizedInput("inside/member.7z"), ParentId = parentId, LibraryFile = memberFile };
            var result = await tx.Commit();
            member = result.Remap(memberFile).Id;
        }
        await MarkFetched(list, memberId, member);

        var plan = Remover.Plan(LibraryFileId.From(list));

        plan.OwnedArchives.Should().BeEmpty();
    }

    [Fact]
    public async Task RemovingWithArchivesDeletesTheOwnedFilesTheRowsAndTheListButKeepsTheRest()
    {
        var (list, loadout) = await AddList("Removed with archives");
        var (_, ownedId) = await AddArchive(list, "owned-r.7z");
        var (_, installedId) = await AddArchive(list, "installed-r.7z");
        var owned = await AddLibraryFile("owned-r.7z", ownedId);
        var installed = await AddLibraryFile("installed-r.7z", installedId);
        await MarkFetched(list, ownedId, owned);
        await MarkFetched(list, installedId, installed);
        await Install(loadout, installed);
        var plan = Remover.Plan(LibraryFileId.From(list));

        await Remover.Remove(plan, deleteArchives: true, _workspace);

        LibraryFile.Load(Connection.Db, owned).IsValid().Should().BeFalse();
        LibraryFile.Load(Connection.Db, installed).IsValid().Should().BeTrue();
        WabbajackModlistArchive.FindByModlist(Connection.Db, list).Should().BeEmpty();
        WabbajackModlistLibraryFile.Load(Connection.Db, list).IsValid().Should().BeFalse();
    }

    [Fact]
    public async Task RemovingWithoutArchivesLeavesEveryArchiveInTheLibrary()
    {
        var (list, _) = await AddList("Removed keeping archives");
        var (_, ownedId) = await AddArchive(list, "owned-k.7z");
        var owned = await AddLibraryFile("owned-k.7z", ownedId);
        await MarkFetched(list, ownedId, owned);
        var plan = Remover.Plan(LibraryFileId.From(list));

        await Remover.Remove(plan, deleteArchives: false, _workspace);

        LibraryFile.Load(Connection.Db, owned).IsValid().Should().BeTrue();
        WabbajackModlistLibraryFile.Load(Connection.Db, list).IsValid().Should().BeFalse();
    }

    [Fact]
    public async Task AnInstalledListNamesItsLoadoutsInTheRemovalPlanAndItsGroupGoesFirst()
    {
        var (list, loadout) = await AddList("Installed then removed");
        EntityId group;
        using (var tx = Connection.BeginTransaction())
        {
            var g = new WabbajackModlistLoadoutGroup.New(tx, out var id)
            {
                ModlistId = list,
                CollectionGroup = new CollectionGroup.New(tx, id)
                {
                    IsReadOnly = false,
                    LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
                    {
                        IsGroup = true,
                        LoadoutItem = new LoadoutItem.New(tx, id) { Name = "Installed then removed", LoadoutId = loadout.LoadoutId },
                    },
                },
            };
            group = (await tx.Commit()).Remap(g).Id;
        }
        var plan = Remover.Plan(LibraryFileId.From(list));
        plan.LoadoutNames.Should().ContainSingle().Which.Should().Be(loadout.Name);
        WabbajackModlistCardViewModel.RemovalText(plan).Should().Contain(loadout.Name);

        await Remover.Remove(plan, deleteArchives: false, _workspace);

        WabbajackModlistLoadoutGroup.Load(Connection.Db, group).IsValid().Should().BeFalse();
        WabbajackModlistLibraryFile.Load(Connection.Db, list).IsValid().Should().BeFalse();
    }
}

/// <summary>The Library's Collections tab lists modlists next to Nexus collections.</summary>
public class LibraryCollectionsTabTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task AModlistForTheLoadoutsGameIsACardOnTheCollectionsTab()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Collections tab loadout");
        using (var tx = Connection.BeginTransaction())
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("tab list" + Guid.NewGuid());
            var file = new LibraryFile.New(tx, out var id)
            {
                FileName = "tab.wabbajack",
                Hash = bytes.xxHash3(),
                Size = Size.From((ulong)bytes.Length),
                LibraryItem = new LibraryItem.New(tx, id) { Name = "tab.wabbajack" },
            };
            _ = new WabbajackModlistLibraryFile.New(tx, id)
            {
                LibraryFile = file, Name = "On the tab", Author = "Someone", Version = "1", GameType = "NoSuchGame",
                ArchiveCount = 0, ArchiveSize = Size.Zero, InstallSize = Size.Zero, IsNsfw = false,
            };
            await tx.Commit();
        }

        var windows = Substitute.For<IWindowManager>();
        var vm = new SilverModManager.App.UI.Pages.LibraryPage.LibraryViewModel(
            windows, Provider, Provider.GetRequiredService<SilverModManager.Abstractions.NexusWebApi.IGameDomainToGameIdMappingCache>(), loadout.LoadoutId);
        using var activation = vm.Activator.Activate();

        await EventuallyOnUi(() => vm.Collections.OfType<IWabbajackModlistCardViewModel>().Select(card => card.Name).Should().Contain("On the tab"));
    }
}
