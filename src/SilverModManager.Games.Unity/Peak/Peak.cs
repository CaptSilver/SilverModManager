using DynamicData.Kernel;
using JetBrains.Annotations;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Unity.Peak;

[UsedImplicitly]
public class Peak(IServiceProvider provider) : AUnityGame<Peak>(provider, Definition), IGameData<Peak>
{
    public static GameId GameId { get; } = GameId.From("Unity.Peak");
    public static string DisplayName => "PEAK";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Optional<Sdk.NexusModsApi.NexusModsGameId>.None;

    /// <remarks><c>new</c> on purpose: the base's instance <see cref="AUnityGame{TSelf}.Definition"/> is this same object.</remarks>
    public new static readonly UnityGameDefinition Definition = new(
        Executable: "PEAK.exe",
        DataFolder: "PEAK_Data",
        SteamAppId: 3527290,
        ThunderstoreCommunity: "peak",
        ThunderstoreLoaderPackage: "BepInEx-BepInExPack_PEAK",
        ThunderstoreLoaderAliases: ["BepInEx-BepInExPack", "bbepis-BepInExPack", "xiaoxiao921-BepInExPack"]);
}
