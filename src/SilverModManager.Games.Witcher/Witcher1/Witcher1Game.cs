using DynamicData.Kernel;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.Witcher.Witcher1;

public sealed class Witcher1Game(IServiceProvider provider) : AWitcherGame<Witcher1Game>(provider), IGameData<Witcher1Game>
{
    public static GameId GameId { get; } = GameId.From("Witcher.Witcher1");
    public static string DisplayName => "The Witcher";
    public static Optional<NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(150);

    protected override StoreIdentifiers CreateStoreIdentifiers() => new(GameId) { SteamAppIds = [20900] };
    protected override RelativePath PrimaryExe => "System/witcher.exe";
}
