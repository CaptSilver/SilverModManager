using DynamicData;
using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The "Allow save files" entry in a mod's context menu sets or clears <see cref="LoadoutItem.ProtectedPathsAllowed"/>
/// and shows whether the flag is currently set.
/// </summary>
public class AllowProtectedPathsActionTests(IServiceProvider provider) : AUiTest(provider)
{
    private async Task<LoadoutItemId> AddItem(bool flagged)
    {
        using var tx = Connection.BeginTransaction();
        var id = tx.TempId();
        _ = new LoadoutItem.New(tx, id)
        {
            Name = "Some mod",
            LoadoutId = LoadoutId.From(EntityId.From(0xDEAD)),
        };
        if (flagged) tx.Add(id, LoadoutItem.ProtectedPathsAllowed, Null.Instance);
        var result = await tx.Commit();
        return LoadoutItemId.From(result[id]);
    }

    private bool IsFlagged(LoadoutItemId id) => LoadoutItem.Load(Connection.Db, id).IsProtectedPathsAllowed;

    private async Task<SharedComponents.AllowProtectedPathsAction> ComponentFor(LoadoutItemId id)
    {
        return await OnUi(() =>
        {
            var model = new CompositeItemModel<EntityId>(id.Value);
            LoadoutDataProviderHelper.AddAllowProtectedPathsAction(Connection, model, LoadoutItem.Load(Connection.Db, id));
            return Task.FromResult(model.Get<SharedComponents.AllowProtectedPathsAction>(LoadoutColumns.EnabledState.AllowProtectedPathsComponentKey));
        });
    }

    [Fact]
    public async Task Toggle_SetsTheFlagOnAnUnflaggedItem()
    {
        var id = await AddItem(flagged: false);

        await LoadoutViewModel.HandleToggleProtectedPathsAllowed([id], Connection);

        IsFlagged(id).Should().BeTrue();
    }

    [Fact]
    public async Task Toggle_ClearsTheFlagOnAFlaggedItem()
    {
        var id = await AddItem(flagged: true);

        await LoadoutViewModel.HandleToggleProtectedPathsAllowed([id], Connection);

        IsFlagged(id).Should().BeFalse();
    }

    [Fact]
    public async Task Toggle_OnAMixedSelectionSetsTheFlagOnEveryItem()
    {
        var flagged = await AddItem(flagged: true);
        var unflagged = await AddItem(flagged: false);

        await LoadoutViewModel.HandleToggleProtectedPathsAllowed([flagged, unflagged], Connection);

        IsFlagged(flagged).Should().BeTrue();
        IsFlagged(unflagged).Should().BeTrue();
    }

    [Fact]
    public async Task Menu_ShowsTheCurrentState()
    {
        var off = await ComponentFor(await AddItem(flagged: false));
        var on = await ComponentFor(await AddItem(flagged: true));

        off.IsChecked.Value.Should().BeFalse();
        on.IsChecked.Value.Should().BeTrue();
    }

    [Fact]
    public async Task Menu_FollowsTheFlagWhenItChanges()
    {
        var id = await AddItem(flagged: false);
        var component = await ComponentFor(id);

        await LoadoutViewModel.HandleToggleProtectedPathsAllowed([id], Connection);
        await EventuallyOnUi(() => component.IsChecked.Value.Should().BeTrue());

        await LoadoutViewModel.HandleToggleProtectedPathsAllowed([id], Connection);
        await EventuallyOnUi(() => component.IsChecked.Value.Should().BeFalse());
    }

    [Fact]
    public async Task Menu_ForAGroupIsCheckedOnlyWhenEveryItemIsFlagged()
    {
        var first = await AddItem(flagged: true);
        var second = await AddItem(flagged: true);

        var component = await OnUi(() =>
        {
            var linked = new SourceCache<LoadoutItem.ReadOnly, EntityId>(static item => item.Id);
            linked.AddOrUpdate([LoadoutItem.Load(Connection.Db, first), LoadoutItem.Load(Connection.Db, second)]);
            var model = new CompositeItemModel<EntityId>(first.Value);
            LoadoutDataProviderHelper.AddAllowProtectedPathsAction(Connection, model, linked.Connect());
            return Task.FromResult(model.Get<SharedComponents.AllowProtectedPathsAction>(LoadoutColumns.EnabledState.AllowProtectedPathsComponentKey));
        });

        // The component starts unchecked until the items report in, so reach checked before testing the
        // mixed case; otherwise an unchecked assertion passes on the starting value alone.
        await EventuallyOnUi(() => component.IsChecked.Value.Should().BeTrue());

        await LoadoutViewModel.HandleToggleProtectedPathsAllowed([second], Connection);
        await EventuallyOnUi(() => component.IsChecked.Value.Should().BeFalse());

        await LoadoutViewModel.HandleToggleProtectedPathsAllowed([first, second], Connection);
        await EventuallyOnUi(() => component.IsChecked.Value.Should().BeTrue());
    }
}
