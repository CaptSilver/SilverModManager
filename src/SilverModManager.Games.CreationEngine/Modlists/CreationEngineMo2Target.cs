using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.LoadOrder.Formats;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Games.CreationEngine.Models;
using SilverModManager.Games.CreationEngine.PluginLists;
using SilverModManager.Games.CreationEngine.SortOrder;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.CreationEngine.Modlists;

/// <summary>
/// How the Creation Engine titles answer <see cref="Abstractions.Games.Modlists.IMo2InstanceTarget"/>; the game
/// classes delegate here.
/// </summary>
internal static class CreationEngineMo2Target
{
    private const string OblivionRemasteredReason =
        "Oblivion Remastered modlists need per-file Unreal routing (Paks, UE4SS) that an MO2 layout does not express";

    // The names are what MO2's own game plugin lists in iniFiles(), because a profile folder holds exactly what MO2
    // wrote. Everything is in Preferences (My Games) unless the row says otherwise.
    private static readonly Dictionary<GameId, string[]> ProfileIniNames = new()
    {
        // game_skyrimse/src/gameskyrimse.cpp:283
        [SkyrimSE.SkyrimSE.GameId] = ["Skyrim.ini", "SkyrimPrefs.ini", "SkyrimCustom.ini"],
        // game_skyrimvr/src/gameskyrimvr.cpp:222
        [SkyrimVR.SkyrimVR.GameId] = ["SkyrimVR.ini", "SkyrimPrefs.ini"],
        // game_skyrim/src/gameskyrim.cpp:188
        [SkyrimLE.SkyrimLE.GameId] = ["Skyrim.ini", "SkyrimPrefs.ini"],
        // game_enderal/src/gameenderal.cpp:216
        [Enderal.Enderal.GameId] = ["Enderal.ini", "EnderalPrefs.ini"],
        // game_enderalse/src/gameenderalse.cpp:291
        [EnderalSE.EnderalSE.GameId] = ["Enderal.ini", "EnderalPrefs.ini"],
        // game_fallout4/src/gamefallout4.cpp:232
        [Fallout4.Fallout4.GameId] = ["Fallout4.ini", "Fallout4Prefs.ini", "Fallout4Custom.ini"],
        // game_fallout4vr/src/gamefallout4vr.cpp:178. Fallout4VR.TestFileIniFiles names Fallout4VR.ini and
        // Fallout4VRCustom.ini instead; a profile holds what MO2 wrote, so this follows MO2.
        [Fallout4VR.Fallout4VR.GameId] = ["Fallout4.ini", "Fallout4Prefs.ini", "Fallout4Custom.ini"],
        // game_starfield/src/gamestarfield.cpp:264 (Starfield.ini is not a profile file)
        [Starfield.Starfield.GameId] = ["StarfieldPrefs.ini", "StarfieldCustom.ini"],
        // game_oblivion/src/gameoblivion.cpp:173
        [Oblivion.Oblivion.GameId] = ["Oblivion.ini", "OblivionPrefs.ini"],
        // game_fallout3/src/gamefallout3.cpp:249
        [Fallout3.Fallout3.GameId] = ["Fallout.ini", "FalloutPrefs.ini", "FalloutCustom.ini", "GECKCustom.ini", "GECKPrefs.ini"],
        // game_falloutnv/src/gamefalloutnv.cpp:285
        [FalloutNewVegas.FalloutNewVegas.GameId] = ["Fallout.ini", "FalloutPrefs.ini", "FalloutCustom.ini", "GECKCustom.ini", "GECKPrefs.ini"],
        // game_morrowind/src/gamemorrowind.cpp:185; see ProfileSettingsFiles for where it lives.
        [Morrowind.Morrowind.GameId] = ["Morrowind.ini"],
        // Oblivion Remastered lists are refused, so it has no profile files.
        [OblivionRemastered.OblivionRemastered.GameId] = [],
    };

    public static string? UnsupportedReason(GameId game) =>
        game == OblivionRemastered.OblivionRemastered.GameId ? OblivionRemasteredReason : null;

    public static GamePath ModFilePath(ICreationEngineGame game, RelativePath modRelative) => game.DataPath / modRelative;

    public static IReadOnlyList<GamePath> ProfileSettingsFiles(GameId game)
    {
        if (!ProfileIniNames.TryGetValue(game, out var names))
            throw new InvalidOperationException($"No Mod Organizer profile files are known for game {game}.");

        // The ini lives in the game folder.
        if (game == Morrowind.Morrowind.GameId)
            return [new GamePath(LocationId.Game, "Morrowind.ini")];

        return names.Select(name => new GamePath(LocationId.Preferences, name)).ToList();
    }

    /// <summary>
    /// The INI files each loadout keeps its own copy of. Morrowind's is left out because its plugin list writer owns
    /// Morrowind.ini; a game with no profile files has none.
    /// </summary>
    public static IReadOnlyList<GamePath> OwnedIniFiles(GameId game) =>
        game == Morrowind.Morrowind.GameId || !ProfileIniNames.ContainsKey(game) ? [] : ProfileSettingsFiles(game);

    public static bool IsGameRootFolder(ICreationEngineGame game, GameInstallation installation, IReadOnlySet<RelativePath> filesDirectlyInside) =>
        filesDirectlyInside.Contains(game.GetPrimaryFile(installation).Path.FileName);

    /// <exception cref="ArgumentException">Neither text is given.</exception>
    public static void StagePluginList(
        ICreationEngineGame game,
        IPluginSortOrderVariety variety,
        Loadout.ReadOnly loadout,
        SortOrderId sortOrderId,
        string? pluginsTxt,
        string? loadorderTxt,
        ITransaction tx)
    {
        if (pluginsTxt is null && loadorderTxt is null)
            throw new ArgumentException("A profile with neither a plugins.txt nor a loadorder.txt has no plugin list to stage.");

        var installation = loadout.InstallationInstance;
        IReadOnlyList<PluginListEntry> active = pluginsTxt is null ? [] : game.PluginList(installation) is AsteriskMechanism
            ? AsteriskPluginList.Parse(pluginsTxt)
            : PlainPluginList.Parse(pluginsTxt);

        // loadorder.txt names every plugin in order; a plugin only plugins.txt mentions follows the listed ones.
        var order = loadorderTxt is null ? [] : LoadOrderTxt.Parse(loadorderTxt);
        var names = order.Concat(active.Select(entry => entry.Name)).Distinct(PluginNameComparer.Instance);

        // Without a plugins.txt nothing says which plugins are off, so the switches are left as they are.
        var switches = new Dictionary<string, bool>(PluginNameComparer.Instance);
        foreach (var entry in active) switches[entry.Name] = entry.Active;

        var listed = names.Select(name => (name, pluginsTxt is null ? null : (bool?)switches.GetValueOrDefault(name, false))).ToList();
        var early = game.EarlyLoadingPlugins(installation).ToHashSet(PluginNameComparer.Instance);
        variety.StageOrder(sortOrderId, PluginOrderMerge.Build(variety.StoredOrder(loadout), listed, early), tx);
        tx.Add(sortOrderId.Value, PluginSortOrder.SeededFromGameFile, true);
    }
}
