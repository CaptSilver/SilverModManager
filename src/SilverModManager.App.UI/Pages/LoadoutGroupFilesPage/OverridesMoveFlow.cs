using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;

namespace SilverModManager.App.UI.Pages.LoadoutGroupFilesPage;

/// <summary>
/// The dialogs and calls behind the External Changes "Move to new mod" and "Move to existing mod" commands:
/// ask where the files go, show what the move would replace or stop winning, then hand the move to
/// <see cref="IOverridesMover"/>. A cancelled dialog ends the flow without touching anything.
/// </summary>
internal sealed class OverridesMoveFlow(IServiceProvider provider, IWindowManager windows, IConnection connection)
{
    private static readonly DialogButtonDefinition MoveButton = new(
        Language.ExternalChanges_Move_Button, ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Primary);

    private IOverridesMover Mover => provider.GetRequiredService<IOverridesMover>();

    /// <summary>
    /// Asks for a name, adds an empty mod with it and moves the files in. The preview needs the mod to exist, so
    /// it is added once the name is confirmed and removed again if the user then backs out or the move is refused:
    /// either way no empty mod is left behind.
    /// </summary>
    /// <param name="selected">What the user selected; the first entry names the proposed mod.</param>
    /// <param name="paths">The files to move, with folders already expanded.</param>
    public async Task MoveToNewMod(LoadoutId loadoutId, GamePath[] selected, GamePath[] paths)
    {
        var dialog = DialogFactory.CreateStandardDialog(
            Language.ExternalChanges_Move_NewMod_Title,
            new StandardDialogParameters
            {
                Text = Language.ExternalChanges_Move_NewMod_Text,
                InputLabel = Language.ExternalChanges_Move_NewMod_InputLabel,
                InputWatermark = Language.ExternalChanges_Move_NewMod_InputWatermark,
                InputText = ProposeModName(selected[0]),
            },
            [DialogStandardButtons.Cancel, MoveButton],
            DialogWindowSize.Small);

        var answer = await windows.ShowDialog(dialog, DialogWindowType.Modal);
        if (answer.ButtonId != ButtonDefinitionId.Accept) return;

        var name = answer.InputText.Trim();
        if (name.Length == 0) throw new OverridesMoveRefusedException("A mod needs a name, so the files were not moved.");

        var target = await Mover.CreateEmptyGroup(loadoutId, name);
        var moved = false;
        try
        {
            // A new mod sits at the top of the order and holds nothing yet, so there is usually nothing to
            // confirm; a path pinned to another mod is the exception.
            moved = await ConfirmAndMove(loadoutId, paths, target, onlyWhenSomethingToReport: true);
        }
        finally
        {
            if (!moved) await provider.GetRequiredService<ILoadoutManager>().RemoveItems([target]);
        }
    }

    /// <summary>Asks which mod receives the files, shows what the move would do, then moves them.</summary>
    /// <param name="paths">The files to move, with folders already expanded.</param>
    public async Task MoveToExistingMod(LoadoutId loadoutId, GamePath[] paths)
    {
        var mods = ModsOf(loadoutId);
        if (mods.Length == 0)
            throw new OverridesMoveRefusedException("This loadout has no mods to move the files into. Use \"Move to new mod\" instead.");

        var picker = new MoveToModDialogViewModel(Language.ExternalChanges_Move_Existing_Text, mods, string.Empty, canKeepWinning: false);
        picker.SelectedMod = picker.Mods.FirstOrDefault();
        var pickerDialog = DialogFactory.CreateDialog(
            Language.ExternalChanges_Move_Existing_Title, [DialogStandardButtons.Cancel, MoveButton], picker, DialogWindowSize.Small);

        var answer = await windows.ShowDialog(pickerDialog, DialogWindowType.Modal);
        if (answer.ButtonId != ButtonDefinitionId.Accept || picker.SelectedMod is null) return;

        await ConfirmAndMove(loadoutId, paths, picker.SelectedMod.Id, onlyWhenSomethingToReport: false);
    }

    /// <summary>Previews the move, asks the user to confirm it and moves the files. False when the user backed out.</summary>
    private async Task<bool> ConfirmAndMove(LoadoutId loadoutId, GamePath[] paths, LoadoutItemGroupId target, bool onlyWhenSomethingToReport)
    {
        var previews = Mover.Preview(loadoutId, paths, target);
        var keepWinning = false;
        if (!onlyWhenSomethingToReport || HasSomethingToReport(previews))
        {
            var confirmed = await Confirm(target, previews);
            if (confirmed is null) return false;
            keepWinning = confirmed.Value;
        }

        await Mover.Move(loadoutId, paths, target, new MoveOptions { PinMovedPaths = keepWinning });
        return true;
    }

    /// <summary>Shows the preview; the answer is the checkbox, or null when the user backed out.</summary>
    private async Task<bool?> Confirm(LoadoutItemGroupId target, OverridesMovePreview[] previews)
    {
        var targetName = NameOf(target);
        var losers = previews.Where(Loses).ToArray();
        var content = new MoveToModDialogViewModel(
            string.Format(Language.ExternalChanges_Move_Confirm_Text, targetName),
            [],
            Describe(targetName, previews),
            canKeepWinning: losers.Length > 0);
        var dialog = DialogFactory.CreateDialog(
            string.Format(Language.ExternalChanges_Move_Confirm_Title, targetName),
            [DialogStandardButtons.Cancel, MoveButton],
            content,
            DialogWindowSize.Medium);

        var answer = await windows.ShowDialog(dialog, DialogWindowType.Modal);
        return answer.ButtonId == ButtonDefinitionId.Accept ? content.KeepWinning : null;
    }

    private static bool Loses(OverridesMovePreview preview) => preview is { InOverrides: true, WinsAfterMove: false };

    private static bool HasSomethingToReport(OverridesMovePreview[] previews) =>
        previews.Any(p => p.InOverrides && (p.TargetHasPath || Loses(p)));

    /// <summary>The folder at the top of the first file's path, or the file's own name for a file in the game folder.</summary>
    internal static string ProposeModName(GamePath first)
    {
        var parts = first.Path.Parts.ToArray();
        return parts.Length > 1 ? parts[0].ToString() : System.IO.Path.GetFileNameWithoutExtension(first.Path.FileName.ToString());
    }

    private string Describe(string targetName, OverridesMovePreview[] previews)
    {
        var lines = new List<string>();
        foreach (var preview in previews.Where(p => p.InOverrides && p.TargetHasPath))
        {
            lines.Add(string.Format(Language.ExternalChanges_Move_Duplicate,
                preview.Path.Path, targetName, HashText(preview.TargetHash), HashText(preview.Hash)));
        }

        foreach (var group in previews.Where(Loses).GroupBy(p => p.WinnerAfterMove))
        {
            if (lines.Count > 0) lines.Add(string.Empty);
            lines.Add(group.Key is { } winner
                ? string.Format(Language.ExternalChanges_Move_Loses, targetName, NameOf(winner))
                : string.Format(Language.ExternalChanges_Move_Loses_NoWinner, targetName));
            lines.AddRange(group.Select(p => "  " + p.Path.Path));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string HashText(Hash? hash) => hash is { } value ? value.ToHex() : Language.ExternalChanges_Move_Deleted;

    private string NameOf(LoadoutItemGroupId group) => LoadoutItemGroup.Load(connection.Db, group).AsLoadoutItem().Name;

    /// <summary>
    /// The loadout's mods: every group except External Changes, collections themselves and the game's own
    /// content. A mod inside a collection counts unless that collection is read-only.
    /// </summary>
    private MoveTargetChoice[] ModsOf(LoadoutId loadoutId)
    {
        var db = connection.Db;
        var mods = new List<MoveTargetChoice>();
        foreach (var datom in db.Datoms(LoadoutItemGroup.Group, Null.Instance))
        {
            var group = LoadoutItemGroup.Load(db, datom.E);
            var item = group.AsLoadoutItem();
            if (item.LoadoutId != loadoutId) continue;
            if (group.TryGetAsLoadoutOverridesGroup(out _) || group.TryGetAsCollectionGroup(out _) || group.TryGetAsGameInstalledItem(out _)) continue;

            if (item.HasParent())
            {
                if (!item.Parent.TryGetAsCollectionGroup(out var collection) || collection.IsReadOnly) continue;
            }

            mods.Add(new MoveTargetChoice(group.LoadoutItemGroupId, item.Name));
        }

        return mods.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
