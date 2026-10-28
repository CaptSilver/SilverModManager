using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;

namespace SilverModManager.App.UI.Pages.MyGames;

/// <summary>
/// What to tell the player when the files the first sync would back up are more than the app copies on its own,
/// and the buttons that answer it. Kept apart from the dialog so the wording can be tested without a window.
/// </summary>
public sealed record BackupOverflowPrompt(string Title, string Markdown, DialogButtonDefinition[] Buttons)
{
    /// <summary>The button that adopts the files where they are.</summary>
    public static readonly ButtonDefinitionId Adopt = ButtonDefinitionId.From("adopt");

    public static BackupOverflowPrompt For(string gameName, BackupOverflow overflow)
    {
        var title = overflow.Operation switch
        {
            BackupOverflowOperation.Switch => $"{gameName}: switching loadouts backs up {overflow.FileCount} files ({overflow.TotalText}), more than the app copies on its own",
            BackupOverflowOperation.Reset => $"{gameName}: putting the folder back backs up {overflow.FileCount} files ({overflow.TotalText}), more than the app copies on its own",
            _ => $"{gameName}: {overflow.FileCount} added files ({overflow.TotalText}) are more than the app backs up on its own",
        };

        var markdown = overflow.CanAdopt
            ? $"""
              The app copies a game's added files into its backup store so it can put them back later, and it does that on its own up to {overflow.LimitText}. These are {overflow.TotalText}.

              **Back up {overflow.TotalText}** copies all of them now. That can take a while and needs the disk space.

              **Adopt in place** manages them where they are and copies nothing now. The app copies a file before it deletes or replaces it (switching loadouts, cleaning the folder, removing the game) and asks again then if that is over {overflow.LimitText}. A file deleted outside the app is the one case it cannot restore. `silvermod loadout backup-game-files` backs them up at any time.
              """
            : $"""
              These {overflow.FileCount} files ({overflow.TotalText}) are about to be deleted or replaced, so they are backed up first, and that is more than the {overflow.LimitText} the app copies on its own. Backing them up can take a while and needs the disk space.
              """;

        if (overflow.CanAdopt && overflow.MustBackUpCount > 0)
        {
            markdown += $"""


                {overflow.MustBackUpCount} of them ({overflow.MustBackUpText}) are about to be deleted, replaced or rewritten and are backed up either way.
                """;
        }

        List<DialogButtonDefinition> buttons = [];
        if (overflow.CanAdopt) buttons.Add(new DialogButtonDefinition("Adopt in place", Adopt));
        buttons.Add(new DialogButtonDefinition($"Back up {overflow.TotalText}", ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Primary));
        buttons.Add(new DialogButtonDefinition(CancelLabel(overflow.Operation), ButtonDefinitionId.Cancel));

        return new BackupOverflowPrompt(title, markdown, buttons.ToArray());
    }

    /// <summary>What the cancel button stops: only a new game and an apply have a name for it.</summary>
    public static string CancelLabel(BackupOverflowOperation operation) => operation switch
    {
        BackupOverflowOperation.Manage => "Don't manage",
        BackupOverflowOperation.Apply => "Don't apply",
        _ => "Cancel",
    };
}
