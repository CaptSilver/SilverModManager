using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.Witcher.Witcher3.SortOrder;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Games.Witcher.Witcher3;

/// <summary>The command line's view of The Witcher 3's generated files. It only reads.</summary>
internal static class Witcher3Verbs
{
    internal const string ModsSettingsName = "witcher3 mods-settings";

    [Verb(ModsSettingsName, "Prints the mods.settings the next Apply would write for a loadout")]
    internal static async Task<int> ModsSettings(
        [Injected] IRenderer renderer,
        [Option("l", "loadout", "The loadout to print the file for")] Loadout.ReadOnly loadout)
    {
        var variety = (loadout.InstallationInstance.GetGame() as Witcher3Game)?.SortOrderManager
            .GetSortOrderVarieties().OfType<IWitcherModSortOrderVariety>().SingleOrDefault();
        if (variety is null)
        {
            await renderer.TextLine($"{loadout.Name} is a loadout for {loadout.InstallationInstance.Game.DisplayName}, which has no mods.settings. Pick a loadout for The Witcher 3.");
            return 1;
        }

        // The file keeps its CRLF endings; a trailing newline would be a byte the game's file does not have.
        await renderer.Text(ModsSettingsDocument.Render(variety.StoredOrder(loadout)));
        return 0;
    }
}
