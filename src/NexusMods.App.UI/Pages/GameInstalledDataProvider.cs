using System.Reactive.Linq;
using DynamicData;
using DynamicData.Aggregation;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.App.UI.Controls;
using NexusMods.App.UI.Pages.LoadoutPage;
using NexusMods.App.UI.Resources;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.DatomIterators;
using NexusMods.MnemonicDB.Abstractions.Query;
using NexusMods.Sdk.Loadouts;
using NexusMods.UI.Sdk;

namespace NexusMods.App.UI.Pages;

/// <summary>
/// Lists what the game installed itself under one "Installed by the game" row per loadout. The group has the
/// only switch; its items are locked and cannot be removed.
/// </summary>
[UsedImplicitly]
internal class GameInstalledDataProvider : ILoadoutDataProvider
{
    private readonly IConnection _connection;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<GameInstalledDataProvider> _logger;

    public GameInstalledDataProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _connection = serviceProvider.GetRequiredService<IConnection>();
        _logger = serviceProvider.GetService<ILogger<GameInstalledDataProvider>>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GameInstalledDataProvider>.Instance;
    }

    public IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> ObserveLoadoutItems(LoadoutFilter loadoutFilter)
    {
        // A collection's page only lists that collection's mods.
        if (loadoutFilter.CollectionGroupId.HasValue)
            return Observable.Empty<IChangeSet<CompositeItemModel<EntityId>, EntityId>>();

        return _connection
            .ObserveDatoms(GameInstalledGroup.InstalledFor, loadoutFilter.LoadoutId)
            .AsEntityIds()
            // A group with nothing left in it (every item claimed) has nothing to show.
            .FilterOnObservable((_, groupId) => ObserveItems(groupId).IsNotEmpty())
            .Transform(datom => ToGroupModel(GameInstalledGroup.Load(_connection.Db, datom.E)));
    }

    public IObservable<int> CountLoadoutItems(LoadoutFilter loadoutFilter)
    {
        if (loadoutFilter.CollectionGroupId.HasValue) return Observable.Return(0);

        return _connection
            .ObserveDatoms(GameInstalledItem.SourceId)
            .AsEntityIds()
            .FilterInStaticLoadout(_connection, loadoutFilter)
            .QueryWhenChanged(static query => query.Count)
            .Prepend(0);
    }

    private IObservable<IChangeSet<Datom, EntityId>> ObserveItems(EntityId groupId)
    {
        return _connection.ObserveDatoms(LoadoutItem.ParentId, groupId).AsEntityIds();
    }

    private CompositeItemModel<EntityId> ToGroupModel(GameInstalledGroup.ReadOnly group)
    {
        var loadoutItem = group.AsLoadoutItemGroup().AsLoadoutItem();

        var childrenObservable = ObserveItems(group.Id)
            .Transform(datom => ToItemModel(GameInstalledItem.Load(_connection.Db, datom.E)));

        var model = new CompositeItemModel<EntityId>(group.Id)
        {
            HasChildrenObservable = childrenObservable.IsNotEmpty(),
            ChildrenObservable = childrenObservable,
        };

        model.Add(SharedColumns.Name.NameComponentKey, new NameComponent(value: Language.GameInstalled_GroupName));
        model.Add(SharedColumns.Name.ImageComponentKey, new ImageComponent(value: ImagePipelines.ModPageThumbnailFallback));
        // The tooltip text is long, so it rides in the subtitle, which cuts it off and shows all of it on hover.
        model.Add(SharedColumns.Name.SubtitleComponentKey, new StringComponent(value: Language.GameInstalled_GroupToolTip));
        model.Add(SharedColumns.InstalledDate.ComponentKey, new DateComponent(value: loadoutItem.GetCreatedAt()));
        model.Add(LoadoutColumns.EnabledState.LoadoutItemIdsComponentKey, new LoadoutComponents.LoadoutItemIds(loadoutItem));

        LoadoutDataProviderHelper.AddEnabledStateToggle(_connection, model, loadoutItem);
        return model;
    }

    private CompositeItemModel<EntityId> ToItemModel(GameInstalledItem.ReadOnly item)
    {
        var loadoutItem = item.AsLoadoutItemGroup().AsLoadoutItem();
        var model = new CompositeItemModel<EntityId>(item.Id);

        model.Add(SharedColumns.Name.NameComponentKey, new NameComponent(value: loadoutItem.Name));
        model.Add(SharedColumns.Name.ImageComponentKey, new ImageComponent(value: ImagePipelines.ModPageThumbnailFallback));
        if (SubtitleFor(item) is { } subtitle)
            model.Add(SharedColumns.Name.SubtitleComponentKey, new StringComponent(value: subtitle));
        model.Add(SharedColumns.InstalledDate.ComponentKey, new DateComponent(value: loadoutItem.GetCreatedAt()));
        model.Add(LoadoutColumns.EnabledState.LoadoutItemIdsComponentKey, new LoadoutComponents.LoadoutItemIds(loadoutItem));
        model.Add(LoadoutColumns.EnabledState.ViewModFilesComponentKey, new SharedComponents.ViewModFilesAction(isEnabled: true));

        LoadoutDataProviderHelper.AddViewModPageActionComponent(model, isEnabled: false);
        LoadoutDataProviderHelper.AddLockedEnabledState(model, loadoutItem);

        if (item.CanBeClaimed)
        {
            var itemId = GameInstalledItemId.From(item.Id);
            model.Add(LoadoutColumns.EnabledState.TreatAsMineComponentKey, new LoadoutComponents.TreatAsMineAction(() => TreatAsMine(itemId)));
        }

        return model;
    }

    /// <summary>
    /// The version, then where the item came from. A Creation named by the game's own record gets no source text:
    /// the record leaves nothing to hedge.
    /// </summary>
    private static string? SubtitleFor(GameInstalledItem.ReadOnly item)
    {
        var parts = new List<string>(2);
        if (GameInstalledItem.Version.TryGetValue(item, out var version)) parts.Add(version);

        if (item.DepotShipped) parts.Add(Language.GameInstalled_DepotShipped);
        else if (item.Confidence == "FileNamePattern") parts.Add(Language.GameInstalled_ProbablyInGame);

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private async Task TreatAsMine(GameInstalledItemId itemId)
    {
        try
        {
            var outcome = await _serviceProvider.GetRequiredService<IGameInstalledContentService>().Claim(itemId);
            // Files the archive keeps a copy of can always be put back; the rest are worth a warning.
            if (outcome.NotBackedUp.Count > 0)
            {
                _serviceProvider.GetRequiredService<IWindowNotificationService>()
                    .ShowToast(string.Format(Language.GameInstalled_TreatAsMine_NotBackedUp,
                        string.Join(", ", outcome.NotBackedUp.Select(path => path.Path))));
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Treating game-installed item {Item} as the user's own failed", itemId);
            _serviceProvider.GetRequiredService<IWindowNotificationService>()
                .ShowToast(string.Format(Language.GameInstalled_TreatAsMine_Failed, e.Message));
        }
    }
}
