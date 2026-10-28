using DynamicData.Kernel;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.Witcher.Witcher2;

public sealed class Witcher2Game(IServiceProvider provider) : AWitcherGame<Witcher2Game>(provider), IGameData<Witcher2Game>
{
    public static GameId GameId { get; } = GameId.From("Witcher.Witcher2");
    public static string DisplayName => "The Witcher 2: Assassins of Kings";
    public static Optional<NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(153);

    protected override StoreIdentifiers CreateStoreIdentifiers() => new(GameId) { SteamAppIds = [20920] };
    protected override RelativePath PrimaryExe => "bin/witcher2.exe";
}
