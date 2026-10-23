namespace NexusMods.Games.Unity;

/// <summary>What sets one Unity game apart from the next: its files, its store id and where its mods come from.</summary>
/// <param name="Executable">The game's executable, relative to the game folder.</param>
/// <param name="DataFolder">The <c>&lt;Name&gt;_Data</c> folder next to the executable.</param>
/// <param name="SteamAppId">The game's Steam app id.</param>
/// <param name="ThunderstoreCommunity">The Thunderstore community slug, e.g. <c>repo</c>.</param>
/// <param name="ThunderstoreLoaderPackage">The <c>Namespace-Name</c> of the package that holds the game's BepInEx loader pack.</param>
/// <param name="ThunderstoreLoaderAliases">Other packages that are the same loader pack, which mods may depend on instead.</param>
public sealed record UnityGameDefinition(
    string Executable,
    string DataFolder,
    uint SteamAppId,
    string ThunderstoreCommunity,
    string ThunderstoreLoaderPackage,
    IReadOnlyList<string> ThunderstoreLoaderAliases);
