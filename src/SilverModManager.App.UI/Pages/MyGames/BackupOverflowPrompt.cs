using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;

namespace SilverModManager.App.UI.Pages.MyGames;

/// <summary>
/// What to tell the player when the files a sync would back up are more than the app copies on its own, and the
/// buttons that answer it. Kept apart from the dialog so the wording can be tested without a window.
/// </summary>
/// <remarks>
/// The title stays short and carries no numbers (the dialog trims what does not fit); the game, the count and the
/// size open the body. The body never calls the files mods and never names a command: that is the terminal's.
/// </remarks>
public sealed record BackupOverflowPrompt(string Title, string Markdown, DialogButtonDefinition[] Buttons)
{
    /// <summary>The button that keeps the files where they are.</summary>
    public static readonly ButtonDefinitionId Adopt = ButtonDefinitionId.From("adopt");

    /// <summary>The label of <see cref="Adopt"/>.</summary>
    public const string KeepLabel = "Keep them where they are";

    public static BackupOverflowPrompt For(string gameName, BackupOverflow overflow)
    {
        var title = TitleFor(overflow.Operation);
        var afterPlay = overflow.Operation == BackupOverflowOperation.AfterPlay;

        List<string> paragraphs = [overflow.Opening(gameName)];
        if (overflow.OnlyMustBackUp) paragraphs[0] += $" That is more than the {overflow.LimitText} the app copies without asking.";

        if (overflow.FolderList() is { Length: > 0 } folders) paragraphs.Add(folders);

        if (!overflow.OnlyMustBackUp)
        {
            paragraphs.Add($"The app keeps a copy of such files so it can put them back later, and does that by itself up to {overflow.LimitText}. These are {overflow.TotalText}, so it asks.");

            if (overflow.MustBackUpCount > 0)
            {
                paragraphs.Add(overflow.CanAdopt
                    ? $"{BackupOverflow.Files(overflow.MustBackUpCount)} ({overflow.MustBackUpText}) of them are deleted or replaced by this {overflow.OperationNoun} and are copied either way; keeping the rest where they are leaves {overflow.MustBackUpText} to back up."
                    : $"{BackupOverflow.Files(overflow.MustBackUpCount)} ({overflow.MustBackUpText}) of them are deleted or replaced by this {overflow.OperationNoun}, which is over {overflow.LimitText} by itself, so they cannot be kept where they are.");
            }
        }

        paragraphs.Add($"**Back up {overflow.TotalText}** copies {(overflow.FileCount == 1 ? "it" : "them")} now{BackUpTail(overflow.Operation)}. It needs that much free disk space and takes a while.");

        if (overflow.CanAdopt && overflow.Operation != BackupOverflowOperation.Reset)
        {
            paragraphs.Add($"**{KeepLabel}** copies nothing now. The app still copies a file before it deletes or replaces it (switching loadouts, cleaning the folder), and asks again then if that is over {overflow.LimitText}. Two things it cannot bring back: a file deleted outside the app, and these files if you remove the game and have its folder cleaned.");
        }

        paragraphs.Add(CancelSentence(overflow));

        List<DialogButtonDefinition> buttons = [];
        if (overflow.CanAdopt && overflow.Operation != BackupOverflowOperation.Reset) buttons.Add(new DialogButtonDefinition(KeepLabel, Adopt));
        buttons.Add(new DialogButtonDefinition($"Back up {overflow.TotalText}", ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Primary));
        if (!(afterPlay && overflow.CanAdopt)) buttons.Add(new DialogButtonDefinition(CancelLabel(overflow.Operation), ButtonDefinitionId.Cancel));

        return new BackupOverflowPrompt(title, string.Join("\n\n", paragraphs), buttons.ToArray());
    }

    /// <summary>The dialog title for what the player was doing. No numbers and no game name: the title is trimmed to fit.</summary>
    public static string TitleFor(BackupOverflowOperation operation) => operation switch
    {
        BackupOverflowOperation.Manage => "Back up files before adding this game?",
        BackupOverflowOperation.Apply => "Back up files before applying?",
        BackupOverflowOperation.Launch => "Back up files before starting the game?",
        BackupOverflowOperation.Switch => "Back up files before switching loadouts?",
        BackupOverflowOperation.Reset => "Back up files before cleaning the folder?",
        _ => "Back up files the game wrote?",
    };

    /// <summary>What the cancel button does, in the words of what the player was doing.</summary>
    public static string CancelLabel(BackupOverflowOperation operation) => operation switch
    {
        BackupOverflowOperation.Manage => "Don't add the game",
        BackupOverflowOperation.Apply => "Don't apply",
        BackupOverflowOperation.Launch => "Don't start the game",
        BackupOverflowOperation.Switch => "Stay on this loadout",
        BackupOverflowOperation.Reset => "Leave the folder as it is",
        _ => "Decide later",
    };

    private static string BackUpTail(BackupOverflowOperation operation) => operation switch
    {
        BackupOverflowOperation.Manage => ", then adds the game",
        BackupOverflowOperation.Apply => ", then applies the loadout",
        BackupOverflowOperation.Launch => ", then starts the game",
        BackupOverflowOperation.Switch => ", then switches",
        BackupOverflowOperation.Reset => ", then cleans the folder",
        _ => "",
    };

    private static string CancelSentence(BackupOverflow overflow) => overflow.Operation switch
    {
        BackupOverflowOperation.Manage => $"**{CancelLabel(overflow.Operation)}** changes nothing; the game is not added.",
        BackupOverflowOperation.Apply => $"**{CancelLabel(overflow.Operation)}** changes nothing; the loadout stays as it was on disk.",
        BackupOverflowOperation.Launch => $"**{CancelLabel(overflow.Operation)}** changes nothing; the game is not started.",
        BackupOverflowOperation.Switch => $"**{CancelLabel(overflow.Operation)}** changes nothing.",
        BackupOverflowOperation.Reset => $"**{CancelLabel(overflow.Operation)}** changes nothing; the files stay and show as External Changes in the loadout.",
        _ when overflow.CanAdopt => "Closing this window keeps them where they are.",
        _ => $"**{CancelLabel(overflow.Operation)}** leaves the loadout applied and these files as they are; the app asks again the next time you apply or switch.",
    };
}
