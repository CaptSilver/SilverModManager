using NexusMods.Sdk.Games;

namespace NexusMods.Games.Declarative.Schema;

public static class GameDefinitionExtensions
{
    public static GameId GetGameId(this GameDefinition definition) => GameId.From(definition.Id);

    /// <summary>The store ids as the locators read them.</summary>
    public static StoreIdentifiers ToStoreIdentifiers(this GameDefinition definition) => new(definition.GetGameId())
    {
        SteamAppIds = [..definition.Stores.Steam],
        GOGProductIds = [..definition.Stores.Gog],
        EGSCatalogItemId = [..definition.Stores.Epic],
        AmazonProductIds = [..definition.Stores.Amazon],
    };
}
