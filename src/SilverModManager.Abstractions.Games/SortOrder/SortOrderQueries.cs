using DynamicData;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games;

public static class SortOrderQueries
{
    /// <summary>
    /// Returns an observable of changes to loadoutItemsWithTargetPath in the specified game.
    /// </summary>
    public static IObservable<IChangeSet<(EntityId ItemId, EntityId GroupId, EntityId CollectionId, EntityId LoadoutId), EntityId>> 
        TrackLoadoutItemChanges(IConnection connection, GameId gameId)
    {
        return connection.Query<(EntityId ItemId, EntityId GroupId, EntityId CollectionId, EntityId LoadoutId)>($"""
                                                 SELECT * FROM sortorder.TrackLoadoutItemChanges({connection}, {gameId.Value})
                                                 """
            )
            .ObserveInBackground(x => x.ItemId);
    }
}
