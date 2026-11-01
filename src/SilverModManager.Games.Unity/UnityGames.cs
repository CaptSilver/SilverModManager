using DynamicData.Kernel;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.Unity;

/// <summary>Every game that is a <see cref="UnityGameDefinition"/> row. Adding a game is adding a row.</summary>
public static class UnityGames
{
    public static readonly UnityGameDefinition Repo = new()
    {
        GameId = GameId.From("Unity.Repo"),
        DisplayName = "R.E.P.O.",
        NexusModsGameId = NexusModsGameId.From(7398),
        Executable = "REPO.exe",
        DataFolder = "REPO_Data",
        SteamAppId = 3241660,
        ThunderstoreCommunity = "repo",
        ThunderstoreLoaderPackage = "BepInEx-BepInExPack",
        ThunderstoreLoaderAliases = ["bbepis-BepInExPack", "xiaoxiao921-BepInExPack"],
        // Patches come in bursts a few days apart and then stop for months, so a shorter window fits.
        DateWindow = TimeSpan.FromDays(14),
    };

    public static readonly UnityGameDefinition Peak = new()
    {
        GameId = GameId.From("Unity.Peak"),
        DisplayName = "PEAK",
        NexusModsGameId = NexusModsGameId.From(7867),
        Executable = "PEAK.exe",
        DataFolder = "PEAK_Data",
        SteamAppId = 3527290,
        ThunderstoreCommunity = "peak",
        ThunderstoreLoaderPackage = "BepInEx-BepInExPack_PEAK",
        ThunderstoreLoaderAliases = ["BepInEx-BepInExPack", "bbepis-BepInExPack", "xiaoxiao921-BepInExPack"],
    };

    public static readonly IReadOnlyList<UnityGameDefinition> All = [Repo, Peak];
}
