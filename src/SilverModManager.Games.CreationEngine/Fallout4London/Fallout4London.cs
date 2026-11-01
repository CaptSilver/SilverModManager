using SilverModManager.Abstractions.Games;
using DynamicData.Kernel;
using SilverModManager.Games.CreationEngine.Fallout4;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Fallout4London;

/// <summary>
/// Fallout 4 London, a total conversion that is installed into a Fallout 4 folder and starts Fallout4.exe. The
/// folder holds <c>Data/LondonWorldSpace.esm</c> only once London is installed, which is what tells it from
/// plain Fallout 4 on the shared Steam app id.
/// </summary>
public class Fallout4London(IServiceProvider provider) : AFallout4Game<Fallout4London>(provider), IGameData<Fallout4London>
{
    public static GameId GameId { get; } = GameId.From("CreationEngine.Fallout4London");
    public static string DisplayName => "Fallout 4 London";

    // The games.json entry "fallout4london" (id 6332) is London's own domain. Fluorine's plugin still reports
    // Fallout 4's id (nexusGameID) because it predates the domain.
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(6332);

    public override StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        // https://github.com/SulfurNitride/Fluorine-Manager libs/game_bethesda/src/games/fallout4london/gamefo4london.cpp
        // (steamAPPId is Fallout 4's). No GOG id: London's GOG install is not known to share Fallout 4's folder.
        SteamAppIds = [377160u],
    };

    // Fluorine lists londonworldspace.esm among the plugins London enables (enabledPlugins), and the file is not
    // in Fallout 4.
    public override IReadOnlyList<RelativePath> IdentifyingFiles => ["Data/LondonWorldSpace.esm"];

    protected override string[] GameAliases => [.. base.GameAliases, "Fallout 4 London", "Fallout4London"];
}
