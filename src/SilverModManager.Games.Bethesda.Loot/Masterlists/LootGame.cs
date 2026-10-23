namespace NexusMods.Games.Bethesda.Loot.Masterlists;

/// <summary>
/// The games LOOT has metadata for. Several games share a masterlist repository.
/// </summary>
public enum LootGame
{
    Morrowind,
    OpenMW,
    Oblivion,
    OblivionRemastered,
    Skyrim,
    SkyrimSE,
    SkyrimVR,
    Fallout3,
    FalloutNV,
    Fallout4,
    Fallout4VR,
    Starfield,
    Enderal,
    EnderalSE,
}

public static class LootGameExtensions
{
    /// <summary>
    /// Repositories under github.com/loot that have a masterlist on the bundled branch. The
    /// skyrimvr, fallout4vr and openmw repositories have none, which is why those games share a sibling's.
    /// </summary>
    public static IReadOnlyList<string> BundledRepositories { get; } =
    [
        "morrowind", "oblivion", "oblivion-remastered", "skyrim", "skyrimse",
        "fallout3", "falloutnv", "fallout4", "starfield", "enderal",
    ];

    /// <summary>
    /// The loot/&lt;repo&gt; repository whose masterlist applies to <paramref name="game"/>.
    /// </summary>
    public static string MasterlistRepository(this LootGame game) => game switch
    {
        LootGame.Morrowind or LootGame.OpenMW => "morrowind",
        LootGame.Oblivion => "oblivion",
        LootGame.OblivionRemastered => "oblivion-remastered",
        LootGame.Skyrim => "skyrim",
        LootGame.SkyrimSE or LootGame.SkyrimVR => "skyrimse",
        LootGame.Fallout3 => "fallout3",
        LootGame.FalloutNV => "falloutnv",
        LootGame.Fallout4 or LootGame.Fallout4VR => "fallout4",
        LootGame.Starfield => "starfield",
        LootGame.Enderal or LootGame.EnderalSE => "enderal",
        _ => throw new ArgumentOutOfRangeException(nameof(game), game, null),
    };
}
