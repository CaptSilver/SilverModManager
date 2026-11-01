using System.Collections.Immutable;
using NexusMods.Paths;
using NexusMods.Paths.Utilities;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.Generic.Simple;

/// <summary>
/// Simple games whose mods go in a folder under Documents or AppData, read from the same plugins and extensions as
/// <see cref="GameFolderSimpleGames"/>. The folder is a Windows path, so these games run under Proton and have no
/// native Linux executable: a native build keeps its data in the user's Linux folders.
/// </summary>
public static class KnownFolderSimpleGames
{
    // The location ends at the mod folder, so the player's name for it is the folder's own.
    private static IReadOnlyDictionary<string, string> Named(ModLocationRoot root, string name) =>
        ImmutableDictionary.CreateRange([KeyValuePair.Create(SimpleLocations.For(root).ToString(), name)]);

    /// <summary>From Vortex.</summary>
    public static readonly SimpleGameDefinition LegendOfGrimrock = new()
    {
        GameId = GameId.From("Simple.LegendOfGrimrock"),
        DisplayName = "Legend of Grimrock",
        NexusModsGameId = NexusModsGameId.From(161),
        Stores = new StoreIdentifiers(GameId.From("Simple.LegendOfGrimrock")) { SteamAppIds = [207170] },
        Executable = "grimrock.exe",
        ModRoot = new ModLocation(ModLocationRoot.Documents, "Almost Human/Legend of Grimrock/Dungeons"),
        LocationNames = Named(ModLocationRoot.Documents, "Dungeons"),
        LocationSentenceNames = Named(ModLocationRoot.Documents, "the Dungeons folder"),
        Install = new SimpleInstallRules { InstallAsIs = true },
    };

    /// <summary>From Mod Organizer 2.</summary>
    public static readonly SimpleGameDefinition BindingOfIsaacRebirth = new()
    {
        GameId = GameId.From("Simple.BindingOfIsaacRebirth"),
        DisplayName = "The Binding of Isaac: Rebirth",
        NexusModsGameId = NexusModsGameId.From(1293),
        Stores = new StoreIdentifiers(GameId.From("Simple.BindingOfIsaacRebirth")) { SteamAppIds = [250900] },
        Executable = "isaac-ng.exe",
        ModRoot = new ModLocation(ModLocationRoot.Documents, "My Games/Binding of Isaac Afterbirth+ Mods"),
        LocationNames = Named(ModLocationRoot.Documents, "Binding of Isaac mods"),
        LocationSentenceNames = Named(ModLocationRoot.Documents, "the Binding of Isaac mods folder"),
        Install = new SimpleInstallRules { InstallAsIs = true },
    };

    /// <remarks>
    /// From Vortex. A mod is a folder holding a modcontent.hpk. GOG and Epic installs are not matched: their
    /// executables are MarsGOG.exe and MarsEpic.exe, not this one.
    /// </remarks>
    public static readonly SimpleGameDefinition SurvivingMars = new()
    {
        GameId = GameId.From("Simple.SurvivingMars"),
        DisplayName = "Surviving Mars",
        NexusModsGameId = NexusModsGameId.From(2347),
        Stores = new StoreIdentifiers(GameId.From("Simple.SurvivingMars")) { SteamAppIds = [464920] },
        Executable = "MarsSteam.exe",
        ModRoot = new ModLocation(ModLocationRoot.RoamingAppData, "Surviving Mars/mods"),
        LocationNames = Named(ModLocationRoot.RoamingAppData, "Surviving Mars mods"),
        LocationSentenceNames = Named(ModLocationRoot.RoamingAppData, "the Surviving Mars mods folder"),
        Install = new SimpleInstallRules
        {
            MarkerRules =
            [
                new SimpleMarkerRule
                {
                    MarkerFile = "modcontent.hpk",
                    Destination = RelativePath.Empty,
                    InFolderNamedAfterMod = true,
                    IgnoredExtensions = [KnownExtensions.Txt, KnownExtensions.Md, KnownExtensions.Png, KnownExtensions.Jpg],
                },
            ],
        },
    };

    public static readonly IReadOnlyList<SimpleGameDefinition> All = [LegendOfGrimrock, BindingOfIsaacRebirth, SurvivingMars];
}
