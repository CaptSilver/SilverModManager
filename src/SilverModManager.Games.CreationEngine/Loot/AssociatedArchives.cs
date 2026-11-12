// Follows libloot src/archive/find.rs (find_associated_archives) and src/game.rs (archive_file_extension).
using SilverModManager.Games.Bethesda.Loot.Masterlists;

namespace SilverModManager.Games.CreationEngine.Loot;

/// <summary>Which of a game's archives a plugin loads, going by the names alone.</summary>
internal static class AssociatedArchives
{
    /// <summary>The extension the game's archives have; the other kind in Data is not looked at.</summary>
    public static string ArchiveExtension(LootGame game) => game switch
    {
        LootGame.Fallout4 or LootGame.Fallout4VR or LootGame.Starfield => ".ba2",
        _ => ".bsa",
    };

    /// <summary>
    /// The names in <paramref name="archiveNames"/> (file names in the Data folder) that the game loads for
    /// <paramref name="pluginName"/>. Names compare without case, as Windows file names do, which is where LOOT's
    /// rules come from.
    /// </summary>
    public static IReadOnlyList<string> Find(LootGame game, string pluginName, IEnumerable<string> archiveNames)
    {
        var extension = ArchiveExtension(game);
        var archives = archiveNames.Where(n => n.EndsWith(extension, StringComparison.OrdinalIgnoreCase)).ToList();
        var stem = Path.GetFileNameWithoutExtension(pluginName);

        return game switch
        {
            // Neither game has plugin-owned archives; Morrowind's are listed in its ini and OpenMW's in its config.
            LootGame.Morrowind or LootGame.OpenMW => [],

            // Only an archive with exactly the plugin's name.
            LootGame.Skyrim or LootGame.Enderal => Named(archives, stem, extension, [""]),

            // The same, and one with " - Textures" after the name. Skyrim VR is assumed to work like SE.
            LootGame.SkyrimSE or LootGame.SkyrimVR or LootGame.EnderalSE => Named(archives, stem, extension, ["", " - Textures"]),

            // Oblivion only loads archives for .esp plugins; Remastered follows the original.
            LootGame.Oblivion or LootGame.OblivionRemastered =>
                pluginName.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) ? StartingWith(archives, stem) : [],

            LootGame.Fallout3 or LootGame.FalloutNV or LootGame.Fallout4 or LootGame.Fallout4VR => StartingWith(archives, stem),

            // The voices archive is for the game's language, which is taken to be English.
            LootGame.Starfield => Named(archives, stem, extension, [" - Main", " - Textures", " - Localization", " - Voices_en"]),

            _ => throw new ArgumentOutOfRangeException(nameof(game), game, null),
        };
    }

    private static List<string> Named(List<string> archives, string stem, string extension, string[] suffixes) =>
        [.. suffixes.SelectMany(suffix => archives.Where(a => a.Equals(stem + suffix + extension, StringComparison.OrdinalIgnoreCase)))];

    // Any suffix is allowed after the plugin's name, so "Mod.esp" loads "Mod - Main.ba2" and "Modern.ba2" alike.
    private static List<string> StartingWith(List<string> archives, string stem) =>
        [.. archives.Where(a => a.StartsWith(stem, StringComparison.OrdinalIgnoreCase))];
}
