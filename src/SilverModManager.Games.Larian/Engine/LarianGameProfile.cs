using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Larian.Engine.Modsettings;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Larian.Engine;

/// <summary>How a game's script extender is installed and found.</summary>
/// <param name="DllPath">The extender's DLL in the game folder; a loadout holding it has the extender installed.</param>
/// <param name="Override">The Wine DLL override the DLL needs, or null when it loads without one.</param>
/// <param name="ConfigEntryName">The name of the extender's config inside a pak (<c>ScriptExtender/Config.json</c>).</param>
/// <param name="ConfigFolder">Where the extender keeps its settings, relative to the Larian folder.</param>
/// <param name="NexusModId">The extender's page on the game's Nexus Mods domain, named when a mod needs it and it is missing.</param>
public sealed record LarianScriptExtender(GamePath DllPath, WineDllOverride? Override, string ConfigEntryName, RelativePath ConfigFolder, uint NexusModId);

/// <summary>
/// Everything that differs between Larian games: where the game keeps its data, how its mod list is written, and
/// what its script extender is. <see cref="ALarianGame{TSelf}"/> builds the synchronizer, installers, sort order
/// and diagnostics from one of these.
/// </summary>
/// <param name="GameName">What the game is called in messages.</param>
/// <param name="LarianRoot">The known folder the Larian folder sits under.</param>
/// <param name="LarianFolder">The game's folder under <paramref name="LarianRoot"/>.</param>
/// <param name="ModsFolder">Where the game loads paks from, under the Larian folder.</param>
/// <param name="Profiles">Where the folder with <c>modsettings.lsx</c> is, under the Larian folder.</param>
/// <param name="GameData">The game's data folder, relative to the game folder; its paks are the game's own modules.</param>
/// <param name="Executables">The game's executables relative to the game folder: the primary one first, the engine's last.</param>
/// <param name="PakVersions">The LSPK versions the game's paks have.</param>
/// <param name="Schema">The dialect of the game's <c>modsettings.lsx</c>, or null when the app does not write one.</param>
/// <param name="GameModuleUuids">Modules the game ships, which mods list as dependencies without the loadout providing them.</param>
/// <param name="ScriptExtender">The game's script extender, or null when it has none.</param>
/// <param name="HasAdventureModules">Whether the game's mod list separates adventures from add-ons.</param>
/// <param name="VarietyId">The id the game's pak load order is stored under.</param>
/// <param name="GameInstalledRecognizer">Finds the paks the game installed itself, or null when there are none.</param>
/// <param name="ExtraModsSourceFolderNames">Folder names besides <c>Mods</c> that an archive may hold its paks under.</param>
public sealed record LarianGameProfile(
    string GameName,
    KnownPath LarianRoot,
    RelativePath LarianFolder,
    RelativePath ModsFolder,
    LarianProfileLayout Profiles,
    RelativePath GameData,
    IReadOnlyList<RelativePath> Executables,
    IReadOnlyList<int> PakVersions,
    IModsettingsSchema? Schema,
    IReadOnlySet<string> GameModuleUuids,
    LarianScriptExtender? ScriptExtender,
    bool HasAdventureModules,
    SortOrderVarietyId VarietyId,
    IGameInstalledContentRecognizer? GameInstalledRecognizer,
    IReadOnlyList<string> ExtraModsSourceFolderNames);
