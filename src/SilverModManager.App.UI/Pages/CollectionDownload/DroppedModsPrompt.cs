using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Pages.CollectionDownload.Dialogs.DroppedMods;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.Collections;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;

namespace SilverModManager.App.UI.Pages.CollectionDownload;

/// <summary>
/// Asks the user what to do with the mods an update took out of the collection. They are already in the loadout's own
/// collection, so closing a dialog without choosing keeps them.
/// </summary>
public static class DroppedModsPrompt
{
    private static readonly ButtonDefinitionId KeepAllId = ButtonDefinitionId.From("keep-all");
    private static readonly ButtonDefinitionId ReviewId = ButtonDefinitionId.From("review");
    private static readonly ButtonDefinitionId RemoveAllId = ButtonDefinitionId.From("remove-all");
    private static readonly ButtonDefinitionId RemoveSelectedId = ButtonDefinitionId.From("remove-selected");

    /// <summary>The first question: keep, review or remove everything.</summary>
    public static IDialog Question(string collectionName, int revisionNumber, int count) => DialogFactory.CreateStandardDialog(
        Language.CollectionUpdate_DroppedMods_Title,
        new StandardDialogParameters { Text = string.Format(count == 1 ? Language.CollectionUpdate_DroppedMods_Text_One : Language.CollectionUpdate_DroppedMods_Text, count, collectionName, revisionNumber) },
        [
            new DialogButtonDefinition(Language.CollectionUpdate_DroppedMods_KeepAll, KeepAllId, ButtonAction.Reject, ButtonStyling.Default),
            new DialogButtonDefinition(Language.CollectionUpdate_DroppedMods_Review, ReviewId, ButtonAction.None, ButtonStyling.Default),
            new DialogButtonDefinition(Language.CollectionUpdate_DroppedMods_RemoveAll, RemoveAllId, ButtonAction.Accept, ButtonStyling.Destructive),
        ]
    );

    /// <summary>The checklist, every mod ticked.</summary>
    public static (IDialog Dialog, DialogDroppedModsReviewViewModel Checklist) Review(IReadOnlyList<DroppedMod> mods)
    {
        var checklist = new DialogDroppedModsReviewViewModel(mods);
        var dialog = DialogFactory.CreateDialog(
            Language.CollectionUpdate_DroppedMods_Title,
            [
                new DialogButtonDefinition(Language.CollectionUpdate_DroppedMods_KeepAll, KeepAllId, ButtonAction.Reject, ButtonStyling.Default),
                new DialogButtonDefinition(Language.CollectionUpdate_DroppedMods_RemoveSelected, RemoveSelectedId, ButtonAction.Accept, ButtonStyling.Destructive),
            ],
            checklist
        );
        return (dialog, checklist);
    }

    /// <summary>Shows the dialogs and returns the mods to remove: none when the user keeps them or closes a dialog.</summary>
    public static async Task<IReadOnlyList<DroppedMod>> Ask(IWindowManager windowManager, string collectionName, int revisionNumber, IReadOnlyList<DroppedMod> dropped)
    {
        if (dropped.Count == 0) return [];

        var answer = await windowManager.ShowDialog(Question(collectionName, revisionNumber, dropped.Count), DialogWindowType.Modal);
        if (answer.ButtonId == RemoveAllId) return dropped;
        if (answer.ButtonId != ReviewId) return [];

        var (dialog, checklist) = Review(dropped);
        var reviewed = await windowManager.ShowDialog(dialog, DialogWindowType.Modal);
        return reviewed.ButtonId == RemoveSelectedId ? checklist.Selected() : [];
    }
}
