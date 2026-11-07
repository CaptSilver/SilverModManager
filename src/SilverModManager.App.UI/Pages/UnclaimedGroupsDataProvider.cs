using System.Reactive.Linq;
using DynamicData;
using DynamicData.Aggregation;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.DatomIterators;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Pages;

/// <summary>
/// Lists the groups that sit in a collection without a library file behind them: the mods adopted from another
/// manager and the "Found in the game folder" group a clean makes. Each is one row with the enable switch and the
/// view-files action, like any other mod. Without this the rows are in the database and My Mods shows nothing.
/// </summary>
[UsedImplicitly]
public class UnclaimedGroupsDataProvider : ILoadoutDataProvider
{
    private readonly IConnection _connection;

    public UnclaimedGroupsDataProvider(IServiceProvider serviceProvider)
    {
        _connection = serviceProvider.GetRequiredService<IConnection>();
    }

    private IObservable<IChangeSet<Datom, EntityId>> FilterLoadoutItems(LoadoutFilter loadoutFilter)
    {
        var adopted = _connection.ObserveDatoms(ForeignDeploymentGroup.PrimaryAttribute).AsEntityIds();
        var found = _connection.ObserveDatoms(FoundInGameFolderGroup.Marker).AsEntityIds();
        return new[] { adopted, found }
            .MergeChangeSets()
            .FilterInStaticLoadout(_connection, loadoutFilter);
    }

    public IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> ObserveLoadoutItems(LoadoutFilter loadoutFilter)
    {
        return FilterLoadoutItems(loadoutFilter).Transform(datom => ToItemModel(LoadoutItem.Load(_connection.Db, datom.E)));
    }

    public IObservable<int> CountLoadoutItems(LoadoutFilter loadoutFilter)
    {
        return FilterLoadoutItems(loadoutFilter).QueryWhenChanged(static query => query.Count).Prepend(0);
    }

    private CompositeItemModel<EntityId> ToItemModel(LoadoutItem.ReadOnly loadoutItem)
    {
        var model = LoadoutDataProviderHelper.ToChildItemModel(_connection, loadoutItem);
        SharedColumns.Source.Add(model, SourceOf(loadoutItem));
        model.Add(SharedColumns.Name.ImageComponentKey, new ImageComponent(value: ImagePipelines.ModPageThumbnailFallback));
        LoadoutDataProviderHelper.AddViewModPageActionComponent(model, isEnabled: false);
        return model;
    }

    /// <summary>The manager that deployed an adopted mod, or the game folder for what a clean set aside.</summary>
    private string SourceOf(LoadoutItem.ReadOnly loadoutItem)
    {
        var foreign = ForeignDeploymentGroup.Load(_connection.Db, loadoutItem.Id);
        return foreign.IsValid() ? foreign.Manager : ModSources.GameFolder;
    }
}
