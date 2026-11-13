namespace SilverModManager.Games.Larian.DivinityOriginalSin2DE;

/// <summary>
/// The modules Divinity: Original Sin 2 ships with: the five adventures, the base add-ons and the optional gift-bag
/// add-ons. A <c>modsettings.lsx</c> lists them next to mods; the game owns them, and the gift bags change gameplay
/// when they are on, so the app only ever recognises them in a file and never writes them unasked.
/// Taken from Divinity Mod Manager's <c>GUI/Resources/IgnoredMods.json</c> (its <c>Mods</c> list).
/// </summary>
public static class Dos2GameModules
{
    public static readonly IReadOnlySet<string> Uuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "1301db3d-1f54-4e98-9be5-5094030916e4", // Divinity: Original Sin 2 (Origins), adventure
        "2bd9bdbe-22ae-4aa2-9c93-205880fc6564", // Shared, adventure
        "eedf7638-36ff-4f26-a50a-076b87d53ba0", // Shared_DOS, adventure
        "00550ab2-ac92-410c-8d94-742f7629de0e", // Game Master, adventure
        "a99afe76-e1b0-43a1-98c2-0fd1448c223b", // Arena, adventure
        "b40e443e-badd-4727-82b3-f88a170c4db7", // Character_Creation_Pack
        "3da57b9d-8b41-46c7-a33c-afb31eea38a3", // ArmorSets
        "015de505-6e7f-460c-844c-395de6c2ce34", // Nine Lives
        "38608c30-1658-4f6a-8adf-e826a5295808", // Herb Gardens
        "1273be96-6a1b-4da9-b377-249b98dc4b7e", // Source Meditation
        "af4b3f9c-c5cb-438d-91ae-08c5804c1983", // From the Ashes
        "ec27251d-acc0-4ab8-920e-dbc851e79bb4", // Endless Runner
        "9b45f7e5-d4e2-4fc2-8ef7-3b8e90a5256c", // 8 Action Points
        "f33ded5d-23ab-4f0c-b71e-1aff68eee2cd", // Hagglers
        "68a99fef-d125-4ed0-893f-bb6751e52c5e", // Crafter's Kit
        "ca32a698-d63e-4d20-92a7-dd83cba7bc56", // Divine Talents
        "f30953bb-10d3-4ba4-958c-0f38d4906195", // Combat Randomiser
        "423fae51-61e3-469a-9c1f-8ad3fd349f02", // Animal Empathy
        "2d42113c-681a-47b6-96a1-d90b3b1b07d3", // Fort Joy Magic Mirror
        "8fe1719c-ef8f-4cb7-84bd-5a474ff7b6c1", // Enhanced Spirit Vision
        "a945eefa-530c-4bca-a29c-a51450f8e181", // Sourcerous Sundries
        "f243c84f-9322-43ac-96b7-7504f990a8f0", // Improved Organisation
        "d2507d43-efce-48b8-ba5e-5dd136c715a7", // Pet Power
    };
}
