using System.Reactive.Linq;
using DynamicData;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.Resources;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using NSubstitute;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// My Mods lists what the game installed under one "Installed by the game" row. The group switches as a whole,
/// its items cannot be switched or removed, and an item that can be claimed offers "Treat as my file".
/// </summary>
public class GameInstalledDataProviderTests(IServiceProvider provider) : AUiTest(provider)
{
    private readonly IGameInstalledContentService _service = Substitute.For<IGameInstalledContentService>();
    private readonly IWindowNotificationService _notifications = Substitute.For<IWindowNotificationService>();

    private GameInstalledDataProvider CreateProvider()
    {
        var services = new ServiceCollection()
            .AddSingleton(Connection)
            .AddSingleton(_service)
            .AddSingleton(_notifications);
        return new GameInstalledDataProvider(services.BuildServiceProvider());
    }

    private static LoadoutFilter FilterFor(Loadout.ReadOnly loadout) => new()
    {
        LoadoutId = loadout.LoadoutId,
        CollectionGroupId = Optional<LoadoutItemGroupId>.None,
    };

    private static LoadoutFilter CollectionFilterFor(Loadout.ReadOnly loadout) => new()
    {
        LoadoutId = loadout.LoadoutId,
        CollectionGroupId = LoadoutItemGroupId.From(EntityId.From(0xC0FFEE)),
    };

    private async Task<(Loadout.ReadOnly Loadout, EntityId Group)> LoadoutWithGroup()
    {
        var loadout = await SeedLoadoutRow(GameStore.Unknown, "game-installed-" + Guid.NewGuid().ToString("N"));
        using var tx = Connection.BeginTransaction();
        _ = new GameInstalledGroup.New(tx, out var groupId)
        {
            InstalledForId = loadout,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, groupId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, groupId)
                {
                    Name = "stored name is not shown",
                    LoadoutId = loadout.Id,
                },
            },
        };
        var result = await tx.Commit();
        return (loadout, result[groupId]);
    }

    private async Task<EntityId> AddItem(
        Loadout.ReadOnly loadout, EntityId group, string title, string path,
        bool canBeClaimed = false, bool depotShipped = false, string confidence = "GameRecord", string? version = null)
    {
        using var tx = Connection.BeginTransaction();
        var item = new GameInstalledItem.New(tx, out var itemId)
        {
            Source = "SkyrimCreation",
            SourceId = path,
            Confidence = confidence,
            CanBeClaimed = canBeClaimed,
            DepotShipped = depotShipped,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, itemId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, itemId)
                {
                    Name = title,
                    LoadoutId = loadout.Id,
                    ParentId = group,
                },
            },
        };
        if (version is not null) item.Version = version;
        _ = new GameInstalledFile.New(tx, out var fileId)
        {
            Path = new GamePath(LocationId.Game, path).ToGamePathParentTuple(loadout.Id),
            Hash = Hash.From(0xAB),
            Size = Size.From(10),
            LoadoutItem = new LoadoutItem.New(tx, fileId)
            {
                Name = path,
                LoadoutId = loadout.Id,
                ParentId = itemId,
            },
        };
        var result = await tx.Commit();
        return result[itemId];
    }

    private async Task<IObservableCache<CompositeItemModel<EntityId>, EntityId>> Observe(LoadoutFilter filter) =>
        await OnUi(() => Task.FromResult(CreateProvider().ObserveLoadoutItems(filter).AsObservableCache()));

    private async Task<CompositeItemModel<EntityId>> GroupRow(LoadoutFilter filter)
    {
        var cache = await Observe(filter);
        await Eventually(() => cache.Count.Should().Be(1));
        return cache.Items.Single();
    }

    private async Task<IObservableCache<CompositeItemModel<EntityId>, EntityId>> Children(CompositeItemModel<EntityId> row)
    {
        var children = await OnUi(() => Task.FromResult(row.ChildrenObservable.AsObservableCache()));
        return children;
    }

    private static string NameOf(CompositeItemModel<EntityId> row) =>
        row.Get<NameComponent>(SharedColumns.Name.NameComponentKey).Value.Value;

    private static string? SubtitleOf(CompositeItemModel<EntityId> row) =>
        row.TryGet<StringComponent>(SharedColumns.Name.SubtitleComponentKey, out var subtitle) ? subtitle.Value.Value : null;

    [Fact]
    public async Task TheGroupAndItsItems_AreFromTheGame()
    {
        var (loadout, group) = await LoadoutWithGroup();
        await AddItem(loadout, group, "Some content", "Data/content.esm");

        var row = await GroupRow(FilterFor(loadout));
        var children = await Children(row);
        await Eventually(() => children.Count.Should().Be(1));

        row.Get<StringComponent>(SharedColumns.Source.ComponentKey).Value.Value.Should().Be("Game");
        children.Items.Single().Get<StringComponent>(SharedColumns.Source.ComponentKey).Value.Value.Should().Be("Game");
    }

    [Fact]
    public async Task TheGroupIsOneRowNamedByTheAppWithItsItemsUnderneath()
    {
        var (loadout, group) = await LoadoutWithGroup();
        await AddItem(loadout, group, "Anniversary Edition", "ccbgssse001-fish.esm");
        await AddItem(loadout, group, "Saints and Seducers", "ccqdrsse001-survivalmode.esl");

        var row = await GroupRow(FilterFor(loadout));
        row.Key.Should().Be(group);
        NameOf(row).Should().Be(Language.GameInstalled_GroupName);

        var children = await Children(row);
        await Eventually(() => children.Count.Should().Be(2));
        children.Items.Select(NameOf).Should().BeEquivalentTo("Anniversary Edition", "Saints and Seducers");
    }

    [Fact]
    public async Task AGroupWithoutItemsIsNotListed()
    {
        var (loadout, _) = await LoadoutWithGroup();

        var cache = await Observe(FilterFor(loadout));
        await Task.Delay(300);

        cache.Count.Should().Be(0);
    }

    [Fact]
    public async Task OtherLoadoutsGroupsAreNotListed()
    {
        var (mine, myGroup) = await LoadoutWithGroup();
        var (other, otherGroup) = await LoadoutWithGroup();
        await AddItem(mine, myGroup, "Mine", "mine.esm");
        await AddItem(other, otherGroup, "Other", "other.esm");

        var row = await GroupRow(FilterFor(mine));

        row.Key.Should().Be(myGroup);
    }

    [Fact]
    public async Task NothingIsListedAndNothingCountedUnderACollectionFilter()
    {
        var (loadout, group) = await LoadoutWithGroup();
        await AddItem(loadout, group, "Anniversary Edition", "ccbgssse001-fish.esm");

        var cache = await Observe(CollectionFilterFor(loadout));
        await Task.Delay(300);
        cache.Count.Should().Be(0);

        var count = await OnUi(() => Task.FromResult(CreateProvider().CountLoadoutItems(CollectionFilterFor(loadout))));
        (await count.FirstAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TheCountIsTheNumberOfItems()
    {
        var (loadout, group) = await LoadoutWithGroup();
        await AddItem(loadout, group, "One", "one.esm");
        await AddItem(loadout, group, "Two", "two.esm");
        var last = 0;
        using var subscription = CreateProvider().CountLoadoutItems(FilterFor(loadout)).Subscribe(count => last = count);

        await Eventually(() => last.Should().Be(2));

        await AddItem(loadout, group, "Three", "three.esm");
        await Eventually(() => last.Should().Be(3));
    }

    [Fact]
    public async Task TheGroupHasTheTooltipAndASwitchButNoUninstall()
    {
        var (loadout, group) = await LoadoutWithGroup();
        await AddItem(loadout, group, "Anniversary Edition", "ccbgssse001-fish.esm");

        var row = await GroupRow(FilterFor(loadout));

        SubtitleOf(row).Should().Be(Language.GameInstalled_GroupToolTip);
        row.TryGet<LoadoutComponents.EnabledStateToggle>(LoadoutColumns.EnabledState.EnabledStateToggleComponentKey, out _).Should().BeTrue();
        row.TryGet<LoadoutComponents.LockedEnabledState>(LoadoutColumns.EnabledState.LockedEnabledStateComponentKey, out _).Should().BeFalse();
        row.TryGet<SharedComponents.UninstallItemAction>(LoadoutColumns.EnabledState.UninstallItemComponentKey, out _).Should().BeFalse();
    }

    [Fact]
    public async Task TheGroupsSwitchFollowsItsDisabledMarker()
    {
        var (loadout, group) = await LoadoutWithGroup();
        await AddItem(loadout, group, "Anniversary Edition", "ccbgssse001-fish.esm");
        var row = await GroupRow(FilterFor(loadout));
        var toggle = row.Get<LoadoutComponents.EnabledStateToggle>(LoadoutColumns.EnabledState.EnabledStateToggleComponentKey);
        using var active = toggle.Activate();
        toggle.Value.Value.Value.Should().BeTrue();

        await LoadoutViewModel.HandleToggleItemEnabledState([LoadoutItemId.From(group)], Connection);

        LoadoutItem.Load(Connection.Db, group).IsDisabled.Should().BeTrue();
        await EventuallyOnUi(() => toggle.Value.Value.Value.Should().BeFalse());
    }

    [Fact]
    public async Task ItemsAreLockedAndOfferNeitherAToggleNorUninstall()
    {
        var (loadout, group) = await LoadoutWithGroup();
        await AddItem(loadout, group, "Anniversary Edition", "ccbgssse001-fish.esm", version: "1.2.3");
        var children = await Children(await GroupRow(FilterFor(loadout)));
        await Eventually(() => children.Count.Should().Be(1));

        var item = children.Items.Single();

        item.TryGet<LoadoutComponents.LockedEnabledState>(LoadoutColumns.EnabledState.LockedEnabledStateComponentKey, out _).Should().BeTrue();
        item.TryGet<LoadoutComponents.EnabledStateToggle>(LoadoutColumns.EnabledState.EnabledStateToggleComponentKey, out _).Should().BeFalse();
        item.TryGet<SharedComponents.UninstallItemAction>(LoadoutColumns.EnabledState.UninstallItemComponentKey, out _).Should().BeFalse();
        item.TryGet<SharedComponents.ViewModFilesAction>(LoadoutColumns.EnabledState.ViewModFilesComponentKey, out _).Should().BeTrue();
        item.TryGet<DateComponent>(SharedColumns.InstalledDate.ComponentKey, out _).Should().BeTrue();
        SubtitleOf(item).Should().Be("1.2.3", "the loadout page has no version column, so the version rides in the subtitle");
    }

    [Fact]
    public async Task ASubtitleSaysWhereTheItemCameFrom()
    {
        var (loadout, group) = await LoadoutWithGroup();
        await AddItem(loadout, group, "Depot", "depot.esm", depotShipped: true);
        await AddItem(loadout, group, "Guessed", "guessed.pak", confidence: "FileNamePattern");
        await AddItem(loadout, group, "Recorded", "recorded.esm");
        var children = await Children(await GroupRow(FilterFor(loadout)));
        await Eventually(() => children.Count.Should().Be(3));

        SubtitleOf(children.Items.Single(row => NameOf(row) == "Depot")).Should().Be(Language.GameInstalled_DepotShipped);
        SubtitleOf(children.Items.Single(row => NameOf(row) == "Guessed")).Should().Be(Language.GameInstalled_ProbablyInGame);
        SubtitleOf(children.Items.Single(row => NameOf(row) == "Recorded")).Should().BeNull("the game's own record names it, so there is nothing to hedge");
    }

    [Fact]
    public async Task TheVersionComesBeforeTheSource()
    {
        var (loadout, group) = await LoadoutWithGroup();
        await AddItem(loadout, group, "Depot", "depot.esm", depotShipped: true, version: "2.0");
        var children = await Children(await GroupRow(FilterFor(loadout)));
        await Eventually(() => children.Count.Should().Be(1));

        SubtitleOf(children.Items.Single()).Should().Be("2.0 \u00b7 " + Language.GameInstalled_DepotShipped);
    }

    [Fact]
    public async Task TreatAsMineIsOfferedOnlyForAnItemThatCanBeClaimed()
    {
        var (loadout, group) = await LoadoutWithGroup();
        await AddItem(loadout, group, "Claimable", "claimable.pak", canBeClaimed: true);
        await AddItem(loadout, group, "Fixed", "fixed.esm", canBeClaimed: false);
        var children = await Children(await GroupRow(FilterFor(loadout)));
        await Eventually(() => children.Count.Should().Be(2));

        children.Items.Single(row => NameOf(row) == "Claimable")
            .TryGet<LoadoutComponents.TreatAsMineAction>(LoadoutColumns.EnabledState.TreatAsMineComponentKey, out _).Should().BeTrue();
        children.Items.Single(row => NameOf(row) == "Fixed")
            .TryGet<LoadoutComponents.TreatAsMineAction>(LoadoutColumns.EnabledState.TreatAsMineComponentKey, out _).Should().BeFalse();
    }

    [Fact]
    public async Task TreatAsMineClaimsTheItem()
    {
        var (loadout, group) = await LoadoutWithGroup();
        var itemId = await AddItem(loadout, group, "Claimable", "claimable.pak", canBeClaimed: true);
        _service.Claim(Arg.Any<GameInstalledItemId>(), Arg.Any<CancellationToken>()).Returns(new ClaimOutcome([], []));
        var children = await Children(await GroupRow(FilterFor(loadout)));
        await Eventually(() => children.Count.Should().Be(1));
        var action = children.Items.Single().Get<LoadoutComponents.TreatAsMineAction>(LoadoutColumns.EnabledState.TreatAsMineComponentKey);

        await OnUi(() => action.Command.Execute(R3.Unit.Default));

        await Eventually(() => _service.Received(1).Claim(GameInstalledItemId.From(itemId), Arg.Any<CancellationToken>()));
    }

    [Fact]
    public async Task AFailedClaimIsShownToTheUser()
    {
        var (loadout, group) = await LoadoutWithGroup();
        await AddItem(loadout, group, "Claimable", "claimable.pak", canBeClaimed: true);
        _service.Claim(Arg.Any<GameInstalledItemId>(), Arg.Any<CancellationToken>())
            .Returns<Task<ClaimOutcome>>(_ => throw new InvalidOperationException("the file is open in another program"));
        var children = await Children(await GroupRow(FilterFor(loadout)));
        await Eventually(() => children.Count.Should().Be(1));
        var action = children.Items.Single().Get<LoadoutComponents.TreatAsMineAction>(LoadoutColumns.EnabledState.TreatAsMineComponentKey);

        await OnUi(() => action.Command.Execute(R3.Unit.Default));

        await Eventually(() => _notifications.Received(1).ShowToast(
            Arg.Is<string>(message => message.Contains("the file is open in another program")),
            Arg.Any<ToastNotificationVariant>(), Arg.Any<TimeSpan?>(),
            Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>()));
    }

    [Fact]
    public async Task ClaimedPathsMovedWithoutABackupAreShownToTheUser()
    {
        var (loadout, group) = await LoadoutWithGroup();
        await AddItem(loadout, group, "Claimable", "claimable.pak", canBeClaimed: true);
        var pak = new GamePath(LocationId.Game, "Data/claimable.pak");
        var esp = new GamePath(LocationId.Game, "Data/claimable.esp");
        var ini = new GamePath(LocationId.Game, "Data/claimable.ini");
        _service.Claim(Arg.Any<GameInstalledItemId>(), Arg.Any<CancellationToken>()).Returns(new ClaimOutcome([pak, esp, ini], [pak, esp]));
        var children = await Children(await GroupRow(FilterFor(loadout)));
        await Eventually(() => children.Count.Should().Be(1));
        var action = children.Items.Single().Get<LoadoutComponents.TreatAsMineAction>(LoadoutColumns.EnabledState.TreatAsMineComponentKey);

        await OnUi(() => action.Command.Execute(R3.Unit.Default));

        await Eventually(() => _notifications.Received(1).ShowToast(
            Arg.Is<string>(message => message.Contains("claimable.pak") && message.Contains("claimable.esp") && !message.Contains("claimable.ini")),
            Arg.Any<ToastNotificationVariant>(), Arg.Any<TimeSpan?>(),
            Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>()));
    }

    [Fact]
    public async Task AClaimThatBackedUpEveryFileShowsNoNotice()
    {
        var (loadout, group) = await LoadoutWithGroup();
        await AddItem(loadout, group, "Claimable", "claimable.pak", canBeClaimed: true);
        _service.Claim(Arg.Any<GameInstalledItemId>(), Arg.Any<CancellationToken>())
            .Returns(new ClaimOutcome([new GamePath(LocationId.Game, "Data/claimable.pak")], []));
        var children = await Children(await GroupRow(FilterFor(loadout)));
        await Eventually(() => children.Count.Should().Be(1));
        var action = children.Items.Single().Get<LoadoutComponents.TreatAsMineAction>(LoadoutColumns.EnabledState.TreatAsMineComponentKey);

        await OnUi(() => action.Command.Execute(R3.Unit.Default));

        await Eventually(() => _service.Received(1).Claim(Arg.Any<GameInstalledItemId>(), Arg.Any<CancellationToken>()));
        _notifications.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task TheGroupRowCannotBeUninstalledButTheSwitchWorks_WhileItemsDoNeither()
    {
        var (loadout, group) = await LoadoutWithGroup();
        var itemId = await AddItem(loadout, group, "Anniversary Edition", "ccbgssse001-fish.esm");
        var windows = Substitute.For<SilverModManager.App.UI.Windows.IWindowManager>();

        await LoadoutViewModel.HandleUninstallItem([LoadoutItemId.From(group), LoadoutItemId.From(itemId)], windows, Connection);
        await LoadoutViewModel.HandleToggleItemEnabledState([LoadoutItemId.From(itemId)], Connection);

        GameInstalledGroup.Load(Connection.Db, group).IsValid().Should().BeTrue();
        GameInstalledItem.Load(Connection.Db, itemId).IsValid().Should().BeTrue();
        LoadoutItem.Load(Connection.Db, itemId).IsDisabled.Should().BeFalse("only the group has a switch");
        windows.ReceivedCalls().Should().BeEmpty("there was nothing removable to confirm");
    }
}
