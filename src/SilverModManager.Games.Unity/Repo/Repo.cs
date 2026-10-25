using DynamicData.Kernel;
using JetBrains.Annotations;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Unity.Repo;

[UsedImplicitly]
public class Repo(IServiceProvider provider) : AUnityGame<Repo>(provider, Definition), IGameData<Repo>
{
    public static GameId GameId { get; } = GameId.From("Unity.Repo");
    public static string DisplayName => "R.E.P.O.";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(7398);

    // Patches come in bursts a few days apart and then stop for months, so a shorter window fits.
    public override TimeSpan? DateWindow => TimeSpan.FromDays(14);

    /// <remarks><c>new</c> on purpose: the base's instance <see cref="AUnityGame{TSelf}.Definition"/> is this same object.</remarks>
    public new static readonly UnityGameDefinition Definition = new(
        Executable: "REPO.exe",
        DataFolder: "REPO_Data",
        SteamAppId: 3241660,
        ThunderstoreCommunity: "repo",
        ThunderstoreLoaderPackage: "BepInEx-BepInExPack",
        ThunderstoreLoaderAliases: ["bbepis-BepInExPack", "xiaoxiao921-BepInExPack"]);
}
