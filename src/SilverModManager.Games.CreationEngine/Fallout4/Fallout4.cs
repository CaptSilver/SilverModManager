using SilverModManager.Abstractions.Games;
using DynamicData.Kernel;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Fallout4;

public class Fallout4(IServiceProvider provider) : AFallout4Game<Fallout4>(provider), IGameData<Fallout4>
{
    public static GameId GameId { get; } = GameId.From("CreationEngine.Fallout4");
    public static string DisplayName => "Fallout 4";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(1151);

    public override StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [377160u],
        GOGProductIds = [1998527297L],
    };
}
