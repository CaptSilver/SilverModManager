using DynamicData.Kernel;
using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The groups that hold files another manager deployed, and the files a clean set aside, have no library file behind
/// them, so no other provider lists them. My Mods shows them through this one, with a switch and a files action.
/// </summary>
public class UnclaimedGroupsDataProviderTests(IServiceProvider provider) : AUiTest(provider)
{
    private async Task<(Loadout.ReadOnly Loadout, LoadoutItemGroupId MyMods)> LoadoutWithAdoptedAndFoundGroups()
    {
        var loadout = await SeedLoadoutRow(GameStore.Unknown, "unclaimed-" + Guid.NewGuid().ToString("N"));
        using var tx = Connection.BeginTransaction();
        var collection = DefaultCollection.Create(tx, loadout.LoadoutId);
        var myMods = LoadoutItemGroupId.From(collection.Id);

        _ = new ForeignDeploymentGroup.New(tx, out var adoptedId)
        {
            Manager = "Vortex",
            Source = "combat-123-1-0-1",
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, adoptedId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, adoptedId) { Name = "Better Combat", LoadoutId = loadout.Id, ParentId = myMods },
            },
        };
        AddFile(tx, loadout, LoadoutItemGroupId.From(adoptedId), "Mods/Combat/a.pak");

        _ = new FoundInGameFolderGroup.New(tx, out var foundId)
        {
            IsMarker = true,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, foundId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, foundId) { Name = DefaultCollection.FoundInGameFolderName, LoadoutId = loadout.Id, ParentId = myMods },
            },
        };
        tx.Add(foundId, LoadoutItem.Disabled, Null.Instance);
        AddFile(tx, loadout, LoadoutItemGroupId.From(foundId), "crash.dmp");

        // A plain group in the same collection that nothing claims is not this provider's to list.
        _ = new LoadoutItemGroup.New(tx, out var plainId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, plainId) { Name = "Plain group", LoadoutId = loadout.Id, ParentId = myMods },
        };

        var result = await tx.Commit();
        return (Loadout.Load(Connection.Db, loadout.LoadoutId), LoadoutItemGroupId.From(result[collection.Id]));
    }

    private static void AddFile(ITransaction tx, Loadout.ReadOnly loadout, LoadoutItemGroupId parent, string path)
    {
        var gamePath = new GamePath(LocationId.Game, path);
        _ = new LoadoutFile.New(tx, out var fileId)
        {
            LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, fileId)
            {
                LoadoutItem = new LoadoutItem.New(tx, fileId) { LoadoutId = loadout.Id, ParentId = parent, Name = gamePath.Path.FileName },
                TargetPath = gamePath.ToGamePathParentTuple(loadout.LoadoutId.Value),
            },
            Hash = Hash.From(0xC0FFEE),
            Size = Size.From(10),
        };
    }

    private sealed class Adapter(IServiceProvider provider, LoadoutFilter filter) : LoadoutTreeDataGridAdapter(provider, filter, new NoFiles())
    {
        public string[] Names() => VisibleRoots.Select(row => row.Get<NameComponent>(SharedColumns.Name.NameComponentKey).Value.Value).Order().ToArray();
    }

    private sealed class NoFiles : IModFileSearch
    {
        public IReadOnlySet<EntityId> ItemsWithFileMatching(string text, CancellationToken cancellationToken) => new HashSet<EntityId>();
    }

    [Fact]
    public async Task MyModsPageListsTheAdoptedModAndTheFoundFilesGroup()
    {
        var (loadout, myMods) = await LoadoutWithAdoptedAndFoundGroups();
        var filter = new LoadoutFilter { LoadoutId = loadout.LoadoutId, CollectionGroupId = myMods };

        var (adapter, activation) = await OnUi(() =>
        {
            var adapter = new Adapter(Provider, filter);
            return Task.FromResult((adapter, adapter.Activate()));
        });
        using var _ = activation;

        await EventuallyOnUi(() => adapter.Names().Should().Equal("Better Combat", DefaultCollection.FoundInGameFolderName));
    }

    [Fact]
    public async Task EachRowHasTheEnableSwitchAndTheViewFilesAction()
    {
        var (loadout, myMods) = await LoadoutWithAdoptedAndFoundGroups();
        var provider = new UnclaimedGroupsDataProvider(Provider);
        var filter = new LoadoutFilter { LoadoutId = loadout.LoadoutId, CollectionGroupId = myMods };
        var cache = await OnUi(() => Task.FromResult(DynamicData.ObservableCacheEx.AsObservableCache(provider.ObserveLoadoutItems(filter))));

        await Eventually(() => cache.Count.Should().Be(2));

        foreach (var row in cache.Items)
        {
            row.GetOptional<LoadoutComponents.EnabledStateToggle>(LoadoutColumns.EnabledState.EnabledStateToggleComponentKey).HasValue.Should().BeTrue();
            row.GetOptional<SharedComponents.ViewModFilesAction>(LoadoutColumns.EnabledState.ViewModFilesComponentKey).HasValue.Should().BeTrue();
        }
    }

    [Fact]
    public async Task CountsBothGroups()
    {
        var (loadout, myMods) = await LoadoutWithAdoptedAndFoundGroups();
        var provider = new UnclaimedGroupsDataProvider(Provider);

        var latest = 0;
        using var subscription = provider.CountLoadoutItems(new LoadoutFilter { LoadoutId = loadout.LoadoutId, CollectionGroupId = myMods })
            .Subscribe(n => latest = n);

        await Eventually(() => latest.Should().Be(2));
    }
}
