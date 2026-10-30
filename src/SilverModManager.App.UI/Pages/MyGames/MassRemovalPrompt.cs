using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;

namespace SilverModManager.App.UI.Pages.MyGames;

/// <summary>
/// What to tell the player when an apply would take most of the loadout's mod files out of the game folder, and the
/// buttons that answer it. Kept apart from the dialog so the wording can be tested without a window.
/// </summary>
/// <remarks>
/// The title stays short and carries no numbers (the dialog trims what does not fit); the body opens with the count
/// and names what is switched off. Enter presses Cancel: a player who does not follow the question must not delete
/// a mod setup by pressing it.
/// </remarks>
public sealed record MassRemovalPrompt(string Title, string Markdown, DialogButtonDefinition[] Buttons)
{
    public static MassRemovalPrompt For(string gameName, MassRemoval removal)
    {
        var markdown = removal.Opening(gameName)
            + "\n\n**Cancel** changes nothing; every file stays where it is. If you did not mean to switch something off, switch it back on and apply again.";

        DialogButtonDefinition[] buttons =
        [
            new("Cancel", ButtonDefinitionId.Cancel, ButtonAction.Accept, ButtonStyling.Primary),
            new($"Remove {MassRemoval.Files(removal.FileCount)}", ButtonDefinitionId.Accept),
        ];

        return new MassRemovalPrompt("Remove mod files from the game folder?", markdown, buttons);
    }
}
